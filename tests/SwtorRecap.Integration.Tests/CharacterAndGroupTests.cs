using SwtorRecap.Core;
using SwtorRecap.Engine;
using SwtorRecap.Storage;
namespace SwtorRecap.Integration.Tests;
public sealed class CharacterAndGroupTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"swtor-characters-"+Guid.NewGuid().ToString("N"));
    private readonly Catalog _catalog=Catalog.LoadDefault();
    public CharacterAndGroupTests()=>Directory.CreateDirectory(_root);
    public void Dispose()=>Directory.Delete(_root,true);
    private static string E(string actor,string target,string effect,string type=EffectIds.Event,string tail="",string time="20:00:00.000")
        =>$"[{time}] [{actor}] [{target}] [] [X {{{type}}}: Y {{{effect}}}] {tail}";
    private sealed class Game:IGameProcessSource,IAwakeClock
    {
        public long Milliseconds{get;set;}
        public IReadOnlyList<ObservedProcess> Discover()=>[new(123,"test",new(2026,11,1,20,0,0,TimeSpan.Zero),new(2026,11,1,20,0,0,TimeSpan.Zero))];
        public Task WaitForExitAsync(ObservedProcess p,CancellationToken token)=>Task.Delay(Timeout.Infinite,token);
    }
    private sealed class Clock:TimeProvider {public override DateTimeOffset GetUtcNow()=>new(2026,11,1,20,30,0,TimeSpan.Zero);}
    private static string[] Pull(string player,int spawn)=>[
        E(player,"","833571547775682",EffectIds.Area,"(test-server) <v7.0.0b>"),E(player,"",EffectIds.Enter),
        E(player,$"Guard {{999}}:{spawn}|(40/100)",EffectIds.Damage,EffectIds.Apply,"(60)"),E("@Group#303",$"Guard {{999}}:{spawn}|(0/100)",EffectIds.Death),
        E(player,$"Boss {{3172554902601728}}:{spawn}|(40/100)",EffectIds.Damage,EffectIds.Apply,"(60)"),E("@Group#303",$"Boss {{3172554902601728}}:{spawn}|(0/100)",EffectIds.Death),E(player,"",EffectIds.Exit)];
    [Theory][InlineData(false)][InlineData(true)]
    public async Task CharacterSwitchesKeepOneSessionAndAllQueriesFilterCorrectly(bool rotateFiles)
    {
        var store=new RecapStore(Path.Combine(_root,"data"),"UTC");var logs=Path.Combine(_root,"logs");Directory.CreateDirectory(logs);var game=new Game();
        using var coordinator=new SessionCoordinator(store,_catalog,game,game,logs,time:new Clock());coordinator.Tick();var session=coordinator.ContainerId;
        await using var service=new TrackerService(store,_catalog,coordinator);
        var players=new[]{"@First#101","@Second#202","@First#101"};
        for(var i=0;i<players.Length;i++)
        {
            var file=Path.Combine(logs,$"combat_2026-11-01_20_0{(rotateFiles?i:0)}_00_000000.txt");File.AppendAllLines(file,Pull(players[i],i+1));
            coordinator.Tick(true);Assert.Equal(session,coordinator.ContainerId);Assert.Equal(i==1?"Second":"First",service.Snapshot().CurrentCharacter);
        }
        var all=store.Totals(new(),_catalog);Assert.Equal(1,all.Sessions);Assert.Equal(360,all.Amounts!.Damage);Assert.Equal(6,all.Kills);Assert.Equal(3,all.Bosses);Assert.Equal(3,all.Flashpoints);
        var characters=store.History(new(),_catalog).Characters;Assert.Equal(2,characters.Length);
        foreach(var character in characters)
        {
            var multiple=character.Name=="First"?2:1;var filter=new QueryFilter(Year:2026,CharacterId:character.Id);
            var recap=store.Recap(filter,_catalog);var history=Assert.Single(store.History(filter,_catalog).Items);var detail=store.Detail(session!,_catalog,character.Id);
            foreach(var totals in new[]{recap.Totals,history.Totals,detail.Item.Totals})
            {Assert.Equal(120*multiple,totals.Amounts!.Damage);Assert.Equal(2*multiple,totals.Kills);Assert.Equal(multiple,totals.Bosses);Assert.Equal(multiple,totals.Flashpoints);}
            Assert.Equal(multiple,detail.Outcomes.Length);Assert.All(detail.Outcomes,o=>Assert.Equal(character.Name,o.Character));Assert.Equal(2*multiple,detail.Kills!.Sum(k=>k.Count));
        }
        coordinator.Stop();store.FinalizeSession(session!,DateTimeOffset.UtcNow,"test");new HistoryMaintenance(store,_catalog).Rebuild();
        Assert.Equal(6,store.Totals(new(),_catalog).Kills);Assert.Equal(2,store.History(new(),_catalog).Characters.Length);
    }
    [Fact]public void DeadPlayerGetsTwoIndividualBossTargetsOneEncounterAndClear()
    {
        var store=new RecapStore(Path.Combine(_root,"data"),"UTC");var engine=new IngestionEngine(store,_catalog);var capture=store.CreateContainer("import");
        var lines=new List<string>{E("@Local#101","","833571547775680",EffectIds.Area,"(test) <v7.0.0b>"),E("@Local#101","",EffectIds.Enter)};
        var ids=new[]{"957850721452032","957597318381568","739825296605184"};var amounts=new[]{60,51,50};
        for(var i=0;i<3;i++)lines.Add(E("@Local#101",$"Boss {{{ids[i]}}}:{i+1}|({100-amounts[i]}/100)",EffectIds.Damage,EffectIds.Apply,$"({amounts[i]})"));
        lines.Add(E("@Enemy#999","@Local#101",EffectIds.Death));lines.Add(E("@Local#101","",EffectIds.Exit));
        for(var i=0;i<3;i++)lines.Add(E("@Other#202",$"Boss {{{ids[i]}}}:{i+1}|(0/100)",EffectIds.Death));
        var path=Path.Combine(_root,"group.txt");File.WriteAllLines(path,lines);var source=engine.Register(capture,path,true,new(2026,11,1),"UTC");
        while(engine.ReadOnce(source,maxRecords:1)>0){} // Every event crosses a durable checkpoint.
        var total=store.Totals(new(),_catalog);Assert.Equal(2,total.Kills);Assert.Equal(1,total.Bosses);Assert.Equal(1,total.Flashpoints);Assert.Equal(1,total.Deaths);Assert.Equal(0,total.FullRuns);
        engine.ReadAll(source);new HistoryMaintenance(store,_catalog).Rebuild();Assert.Equal(2,store.Totals(new(),_catalog).Kills);
    }
    [Fact]public async Task UpdatePreparationBacksUpHistoryAndRefusesActiveGame()
    {
        var store=new RecapStore(Path.Combine(_root,"data"),"UTC");
        await using(var service=new TrackerService(store,_catalog))
        {await using var client=new LocalTrackerClient(service);var result=await client.RequestAsync<MutationResult>("PrepareUpdate");Assert.True(File.Exists(result.Path));Assert.Equal(Versions.App,(await client.RequestAsync<TrackerSnapshot>("Snapshot")).AppVersion);}
        var game=new Game();using var coordinator=new SessionCoordinator(store,_catalog,game,game,Path.Combine(_root,"logs"),time:new Clock());
        await using var active=new TrackerService(store,_catalog,coordinator);await using var connected=new LocalTrackerClient(active);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>connected.RequestAsync<MutationResult>("PrepareUpdate"));
    }
}
