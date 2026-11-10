using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Styling;
using Avalonia.Threading;
using SwtorRecap.Core;
using SwtorRecap.Engine;
using SwtorRecap.Storage;
using SwtorRecap.Platform.Windows;

namespace SwtorRecap.Desktop;

internal static class Program
{
    internal static ITrackerClient Client=null!;
    internal static bool Preview,Settings;
    private static TrackerService? _service;
    private static WriterGuard? _previewGuard;
    private static FileStream? _desktopGuard;
    private static CancellationTokenSource? _activation;
    private static Mutex? _installerMutex;
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Preview=args.Contains("--preview");Settings=args.Contains("--settings");
            string? Argument(string name){var index=Array.IndexOf(args,name);return index>=0&&index+1<args.Length?args[index+1]:null;}
            if(Preview)
            {
                var root=DataPaths.Isolated(Environment.GetEnvironmentVariable("PROJECT_FLOW_DATA")??Argument("--data-dir"));_previewGuard=new(root);
                var store=new RecapStore(root,"UTC");var catalog=Catalog.LoadDefault();
                var fixture=Argument("--fixture");if(fixture is not null&&!File.Exists(fixture))throw new FileNotFoundException("Preview fixture not found.",fixture);
                var year=fixture is null?2026:Json.Read<PreviewFixture>(File.ReadAllText(fixture)).Year;
                if(year is <1900 or >2200)throw new ArgumentException("Preview fixture year is outside the supported calendar.");
                if(!args.Contains("--empty"))PreviewSeeder.Seed(store,catalog,year);
                _service=new(store,catalog);Client=new LocalTrackerClient(_service);
            }
            else
            {
                if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Use --preview with PROJECT_FLOW_DATA on macOS.");
                WindowsAppIdentity.Apply();
                if(Argument("--data-dir") is null)_installerMutex=new Mutex(false,@"Local\SwtorRecap.Desktop");
                var root=Argument("--data-dir") is { } test?DataPaths.Isolated(test):DataPaths.Production();Directory.CreateDirectory(root);
                try{_desktopGuard=new FileStream(Path.Combine(root,"desktop.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
                catch(IOException){ActivateExisting(root,Settings).GetAwaiter().GetResult();return 0;}
                Client=new PipeTrackerClient(root,Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"agent","SwtorRecap.Agent.exe")),Argument("--data-dir") is not null);
                _activation=new();_ = ListenForActivation(root,_activation.Token);
            }
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args,ShutdownMode.OnMainWindowClose);
        }
        catch(Exception ex){if(OperatingSystem.IsWindows())WindowsAppIdentity.ShowError(ex.Message);else Console.Error.WriteLine(ex.Message);return 1;}
        finally
        {
            _activation?.Cancel();Client?.DisposeAsync().AsTask().GetAwaiter().GetResult();_service?.DisposeAsync().AsTask().GetAwaiter().GetResult();_desktopGuard?.Dispose();_previewGuard?.Dispose();_installerMutex?.Dispose();
        }
    }
    public static AppBuilder BuildAvaloniaApp()=>AppBuilder.Configure<App>().UsePlatformDetect();
    private sealed record PreviewFixture(int Year);
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task ActivateExisting(string root,bool settings)
    {
        using var pipe=new System.IO.Pipes.NamedPipeClientStream(".",DataPaths.PipeName(root)+".desktop",System.IO.Pipes.PipeDirection.Out,System.IO.Pipes.PipeOptions.Asynchronous|System.IO.Pipes.PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(2000);await FrameProtocol.WriteAsync(pipe,settings?"settings":"open",CancellationToken.None);
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task ListenForActivation(string root,CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            try
            {
                using var pipe=LocalPipe.Create(DataPaths.PipeName(root)+".desktop");await pipe.WaitForConnectionAsync(token);LocalPipe.VerifyPeer(pipe);
                var command=await FrameProtocol.ReadAsync<string>(pipe,token);
                Dispatcher.UIThread.Post(()=>{if(Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life&&life.MainWindow is { } window){window.WindowState=WindowState.Normal;window.Activate();if(command=="settings"){Settings=true;if(window is MainWindow main)main.Navigate("Settings");}}});
            }
            catch(Exception ex) when(ex is IOException or OperationCanceledException or UnauthorizedAccessException){}
        }
    }
}

public sealed class App : Application
{
    public override void Initialize(){Styles.Add(new FluentTheme());RequestedThemeVariant=ThemeVariant.Dark;}
    public override void OnFrameworkInitializationCompleted()
    {
        if(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow=Program.Preview?new MainWindow(Program.Client,true,Program.Settings):new StartupWindow(Program.Client,snapshot=>
            {
                var window=new MainWindow(Program.Client,false,Program.Settings,snapshot);
                desktop.MainWindow=window;window.Show();
            });
        base.OnFrameworkInitializationCompleted();
    }
}
