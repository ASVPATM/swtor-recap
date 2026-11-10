using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using SwtorRecap.Engine;

namespace SwtorRecap.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsGameProcesses(string processName="swtor") : IGameProcessSource
{
    private readonly int _session=CurrentSession();
    private static int CurrentSession(){using var process=Process.GetCurrentProcess();return process.SessionId;}
    public IReadOnlyList<ObservedProcess> Discover()
    {
        var result=new List<ObservedProcess>();
        using var identity=WindowsIdentity.GetCurrent();var user=identity.User!.Value;
        foreach(var process in Process.GetProcessesByName(processName))
        using(process)
        {
            try
            {
                if(process.SessionId!=_session||process.HasExited)continue;
                DateTimeOffset? started=null;try{started=process.StartTime.ToUniversalTime();}catch(Win32Exception){}
                if(!process.HasExited)result.Add(new(process.Id,user+":"+_session,started,DateTimeOffset.UtcNow));
            }
            catch(Exception ex) when(ex is InvalidOperationException or Win32Exception){}
        }
        return result;
    }
    public async Task WaitForExitAsync(ObservedProcess process,CancellationToken cancellationToken)
    {
        try{using var p=Process.GetProcessById(process.Pid);await p.WaitForExitAsync(cancellationToken);}
        catch(ArgumentException){}catch(Win32Exception){await Task.Delay(TimeSpan.FromSeconds(3),cancellationToken);}
    }
}
[SupportedOSPlatform("windows")]
public sealed class WindowsAwakeClock : IAwakeClock
{
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool QueryUnbiasedInterruptTime(out ulong value);
    public long Milliseconds=>QueryUnbiasedInterruptTime(out var value)?checked((long)(value/10000)):throw new Win32Exception(Marshal.GetLastWin32Error());
}
[SupportedOSPlatform("windows")]
public static class WindowsFileIdentity
{
    [StructLayout(LayoutKind.Sequential)]private struct FileIdInfo{public ulong Volume;public ulong Low;public ulong High;}
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle file,int infoClass,out FileIdInfo info,uint size);
    public static string? Get(FileStream file)=>GetFileInformationByHandleEx(file.SafeFileHandle,18,out var info,(uint)Marshal.SizeOf<FileIdInfo>())?$"{info.Volume:x16}:{info.Low:x16}{info.High:x16}":null;
    public static string CombatLogFolder=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Star Wars - The Old Republic","CombatLogs");
}
[SupportedOSPlatform("windows")]
public sealed class WindowsStartup(string agentPath,string valueName="SwtorRecap") : IStartupRegistration
{
    private const string RegistryPath=@"Software\Microsoft\Windows\CurrentVersion\Run";
    private string Command
    {
        get
        {
            if(!Path.IsPathFullyQualified(agentPath)||agentPath.Contains('"'))throw new ArgumentException("Startup executable must have a valid absolute path.");
            var command="\""+agentPath+"\"";
            if(command.Length>260)throw new ArgumentException("The startup command exceeds the Windows Run-key limit. Move the portable package to a shorter path.");return command;
        }
    }
    public bool IsEnabled {get{using var key=Registry.CurrentUser.OpenSubKey(RegistryPath);return key?.GetValue(valueName) as string==Command;}}
    public void SetEnabled(bool enabled)
    {
        using var key=Registry.CurrentUser.CreateSubKey(RegistryPath,true);
        if(enabled)key.SetValue(valueName,Command,RegistryValueKind.String);else key.DeleteValue(valueName,false);
        if(IsEnabled!=enabled)throw new IOException("Windows startup registration did not match the requested setting.");
    }
}
