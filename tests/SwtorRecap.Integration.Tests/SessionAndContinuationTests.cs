using SwtorRecap.Core;
using SwtorRecap.Engine;
using SwtorRecap.Storage;

namespace SwtorRecap.Integration.Tests;

public sealed class SessionAndContinuationTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"swtor-session-"+Guid.NewGuid().ToString("N"));
    private readonly Catalog _catalog=Catalog.LoadDefault();
    public SessionAndContinuationTests()=>Directory.CreateDirectory(_root);
    public void Dispose()=>Directory.Delete(_root,true);
    private sealed class Clock : TimeProvider,IAwakeClock
    {
        public DateTimeOffset Now=new(2026,11,1,20,0,0,TimeSpan.Zero);
        public long Milliseconds{get;set;}
        public override DateTimeOffset GetUtcNow()=>Now;
        public void Advance(int awake,int wall){Milliseconds+=awake;Now=Now.AddMilliseconds(wall);}
    }
    private sealed class Processes : IGameProcessSource
    {
        public List<ObservedProcess> Items=[];
        public IReadOnlyList<ObservedProcess> Discover()=>Items;
        public Task WaitForExitAsync(ObservedProcess process,CancellationToken token)=>Task.Delay(Timeout.Infinite,token);
    }
    [Fact]public void SameProcessRestartPreservesSessionAndExcludesUnobservedTime()
    {
        var s=new RecapStore(Path.Combine(_root,"data"),"UTC");var clock=new Clock();var processes=new Processes{Items=[new(44,"scope",clock.Now.AddHours(-1),clock.Now)]};
        string id;
        using(var first=new SessionCoordinator(s,_catalog,processes,clock,Path.Combine(_root,"logs"),time:clock)){first.Tick();id=first.ContainerId!;clock.Advance(5000,5000);first.Tick();first.Stop();}
        clock.Advance(60000,60000);
        using(var second=new SessionCoordinator(s,_catalog,processes,clock,Path.Combine(_root,"logs"),time:clock)){second.Tick();Assert.Equal(id,second.ContainerId);clock.Advance(3000,3000);second.Tick();}
        Assert.Equal(1,s.Totals(new(),_catalog).Sessions);Assert.Equal(8000,s.Totals(new(),_catalog).GameMilliseconds);
    }
    [Fact]public void PidReuseProducesNewSession()
    {
        var s=new RecapStore(Path.Combine(_root,"data"),"UTC");var clock=new Clock();var processes=new Processes{Items=[new(44,"scope",clock.Now,clock.Now)]};string first;
        using(var coordinator=new SessionCoordinator(s,_catalog,processes,clock,_root,time:clock)){coordinator.Tick();first=coordinator.ContainerId!;}
        clock.Advance(5000,5000);processes.Items=[new(44,"scope",clock.Now,clock.Now)];
        using(var coordinator=new SessionCoordinator(s,_catalog,processes,clock,_root,time:clock)){coordinator.Tick();Assert.NotEqual(first,coordinator.ContainerId);}
        Assert.Equal(2,s.Totals(new(),_catalog).Sessions);
    }
    [Fact]public void SleepAndPauseDoNotBecomeObservedGameHours()
    {
        var s=new RecapStore(Path.Combine(_root,"data"),"UTC");var clock=new Clock();var processes=new Processes{Items=[new(44,"scope",clock.Now,clock.Now)]};
        using var coordinator=new SessionCoordinator(s,_catalog,processes,clock,Path.Combine(_root,"logs"),time:clock);coordinator.Tick();clock.Advance(5000,3600000);coordinator.Tick();
        Assert.Equal(5000,s.Totals(new(),_catalog).GameMilliseconds);Assert.Equal(0,s.Recap(new(Year:2026),_catalog).Totals.GameMilliseconds);
        coordinator.SetPaused(true);clock.Advance(5000,5000);coordinator.Tick();coordinator.SetPaused(false);Assert.Equal(5000,s.Totals(new(),_catalog).GameMilliseconds);
        processes.Items=[];coordinator.Tick();Assert.Null(coordinator.ContainerId);Assert.Equal("completed",s.History(new(),_catalog).Items.Single().State);
    }
    [Fact]public void CopyingHistoricalFilesIntoAnActiveFolderDoesNotImportThemAutomatically()
    {
        var store=new RecapStore(Path.Combine(_root,"data"),"UTC");var clock=new Clock();var processes=new Processes{Items=[new(44,"scope",clock.Now,clock.Now)]};
        var logs=Path.Combine(_root,"logs");Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs,"combat_2020-01-01_20_00_00_000000.txt"),"old recording\n");
        using var coordinator=new SessionCoordinator(store,_catalog,processes,clock,logs,time:clock);coordinator.Tick();Assert.Empty(store.SourceIds());
    }
    [Fact]public async Task GameExitClearsCurrentRecordingAndPauseWithoutRemovingHistory()
    {
        var store=new RecapStore(Path.Combine(_root,"data"),"UTC");var clock=new Clock();
        var processes=new Processes();store.UpdateSettings(store.Revision.SettingsRevision,store.Settings with{Paused=true});
        using var coordinator=new SessionCoordinator(store,_catalog,processes,clock,Path.Combine(_root,"logs"),time:clock);
        await using var service=new TrackerService(store,_catalog,coordinator);coordinator.Tick();
        Assert.False(store.Settings.Paused);processes.Items=[new(44,"scope",clock.Now,clock.Now)];coordinator.Tick();
        Assert.NotNull(service.Snapshot().Current);coordinator.SetPaused(true);
        processes.Items=[];coordinator.Tick();var snapshot=service.Snapshot();
        Assert.Null(snapshot.Current);Assert.False(snapshot.Settings.Paused);
        Assert.Single(store.History(new(),_catalog).Items);
        var response=await service.HandleAsync(new(Versions.Protocol,"idle-pause","Pause",System.Text.Json.JsonSerializer.SerializeToElement(true),store.Revision.HistoryEpoch));
        Assert.False(response.Success);Assert.False(store.Settings.Paused);
        Assert.Equal(store.Root,snapshot.DataDirectory);Assert.Equal(Path.Combine(_root,"logs"),snapshot.ResolvedLogFolder);
    }
    [Fact]public void VerifiedContinuationCarriesMultiRoleAttemptAndRebuildsWholeClosure()
    {
        var s=new RecapStore(Path.Combine(_root,"data"),"UTC");var engine=new IngestionEngine(s,_catalog);var container=s.CreateContainer("import");
        var first=Path.Combine(_root,"a.txt");var second=Path.Combine(_root,"b.txt");
        const string area="[20:00:00.000] [@Local#101] [] [] [Area {836045448953664}: KP {833571547775669}] (test) <v7.0.0b>";
        const string jarg="Jarg {2760500035190784}:1";const string sorno="Sorno {2760504330158080}:2";
        File.WriteAllLines(first,[area,PreviewSeeder.Line("20:00:01.000","@Local#101","","",$"Event {{{EffectIds.Event}}}: Enter {{{EffectIds.Enter}}}"),
            PreviewSeeder.Line("20:00:02.000","@Local#101",jarg+"|(100/100)","",$"Apply {{{EffectIds.Apply}}}: Damage {{{EffectIds.Damage}}}","(100)"),
            PreviewSeeder.Line("20:00:03.000","@Other#202",jarg+"|(0/100)","",$"Event {{{EffectIds.Event}}}: Death {{{EffectIds.Death}}}")]);
        File.WriteAllLines(second,[area.Replace("20:00:00","20:00:04"),
            PreviewSeeder.Line("20:00:05.000","@Local#101",sorno+"|(100/100)","",$"Apply {{{EffectIds.Apply}}}: Damage {{{EffectIds.Damage}}}","(200)"),
            PreviewSeeder.Line("20:00:06.000","@Other#202",sorno+"|(0/100)","",$"Event {{{EffectIds.Event}}}: Death {{{EffectIds.Death}}}")]);
        var a=engine.Register(container,first,true,new(2026,11,1),"UTC");engine.ReadAll(a);var b=engine.Register(container,second,true,new(2026,11,1),"UTC");Assert.True(engine.TryContinue(a,b));engine.ReadAll(b);
        Assert.Equal(1,s.Totals(new(),_catalog).Bosses);Assert.Equal(300,s.Totals(new(),_catalog).Amounts!.Damage);s.Verify();
        new HistoryMaintenance(s,_catalog).Rebuild();Assert.Equal(1,s.Totals(new(),_catalog).Bosses);Assert.Equal(300,s.Totals(new(),_catalog).Amounts!.Damage);s.Verify();
    }
}
