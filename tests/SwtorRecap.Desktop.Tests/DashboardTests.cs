using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using SwtorRecap.Core;
using SwtorRecap.Desktop;

[assembly:AvaloniaTestApplication(typeof(SwtorRecap.Desktop.Tests.TestApplication))]

namespace SwtorRecap.Desktop.Tests;

public sealed class TestApplication : Application
{
    public static AppBuilder BuildAvaloniaApp()=>AppBuilder.Configure<TestApplication>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false});
    public override void Initialize(){Styles.Add(new FluentTheme());RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Dark;}
}
public sealed class DashboardTests
{
    private sealed class Client : ITrackerClient,ITrackerReconnect
    {
        public bool Empty,Disconnected,Paused,Unresolved,GameClosed,LegacyKills;
        public readonly List<string> Commands=[];
        public readonly List<(string Command,object? Parameters)> Requests=[];
        private readonly Revision _revision=new("epoch",42,0);
        private Totals Totals=>new(new(){Damage=123456789,EffectiveDamage=100000000,Healing=9876543,EffectiveHealing=8000000},3600000,900000,3,1,0,0,1,2,1,0,37,LegacyKills);
        private HistoryItem Item=>new("session","session","completed","2026-11-01","2026-11-01",["A character with an intentionally very long display name for layout verification"],Totals,1);
        public Task<T> RequestAsync<T>(string command,object? parameters=null,CancellationToken cancellationToken=default)
        {
            Commands.Add(command);Requests.Add((command,parameters));if(Disconnected)throw new IOException("Test connection interrupted.");
            object result=command switch
            {
                "Snapshot"=>new TrackerSnapshot(_revision,Empty||GameClosed?"Idle":"Tracking",Empty||GameClosed?"Ready for SWTOR":"Recording available combat logs",new(null,Paused,false,false,"UTC"),Empty||GameClosed?null:Item,"3.53.4",Versions.Parser,Versions.Catalog,true,[],"C:\\Documents\\CombatLogs","C:\\AppData\\SwtorRecap"),
                "History"=>new HistoryPage(_revision,Empty?[]:[Item],false,[new("first","Aster",0,0,0),new("second","Mira",0,0,0)]),
                "Detail"=>new HistoryDetail(_revision,Item,[],[new("source","capture.txt",100,100,1,0,"verified",null,null,Unresolved?[new("101","Aster")]:[])],[],[new("Guard","999",37)]),
                "Recap"=>new RecapSnapshot(_revision,2026,null,"UTC",Totals,[new("2026-11-01",123456789,9876543,900000,3600000)],[],[],["Partial source coverage."],true,Item),
                "Pause"=>Pause((bool)parameters!),
                "AttributeOwner"=>Attribute(),
                _=>throw new InvalidOperationException(command)
            };
            return Task.FromResult((T)result);
        }
        private MutationResult Pause(bool value){Paused=value;return new(_revision,"Saved.");}
        private MutationResult Attribute(){Unresolved=false;return new(_revision,"Owner applied.");}
        public void PermitAgentLaunch(){Disconnected=false;Commands.Add("ExplicitReconnect");}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private static string VisibleText(Window window)=>string.Join("\n",window.GetVisualDescendants().OfType<TextBlock>().Select(t=>t.Text));
    private static Button Find(Window window,string id)=>window.GetVisualDescendants().OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)==id);
    [AvaloniaFact]public void SessionHistoryAndRecapShareExactAmounts()
    {
        var client=new Client();var window=new MainWindow(client,true);window.Show();window.UpdateLayout();
        Assert.Contains("123,456,789",VisibleText(window));
        Capture(window,"session");
        window.Navigate("History");window.UpdateLayout();Assert.Contains("123,456,789",VisibleText(window));
        Capture(window,"history");
        window.Navigate("Recap");window.UpdateLayout();Assert.Contains("123,456,789",VisibleText(window));Assert.Contains("Period in progress",VisibleText(window));Capture(window,"recap");window.Close();
    }
    [AvaloniaFact]public void NpcKillsAndOlderRecordingNoticeAreVisible()
    {
        var window=new MainWindow(new Client{LegacyKills=true},true);window.Show();window.UpdateLayout();
        Assert.Contains("NPC kills",VisibleText(window));Assert.Contains("37+",VisibleText(window));Assert.Contains("Reprocess saved logs",VisibleText(window));
        Find(window,"session-details").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();
        var expander=window.GetVisualDescendants().OfType<Expander>().Single(e=>e.Header?.ToString()=="NPC kills · 37");
        expander.IsExpanded=true;window.UpdateLayout();Assert.Contains("Guard · 37",VisibleText(window));Capture(window,"kills-detail");window.Close();
    }
    [AvaloniaFact]public void StartupFailureHasAWorkingRetryAndOnlyOpensDashboardAfterConnection()
    {
        var client=new Client{Disconnected=true};TrackerSnapshot? ready=null;
        var window=new StartupWindow(client,s=>ready=s);window.Show();window.UpdateLayout();
        Assert.Null(ready);Assert.Contains("Could not open the tracker",VisibleText(window));Capture(window,"startup-error");
        Find(window,"startup-retry").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.NotNull(ready);Assert.False(window.IsVisible);Assert.Contains("ExplicitReconnect",client.Commands);
    }
    private sealed class StartingClient : ITrackerClient
    {
        public TaskCompletionSource<TrackerSnapshot> Pending {get;}=new();
        public async Task<T> RequestAsync<T>(string command,object? parameters=null,CancellationToken cancellationToken=default)
            =>(T)(object)await Pending.Task.WaitAsync(cancellationToken);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    [AvaloniaFact]public async Task LoadingWindowTracksRealWorkAndClosingCancelsOpening()
    {
        var client=new StartingClient();var opened=false;var window=new StartupWindow(client,_=>opened=true);window.Show();window.UpdateLayout();
        Assert.Contains("Opening your history",VisibleText(window));Assert.False(opened);Capture(window,"startup");window.Close();
        client.Pending.SetResult(await new Client().RequestAsync<TrackerSnapshot>("Snapshot"));
        await Task.Yield();Assert.False(opened);
    }
    [AvaloniaFact]public void EmptyStateHasWorkingSetupAction()
    {
        var window=new MainWindow(new Client{Empty=true},true);window.Show();window.UpdateLayout();Assert.Contains("Ready when you are",VisibleText(window));
        Find(window,"session-setup").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();Assert.NotNull(Find(window,"import-logs"));window.Close();
    }
    [AvaloniaFact]public void PauseUsesServiceAndUpdatesPreference()
    {
        var client=new Client();var window=new MainWindow(client,true);window.Show();window.UpdateLayout();Find(window,"session-pause").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.True(client.Paused);Assert.Contains("Pause",client.Commands);window.Close();
    }
    [AvaloniaFact]public void ClosedGameHasNoRecordingControlsAndKeepsHistoryAccessible()
    {
        var client=new Client{GameClosed=true};var window=new MainWindow(client);window.Show();window.UpdateLayout();
        Assert.Contains("Ready when you are",VisibleText(window));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(),b=>AutomationProperties.GetAutomationId(b)=="session-pause");
        Assert.DoesNotContain("123,456,789",VisibleText(window));Capture(window,"idle");
        Find(window,"session-history").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();
        Assert.Contains("123,456,789",VisibleText(window));window.Close();
    }
    [AvaloniaFact]public void SettingsSeparatesReadOnlyCombatLogsFromSavedHistory()
    {
        var window=new MainWindow(new Client(),true,true);window.Show();window.UpdateLayout();
        var text=VisibleText(window);Assert.Contains("C:\\Documents\\CombatLogs",text);Assert.Contains("C:\\AppData\\SwtorRecap",text);Assert.Contains("without changing them",text);window.Close();
    }
    [Fact]public void RecordedDatesHideRawIsoDetails()
    {
        Assert.Equal("Nov 9, 2026",MainWindow.DisplayDate("2026-11-09"));
        Assert.DoesNotContain("T00:",MainWindow.DisplayDate("2026-11-10T00:42:28.0941660+00:00"));
        Assert.Equal("Undated recording",MainWindow.DisplayDate(null));
    }
    [AvaloniaFact]public void NarrowLongContentKeepsNavigationAndNumbersInsideWindow()
    {
        var window=new MainWindow(new Client(),true){Width=760,Height=560};window.Show();window.UpdateLayout();
        foreach(var id in new[]{"nav-session","nav-history","nav-recap","nav-settings"})
        {var button=Find(window,id);var top=button.TranslatePoint(default,window)!.Value;Assert.InRange(top.X,0,760-button.Bounds.Width);}
        Assert.Contains("123,456,789",VisibleText(window));Capture(window,"narrow-session");window.Close();
    }
    [AvaloniaFact]public void PreviewCannotEnableWindowsStartup()
    {
        var window=new MainWindow(new Client(),true,true);window.Show();window.UpdateLayout();var startup=window.GetVisualDescendants().OfType<CheckBox>().Single(c=>AutomationProperties.GetAutomationId(c)=="startup-preference");Assert.False(startup.IsEnabled);window.Close();
    }
    [AvaloniaFact]public void DisconnectionIsVisibleAndDoesNotClaimLiveValues()
    {var window=new MainWindow(new Client{Disconnected=true},true);window.Show();window.UpdateLayout();Assert.Contains("Disconnected",VisibleText(window));window.Close();}
    [AvaloniaFact]public void ReconnectionHasAnExplicitWorkingAction()
    {
        var client=new Client{Disconnected=true};var window=new MainWindow(client,true);window.Show();window.UpdateLayout();
        Find(window,"tracker-reconnect").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();
        Assert.Contains("ExplicitReconnect",client.Commands);Assert.DoesNotContain("Disconnected",VisibleText(window));Assert.Contains("123,456,789",VisibleText(window));window.Close();
    }
    [AvaloniaFact]public void RecapCharacterFilterReachesTheRealQueryContract()
    {
        var client=new Client();var window=new MainWindow(client,true);window.Show();window.Navigate("Recap");window.UpdateLayout();
        window.GetVisualDescendants().OfType<ComboBox>().Single(c=>AutomationProperties.GetAutomationId(c)=="recap-character").SelectedIndex=2;
        Find(window,"recap-filter").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("second",((QueryFilter)client.Requests.Last(r=>r.Command=="Recap").Parameters!).CharacterId);window.Close();
    }
    [AvaloniaFact]public void UnresolvedOwnerCanBeSelectedAndReprocessed()
    {
        var client=new Client{Unresolved=true};var window=new MainWindow(client,true);window.Show();window.UpdateLayout();
        Find(window,"session-details").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();
        Find(window,"owner-source").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();
        Assert.Equal(new[]{"source","101"},(string[])client.Requests.Single(r=>r.Command=="AttributeOwner").Parameters!);
        Assert.False(client.Unresolved);Assert.Contains("Owner applied.",VisibleText(window));window.Close();
    }
    [AvaloniaFact]public void UnchangedSettingsCannotBeSavedUntilEdited()
    {
        var window=new MainWindow(new Client(),true,true);window.Show();window.UpdateLayout();Assert.False(Find(window,"settings-save").IsEnabled);
        window.GetVisualDescendants().OfType<TextBox>().Single(c=>AutomationProperties.GetName(c)=="Combat-log folder override").Text="/example";
        Assert.True(Find(window,"settings-save").IsEnabled);Capture(window,"settings");window.Close();
    }
    private static string Artifact(string name)
    {var directory=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../.artifacts/ui-qa"));Directory.CreateDirectory(directory);return Path.Combine(directory,name+".png");}
    private static void Capture(Window window,string name)
    {AvaloniaHeadlessPlatform.ForceRenderTimerTick();using var bitmap=window.CaptureRenderedFrame();Assert.NotNull(bitmap);bitmap.Save(Artifact(name),PngBitmapEncoderOptions.Default);}
    [AvaloniaFact]public async Task ExportUsesExactDtoAndHidesNames()
    {
        var recap=await new Client().RequestAsync<RecapSnapshot>("Recap");var card=RecapImageRenderer.CreateCard(recap);
        var text=string.Join("\n",card.GetVisualDescendants().OfType<TextBlock>().Select(t=>t.Text));
        Assert.Contains("123,456,789",text);Assert.DoesNotContain("character with",text);RecapImageRenderer.Save(recap,Artifact("recap-export"));
        using var image=new Bitmap(Artifact("recap-export"));Assert.Equal(1100,image.PixelSize.Width);Assert.True(image.PixelSize.Height>500);
    }
    [AvaloniaFact]public async Task CharacterExportDoesNotClaimZeroObservedProcessTime()
    {
        var recap=await new Client().RequestAsync<RecapSnapshot>("Recap");var card=RecapImageRenderer.CreateCard(recap with{CharacterFilter="first"});
        Assert.Contains("Not attributed",string.Join("\n",card.GetVisualDescendants().OfType<TextBlock>().Select(t=>t.Text)));
    }
}
