using System.Diagnostics;
using System.Text.Json;
using SwtorRecap.Core;
using SwtorRecap.Engine;
using SwtorRecap.Platform.Windows;
using SwtorRecap.Storage;

namespace SwtorRecap.Agent;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if(!OperatingSystem.IsWindows()){Console.Error.WriteLine("The tracking agent requires Windows. Use the replay tool or isolated desktop preview on this platform.");return 2;}
        try{return RunAsync(args).GetAwaiter().GetResult();}
        catch(Exception ex){Console.Error.WriteLine(ex.Message);return 1;}
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<int> RunAsync(string[] args)
    {
        if(args.Length==2&&args[0]=="--apply-update")return await UpdateInstaller.ApplyAsync(args[1]);
        if(UpdateInstaller.IsRunning())return 0;
        var test=Array.IndexOf(args,"--test-data-dir");
        var root=test>=0?DataPaths.Isolated(test+1<args.Length?args[test+1]:null):DataPaths.Production();
        using var installerMutex=test>=0?null:new Mutex(false,@"Local\SwtorRecap.Tracker");
        using var guard=new WriterGuard(root);
        var store=new RecapStore(root);store.Verify();var catalog=Catalog.LoadDefault();
        var executable=Environment.ProcessPath??throw new InvalidOperationException("Agent path unavailable.");
        var processName="swtor";var processOption=Array.IndexOf(args,"--test-process-name");
        if(processOption>=0){if(test<0)throw new ArgumentException("A fixture process requires an isolated test data root.");processName=args[processOption+1];}
        var coordinator=new SessionCoordinator(store,catalog,new WindowsGameProcesses(processName),new WindowsAwakeClock(),test>=0?Path.Combine(root,"logs"):WindowsFileIdentity.CombatLogFolder,WindowsFileIdentity.Get);
        await using var service=new TrackerService(store,catalog,coordinator,test>=0?new PreviewStartup():new WindowsStartup(executable));
        if(args.Contains("--session-once"))store.UpdateSettings(store.Revision.SettingsRevision,store.Settings with{SessionOnce=true});
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(service.Shutdown);
        Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;cancellation.Cancel();};
        AppDomain.CurrentDomain.ProcessExit+=(_,_)=>cancellation.Cancel();
        void OpenDashboard(bool settings)
        {
            var path=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","SwtorRecap.exe"));
            if(!File.Exists(path))return;
            var info=new ProcessStartInfo(path){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(path)!};if(settings)info.ArgumentList.Add("--settings");
            if(test>=0){info.ArgumentList.Add("--data-dir");info.ArgumentList.Add(root);}
            Process.Start(info);
        }
        using var tray=new NativeTray(action=>
        {
            if(action is "Open" or "Settings"){OpenDashboard(action=="Settings");return;}
            _=Task.Run(async()=>
            {
                var command=action=="Pause"?"Pause":"Quit";object parameter=action=="Pause"?!store.Settings.Paused:new{};
                await service.HandleAsync(new(Versions.Protocol,Guid.NewGuid().ToString("N"),command,JsonSerializer.SerializeToElement(parameter,Json.Options),store.Revision.HistoryEpoch));
            });
        },_=>coordinator.Wake.Writer.TryWrite(true));
        var monitor=Task.Run(()=>service.MonitorAsync(cancellation.Token));
        var pipes=LocalPipe.ServeAsync(DataPaths.PipeName(root),service,cancellation.Token);
        try
        {
            while(!cancellation.IsCancellationRequested)
            {
                tray.Update(service.LastError??coordinator.Message,store.Settings.Paused,coordinator.Active);
                var wait=Task.Delay(3000,cancellation.Token);var done=await Task.WhenAny(wait,pipes,monitor);
                if(done==pipes||done==monitor){await done;break;}
            }
        }
        catch(OperationCanceledException){}
        finally{cancellation.Cancel();try{await Task.WhenAll(pipes,monitor);}catch(OperationCanceledException){}}
        return 0;
    }
}
