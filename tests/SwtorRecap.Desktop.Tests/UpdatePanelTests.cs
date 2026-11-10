using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SwtorRecap.Core;
using SwtorRecap.Desktop;
using SwtorRecap.Engine;
namespace SwtorRecap.Desktop.Tests;
public sealed class UpdatePanelTests
{
    private sealed class Updater:IReleaseUpdater
    {
        public int Checks,Downloads;public bool Fail,Private;
        public Task<UpdateCheck> CheckAsync(CancellationToken token)
        {Checks++;if(Fail)throw new IOException("Offline");return Task.FromResult(Private?new UpdateCheck(null,"No public stable release is available yet."):new(new("9.0.0","unused","unused",100),"Version 9.0.0 is available."));}
        public Task<string> DownloadAsync(ReleaseUpdate r,IProgress<int>? progress,CancellationToken token)
        {Downloads++;progress?.Report(100);return Task.FromResult("verified-test-file");}
    }
    private static Button Find(Window window,string id)=>window.GetVisualDescendants().OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)==id);
    private static string Text(Window window)=>string.Join("\n",window.GetVisualDescendants().OfType<TextBlock>().Select(t=>t.Text));
    [AvaloniaFact]public void UpdatesRequireUserActionAndBlockInstallationDuringPlay()
    {
        var updater=new Updater();var playing=true;var installed=false;
        var window=new Window{Width=760,Height=450,Content=new UpdatePanel(updater,(_,path)=>{Assert.Equal("verified-test-file",path);installed=true;return Task.CompletedTask;},()=>!playing)};
        window.Show();window.UpdateLayout();Assert.Equal(0,updater.Checks);
        Find(window,"updates-check").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();Assert.Contains("9.0.0",Text(window));
        Find(window,"updates-install").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal(0,updater.Downloads);Assert.False(installed);
        playing=false;Find(window,"updates-install").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal(1,updater.Downloads);Assert.True(installed);window.Close();
    }
    [AvaloniaFact]public void OfflineAndPrivateChecksHaveAnHonestRetryState()
    {
        var updater=new Updater{Fail=true};var window=new Window{Width=740,Height=430,Content=new UpdatePanel(updater,(_,_)=>Task.CompletedTask,()=>true,true)};
        AppTheme.Apply(window);window.Show();window.UpdateLayout();Find(window,"updates-check").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();
        Assert.Contains("Offline",Text(window));Assert.True(Find(window,"updates-check").IsEnabled);Assert.False(Find(window,"updates-install").IsVisible);
        updater.Fail=false;updater.Private=true;Find(window,"updates-check").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();Assert.Contains("No public stable release",Text(window));
        updater.Private=false;Find(window,"updates-check").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();using var bitmap=window.CaptureRenderedFrame();
        var path=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../.artifacts/ui-qa/updates.png"));Directory.CreateDirectory(Path.GetDirectoryName(path)!);bitmap!.Save(path,Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);window.Close();
    }
    private sealed class OldTracker:ITrackerClient,ITrackerVersionRecovery
    {
        public bool Restarted;
        public Task<T> RequestAsync<T>(string command,object? parameters=null,CancellationToken cancellationToken=default)
        {
            if(!Restarted)throw new TrackerVersionException(null);
            return Task.FromResult((T)(object)new TrackerSnapshot(new("e",1,1),"Idle","Ready",new(null,false,false,false,"UTC"),null,"3.53.4",Versions.Parser,Versions.Catalog,true,[],AppVersion:Versions.App));
        }
        public Task RestartMatchingAgentAsync(CancellationToken token){Restarted=true;return Task.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    [AvaloniaFact]public void OlderTrackerCannotOpenDashboardUntilMatchingRestartCompletes()
    {
        var client=new OldTracker();var opened=false;var window=new StartupWindow(client,_=>opened=true);window.Show();window.UpdateLayout();
        Assert.False(opened);Assert.Contains("older release",Text(window));var retry=Find(window,"startup-retry");Assert.Equal("Restart matching tracker",retry.Content);
        retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.True(client.Restarted);Assert.True(opened);
    }
}
