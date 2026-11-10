using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SwtorRecap.Core;
using SwtorRecap.Engine;

namespace SwtorRecap.Platform.Windows;

[SupportedOSPlatform("windows")]
public static class LocalPipe
{
    [StructLayout(LayoutKind.Sequential)]private struct SecurityAttributes{public int Length;public IntPtr Descriptor;[MarshalAs(UnmanagedType.Bool)]public bool Inherit;}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl,uint revision,out IntPtr descriptor,out uint size);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafePipeHandle CreateNamedPipe(string name,uint openMode,uint pipeMode,uint instances,uint outSize,uint inSize,uint timeout,ref SecurityAttributes security);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint pid);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool OpenProcessToken(IntPtr process,uint access,out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool GetTokenInformation(SafeAccessTokenHandle token,int infoClass,out int value,int length,out int returned);
    public static NamedPipeServerStream Create(string name)
    {
        using var identity=WindowsIdentity.GetCurrent();var sid=identity.User!.Value;
        if(!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(D;;GA;;;NU)(A;;GA;;;{sid})",1,out var descriptor,out _))throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes=new SecurityAttributes{Length=Marshal.SizeOf<SecurityAttributes>(),Descriptor=descriptor,Inherit=false};
            // Byte mode + PIPE_REJECT_REMOTE_CLIENTS; only current user's DACL. No TCP listener.
            var handle=CreateNamedPipe(@"\\.\pipe\"+name,3|0x40000000,0x8,8,65536,65536,0,ref attributes);
            if(handle.IsInvalid){handle.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}
            return new NamedPipeServerStream(PipeDirection.InOut,true,false,handle);
        }
        finally{LocalFree(descriptor);}
    }
    public static void VerifyPeer(NamedPipeServerStream pipe)
    {
        if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var pid))throw new Win32Exception(Marshal.GetLastWin32Error());
        using var peer=Process.GetProcessById(checked((int)pid));using var self=Process.GetCurrentProcess();using var selfIdentity=WindowsIdentity.GetCurrent();
        if(peer.SessionId!=self.SessionId)throw new UnauthorizedAccessException("The agent belongs to another interactive login session.");
        if(!OpenProcessToken(peer.Handle,8,out var token))throw new Win32Exception(Marshal.GetLastWin32Error());
        using(token)
        using(var identity=new WindowsIdentity(token.DangerousGetHandle()))
        {
            if(identity.User!=selfIdentity.User)throw new UnauthorizedAccessException("Pipe peer is a different user.");
            if(!GetTokenInformation(token,20,out var elevated,4,out _))throw new Win32Exception(Marshal.GetLastWin32Error());
            if(!OpenProcessToken(self.Handle,8,out var mine))throw new Win32Exception(Marshal.GetLastWin32Error());
            using(mine){if(!GetTokenInformation(mine,20,out var myElevation,4,out _)||elevated!=myElevation)throw new UnauthorizedAccessException("Open both tracker processes unelevated under the same user.");}
        }
    }
    public static async Task ServeAsync(string name,TrackerService service,CancellationToken cancellationToken)
    {
        using var capacity=new SemaphoreSlim(4,4);var clients=new List<Task>();
        try
        {
            while(!cancellationToken.IsCancellationRequested)
            {
                await capacity.WaitAsync(cancellationToken);var pipe=Create(name);
                try{await pipe.WaitForConnectionAsync(cancellationToken);VerifyPeer(pipe);}
                catch(Exception ex) when(ex is UnauthorizedAccessException or Win32Exception or ArgumentException){pipe.Dispose();capacity.Release();continue;}
                catch{pipe.Dispose();capacity.Release();throw;}
                clients.RemoveAll(t=>t.IsCompleted);
                clients.Add(Task.Run(async()=>
                {
                    using(pipe)
                    using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(30));
                        try{var request=await FrameProtocol.ReadAsync<IpcRequest>(pipe,timeout.Token);if(request.Command is "Rebuild" or "AttributeOwner")timeout.CancelAfter(TimeSpan.FromMinutes(10));var response=await service.HandleAsync(request,timeout.Token);await FrameProtocol.WriteAsync(pipe,response,timeout.Token);}
                        catch(Exception ex) when(ex is IOException or OperationCanceledException or JsonException or UnauthorizedAccessException){}
                        finally{capacity.Release();}
                    }
                },CancellationToken.None));
            }
        }
        finally{await Task.WhenAll(clients);}
    }
}

