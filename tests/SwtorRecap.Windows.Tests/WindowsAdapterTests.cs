using System.Diagnostics;
using System.IO.Pipes;
using SwtorRecap.Engine;
using SwtorRecap.Platform.Windows;
using SwtorRecap.Storage;

namespace SwtorRecap.Windows.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{public WindowsFactAttribute(){if(!OperatingSystem.IsWindows())Skip="Requires native Windows; not validated by macOS compilation.";}}
public sealed class WindowsAdapterTests
{
    [Fact]public void TrayVersionFourUsesSelectionEventsRatherThanBalloonDismissal()
    {
        Assert.True(NativeTrayCallbacks.OpensDashboard((1L<<16)|0x400));
        Assert.True(NativeTrayCallbacks.OpensDashboard((1L<<16)|0x401));
        Assert.False(NativeTrayCallbacks.OpensDashboard((1L<<16)|0x403));
    }
    [WindowsFact]public void NativeSqliteAndSharedFileIdentityWork()
    {
        if(!OperatingSystem.IsWindows())return;
        var root=Path.Combine(Path.GetTempPath(),"swtor-windows-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var store=new RecapStore(root,"UTC");Assert.True(Version.Parse(store.SqliteVersion)>=new Version(3,51,3));
            var path=Path.Combine(root,"log.txt");File.WriteAllText(path,"one\n");using var reader=ByteFramer.OpenRead(path);var id=WindowsFileIdentity.Get(reader);Assert.NotNull(id);
            File.Move(path,path+".renamed");File.AppendAllText(path+".renamed","two\n");using var reopened=ByteFramer.OpenRead(path+".renamed");Assert.Equal(id,WindowsFileIdentity.Get(reopened));
        }
        finally{Directory.Delete(root,true);}
    }
    [WindowsFact]public async Task LocalPipeVerifiesSameUserAndFrame()
    {
        if(!OperatingSystem.IsWindows())return;
        var name="SwtorRecap.test."+Guid.NewGuid().ToString("N");using var server=LocalPipe.Create(name);using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        var accept=server.WaitForConnectionAsync();await client.ConnectAsync(3000);await accept;LocalPipe.VerifyPeer(server);
        await FrameProtocol.WriteAsync(client,"hello",CancellationToken.None);Assert.Equal("hello",await FrameProtocol.ReadAsync<string>(server,CancellationToken.None));
    }
    [WindowsFact]public void StartupRoundTripTouchesOnlyTemporaryApplicationValue()
    {
        if(!OperatingSystem.IsWindows())return;
        var registration=new WindowsStartup(Environment.ProcessPath!,"SwtorRecap.Test."+Guid.NewGuid().ToString("N"));
        try{registration.SetEnabled(true);Assert.True(registration.IsEnabled);registration.SetEnabled(false);Assert.False(registration.IsEnabled);}
        finally{registration.SetEnabled(false);}
    }
    [WindowsFact]public async Task BenignFixtureProcessCanBeDiscoveredAndExited()
    {
        if(!OperatingSystem.IsWindows())return;
        // A unique copy avoids discovering another person's terminal or game process.
        var root=Path.Combine(Path.GetTempPath(),"swtor-process-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var exe=Path.Combine(root,"swtor-fixture-"+Guid.NewGuid().ToString("N")+".exe");File.Copy(Path.Combine(Environment.SystemDirectory,"cmd.exe"),exe);
        try
        {
            using var process=Process.Start(new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardInput=true,CreateNoWindow=true})!;
            var adapter=new WindowsGameProcesses(Path.GetFileNameWithoutExtension(exe));var observed=Assert.Single(adapter.Discover());Assert.Equal(process.Id,observed.Pid);
            var exit=adapter.WaitForExitAsync(observed,CancellationToken.None);await process.StandardInput.WriteLineAsync("exit");await process.StandardInput.FlushAsync();await exit.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally{Directory.Delete(root,true);}
    }
}
