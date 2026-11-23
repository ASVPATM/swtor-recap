using SwtorRecap.Core;

namespace SwtorRecap.Core.Tests;

public sealed class EncounterEvidenceTests
{
    private const string Local="@Local#101", Other="@Other#202";
    private const string Braxx="Braxx {2438167034593280}:1", Hound1="Hound {2438158444658688}:2", Hound2="Hound {2438158444658688}:3";
    private static string E(string source,string target,string effect,string tail="",string type=EffectIds.Event)
        =>$"[20:00:00.000] [{source}] [{target}] [] [X {{{type}}}: Y {{{effect}}}] {tail}";
    private static string Area(string id,string? difficulty=null)
        =>$"[20:00:00.000] [{Local}] [] [] [Area {{{EffectIds.Area}}}: Zone {{{id}}}"+(difficulty is null?"":$" Mode {{{difficulty}}}")+"]";
    private static string Hit(string target)=>E(Local,target+"|(40/100)",EffectIds.Damage,"(60)",EffectIds.Apply);
    private static string Death(string target)=>E(Other,target+"|(0/100)",EffectIds.Death);
    private static BatchProjection Replay(IEnumerable<string> lines,bool checkpoint=true)
    {
        long offset=0;
        var records=lines.Select(l=>{var r=new SourceRecord(offset,offset+l.Length+1,l);offset=r.End;return r;}).ToArray();
        var reducer=new CombatReducer(Catalog.LoadDefault(),"UTC");
        var state=new ReducerState{Clock=new(){Date=new(2026,11,1)}};
        if(!checkpoint)return reducer.Reduce(state,"source","session",records);
        var all=new BatchProjection{State=state};
        foreach(var record in records)
        {
            var batch=reducer.Reduce(Json.Clone(state),"source","session",[record]);state=batch.State;
            foreach(var (id,a) in batch.Attempts)all.Attempts[id]=a;
            foreach(var (id,r) in batch.Runs)all.Runs[id]=r;
            all.Facts.AddRange(batch.Facts);
        }
        return all;
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void KaonVeteranFinalRequiresAllThreeActualTargets(bool complete)
    {
        string[] targets=["Commander {2762127827795968}:1","Chak {2762140712697856}:2","Gklarg {2762123532828672}:3"];
        var lines=new List<string>{Area("833571547775674"),E(Local,"",EffectIds.Enter)};
        lines.AddRange(targets.Select(Hit));lines.AddRange(targets.Take(complete?3:2).Select(Death));lines.Add(E(Local,"",EffectIds.Exit));
        var batch=Replay(lines);
        Assert.Equal(complete,Assert.Single(batch.Attempts.Values).Result=="won");
        Assert.Equal(complete,Assert.Single(batch.Runs.Values).Terminal);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void BraxxAndTwoHoundsRemainOneEncounterWhenTargetsAlternate(bool checkpoint)
    {
        var lines=new List<string>{Area("833571547775687"),E(Local,"",EffectIds.Enter),Hit(Braxx)};
        for(var i=0;i<12;i++){lines.Add(Hit(Hound1));lines.Add(Hit(Hound2));}
        // A group finish after the local player's death still counts, including each majority target.
        lines.Add(E(Other,Local,EffectIds.Death));lines.Add(E(Local,"",EffectIds.Exit));
        lines.AddRange(new[]{Braxx,Hound1,Hound2}.Select(Death));lines.Add(Death(Hound2));
        var batch=Replay(lines,checkpoint);var attempt=Assert.Single(batch.Attempts.Values);
        Assert.Equal("won",attempt.Result);Assert.True(attempt.Qualified);Assert.Equal(3,attempt.Entities.Count);
        Assert.Equal(3,batch.Facts.Count(f=>f.Kind=="npc_kill"));
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void BraxxNeedsBothDistinctHoundDeaths(bool observeSecond)
    {
        var lines=new List<string>{Area("833571547775687"),E(Local,"",EffectIds.Enter),Hit(Braxx),Hit(Hound1)};
        if(observeSecond)lines.Add(Hit(Hound2));
        lines.AddRange([Death(Braxx),Death(Hound1),Death(Hound1),E(Local,"",EffectIds.Exit)]);
        Assert.DoesNotContain(Replay(lines).Attempts.Values,a=>a.Result=="won");
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void SeparatePullsCannotCombineHoundDeaths(bool combatBoundary)
    {
        var lines=new List<string>{Area("833571547775687"),E(Local,"",EffectIds.Enter),Hit(Braxx),Hit(Hound1),Hit(Hound2),Death(Braxx),Death(Hound1)};
        if(combatBoundary)lines.AddRange([E(Local,"",EffectIds.Exit),E(Local,"",EffectIds.Enter)]);
        var nextBraxx=Braxx.Replace(":1",":4");var nextHound1=Hound1.Replace(":2",":5");var nextHound2=Hound2.Replace(":3",":6");
        lines.AddRange([Hit(nextBraxx),Hit(nextHound1),Hit(nextHound2),Death(nextBraxx),Death(nextHound2),E(Local,"",EffectIds.Exit)]);
        Assert.DoesNotContain(Replay(lines).Attempts.Values,a=>a.Result=="won");
    }
    [Theory][InlineData("836045448953657",true)][InlineData("836045448953659",false)][InlineData(null,false)][InlineData("999",false)]
    public void OnlyConfirmedVeteranModeEndsCademimuOnXanderAlone(string? difficulty,bool won)
    {
        const string xander="Xander {2504232221540352}:1";
        var batch=Replay([Area("833571547775684",difficulty),E(Local,"",EffectIds.Enter),Hit(xander),Death(xander),E(Local,"",EffectIds.Exit)]);
        Assert.Equal(won,Assert.Single(batch.Attempts.Values).Result=="won");
        Assert.False(Assert.Single(batch.Runs.Values).Terminal);
    }
    [Fact]public void MasterCademimuStillRequiresBothDeaths()
    {
        const string xander="Xander {3210672737353728}:1",droid="EN4C {3210668442386432}:2";
        var batch=Replay([Area("833571547775684","836045448953659"),E(Local,"",EffectIds.Enter),Hit(xander),Hit(droid),Death(xander),Death(droid)]);
        Assert.Equal("won",Assert.Single(batch.Attempts.Values).Result);
    }
}