[SupportedOSPlatform("windows")]
public sealed class PipeTrackerClient(string dataRoot,string agentPath,bool isolated=false) : ITrackerClient,ITrackerReconnect,ITrackerVersionRecovery
{
    private string? _epoch;
    private long _revision=-1;
    private readonly SemaphoreSlim _requests=new(1,1);
    private readonly CancellationTokenSource _closed=new();
    private bool _mayLaunch=true;
    public void PermitAgentLaunch()=>_mayLaunch=true;
    public async Task RestartMatchingAgentAsync(CancellationToken token)
    {
        if(new WindowsGameProcesses().Discover().Count>0)throw new InvalidOperationException("Close SWTOR before restarting the tracker.");
        await _requests.WaitAsync(token);
        try
        {
            try{await Send<MutationResult>("Quit",null,token);}catch(IOException){} // An older tracker can close its pipe before acknowledging Quit.
            for(var i=0;i<40;i++)
            {
                try{using var guard=new WriterGuard(dataRoot);_epoch=null;_revision=-1;_mayLaunch=true;return;}
                catch(IOException){await Task.Delay(250,token);}
            }
            throw new IOException("The previous tracker is still stopping. Try again shortly.");
        }
        finally{_requests.Release();}
    }
    public async Task<T> RequestAsync<T>(string command,object? parameters=null,CancellationToken cancellationToken=default)
    {
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,_closed.Token);cancellationToken=lifetime.Token;
        await _requests.WaitAsync(cancellationToken);
        try
        {
            if(_epoch is null&&command!="Snapshot")await Send<TrackerSnapshot>("Snapshot",null,cancellationToken);
            return await Send<T>(command,parameters,cancellationToken);
        }
        finally{_requests.Release();}
    }
    private async Task<T> Send<T>(string command,object? parameters,CancellationToken token)
    {
        var id=Guid.NewGuid().ToString("N");Exception? last=null;
        for(var attempt=0;attempt<3;attempt++)
        {
            try
            {
                using var pipe=new NamedPipeClientStream(".",DataPaths.PipeName(dataRoot),PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(1200,token);
                await FrameProtocol.WriteAsync(pipe,new IpcRequest(Versions.Protocol,id,command,JsonSerializer.SerializeToElement(parameters??new QueryFilter(),Json.Options),_epoch),token);
                var response=await FrameProtocol.ReadAsync<IpcResponse>(pipe,token);
                _mayLaunch=false;
                if(response.ProtocolVersion!=Versions.Protocol||response.RequestId!=id)throw new InvalidDataException("Unexpected agent response.");
                if(!response.Success)throw new InvalidOperationException(response.Error);
                if(_epoch!=response.Revision.HistoryEpoch){_epoch=response.Revision.HistoryEpoch;_revision=-1;}
                if(response.Revision.DataRevision<_revision)throw new InvalidDataException("Stale history response.");
                _revision=response.Revision.DataRevision;
                var payload=response.Payload.Deserialize<T>(Json.Options)??throw new InvalidDataException("Empty response.");
                if(payload is TrackerSnapshot snapshot&&snapshot.AppVersion!=Versions.App)throw new TrackerVersionException(snapshot.AppVersion);
                return payload;
            }
            catch(Exception ex) when(ex is TimeoutException or IOException)
            {
                last=ex;
                if(attempt==0&&_mayLaunch)
                {
                    _mayLaunch=false;
                    if(!File.Exists(agentPath))throw new FileNotFoundException("The installed agent is missing. Extract the complete portable package.",agentPath);
                    var launch=new ProcessStartInfo(agentPath){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(agentPath)!};
                    if(isolated){launch.ArgumentList.Add("--test-data-dir");launch.ArgumentList.Add(dataRoot);}
                    Process.Start(launch);
                }
                await Task.Delay(400*(attempt+1),token);
            }
        }
        throw new IOException("The tracker is unavailable. Run both processes as the same unelevated user; check for another Windows login session owning history.",last);
    }
    public ValueTask DisposeAsync(){_closed.Cancel();return ValueTask.CompletedTask;}
}
