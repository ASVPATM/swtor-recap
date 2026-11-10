using SwtorRecap.Core;

namespace SwtorRecap.Core.Tests;

public sealed class ReducerTests
{
    private static string E(string source,string target,string effect,string detail="",string type=EffectIds.Event,string time="20:00:00.000")=>$"[{time}] [{source}] [{target}] [] [X {{{type}}}: Y {{{effect}}}] {detail}";
    private static SourceRecord[] Records(params string[] lines){long offset=0;return lines.Select(l=>{var r=new SourceRecord(offset,offset+l.Length+1,l);offset=r.End;return r;}).ToArray();}
    private static BatchProjection Reduce(params string[] lines)=>new CombatReducer(Catalog.LoadDefault(),"UTC").Reduce(new(){Clock=new(){Date=new(2026,11,1)}},"source","capture",Records(lines));
    [Fact]public void DisciplineOrderNeverElectsOwner()
    {var b=Reduce(E("@Other#202","","16141082698337403481",type:EffectIds.Discipline));Assert.Null(b.State.Owner);}
    [Fact]public void LaterAreaDoesNotBackAssignAnUnprovedPrefix()
    {
        var b=Reduce(E("@Other#202","Boss {999}:1",EffectIds.Damage,"(9000)",EffectIds.Apply),E("@Local#101","Boss {999}:1",EffectIds.Damage,"(100)",EffectIds.Apply),E("@Local#101","","123",type:EffectIds.Area));
        Assert.Equal("p:101",b.State.Segment!.Owner);Assert.Null(b.Segments[0].Owner);Assert.NotEqual(b.Segments[0].Id,b.State.Segment.Id);Assert.Contains(b.Gaps,g=>g.Reason=="prefix_continuity_unproven");
    }
    [Fact]public void CharacterSwitchCreatesBoundary()
    {var b=Reduce(E("@One#101","","123",type:EffectIds.Area),E("@One#101","Boss {999}:1",EffectIds.Damage,"(100)",EffectIds.Apply),E("@Two#202","","123",type:EffectIds.Area),E("@Two#202","Boss {999}:1",EffectIds.Damage,"(200)",EffectIds.Apply));Assert.Equal(2,b.Segments.Count);Assert.NotEqual(b.Segments[0].Id,b.Segments[1].Id);Assert.Equal("p:101",b.Segments[0].Owner);Assert.Equal("p:202",b.Segments[1].Owner);}
    [Fact]public void DeathTargetAndLifeContextMatter()
    {var b=Reduce(E("@Local#101","",EffectIds.Enter),E("@Other#202","@Local#101",EffectIds.Death),E("@Other#202","@Local#101",EffectIds.Death),E("@Local#101","@Local#101",EffectIds.Revive),E("@Other#202","@Local#101",EffectIds.Death));Assert.Equal(2,b.Facts.Count(f=>f.Kind=="player_death"));}
    [Fact]public void RepeatedEnterDoesNotMultiplyCombatDuration()
    {var b=Reduce(E("@Local#101","",EffectIds.Enter),E("@Local#101","",EffectIds.Enter,time:"20:00:10.000"),E("@Local#101","",EffectIds.Exit,time:"20:01:00.000"));Assert.Equal(60000,b.Intervals.Sum(i=>i.Milliseconds));}
    [Fact]public void SelfDamageExcludedSelfHealingIncluded()
    {var b=Reduce(E("@Local#101","",EffectIds.Enter),E("@Local#101","=",EffectIds.Damage,"(100)",EffectIds.Apply),E("@Local#101","=",EffectIds.Heal,"(50 ~0)",EffectIds.Apply));Assert.Equal(0,b.Buckets.Values.Sum(x=>x.Amounts.Damage));Assert.Equal(50,b.Buckets.Values.Sum(x=>x.Amounts.Healing));}
    [Theory]
    [InlineData("137438989504",true)]
    [InlineData("833571547775682",false)]
    public void FreshAreaStartsCleanRunButRepeatedAreaCannotEraseMissingCoverage(string previousArea,bool full)
    {
        var lines=new List<string>{E("@Local#101","","833571547775682",type:EffectIds.Area),E("@Local#101","",EffectIds.Enter)};
        foreach(var id in new[]{"3152170987814912","3152166692847616","3152162397880320","3152158102913024","3172554902601728"})
        {
            lines.Add(E("@Local#101",$"Boss {{{id}}}:1|(100/100)",EffectIds.Damage,"(100)",EffectIds.Apply));
            // Group credit is participation plus a logged target death, not the final blow.
            lines.Add(E("@Other#202",$"Boss {{{id}}}:1|(0/100)",EffectIds.Death));
        }
        var state=new ReducerState{Area=previousArea,Owner="p:101",Gap=true,Clock=new(){Date=new(2026,11,1)}};
        var b=new CombatReducer(Catalog.LoadDefault(),"UTC").Reduce(state,"source","capture",Records(lines.ToArray()));
        Assert.Equal(3,b.Attempts.Values.Count(a=>a.Result=="won"&&a.Qualified));
        var run=Assert.Single(b.Runs.Values);Assert.True(run.Terminal);Assert.Equal(full,run.Full);
    }
    [Fact]public void LoggedSelfEffectsCountAsHealingButCompanionAndIncomingHealsDoNot()
    {
        var b=Reduce(E("@Local#101","",EffectIds.Enter),
            E("@Local#101","=",EffectIds.Heal,"(173338 ~146281)",EffectIds.Apply),
            E("@Local#101","=",EffectIds.Heal,"(356360 ~162718)",EffectIds.Apply),
            E("@Local#101/Companion {999}:1","@Local#101",EffectIds.Heal,"(6857178 ~4108738)",EffectIds.Apply),
            E("@Other#202","@Local#101",EffectIds.Heal,"(5000)",EffectIds.Apply));
        Assert.Equal(529698,b.Buckets.Values.Sum(x=>x.Amounts.Healing));
        Assert.Equal(308999,b.Buckets.Values.Sum(x=>x.Amounts.EffectiveHealing));
    }
    [Fact]public void HpZeroRequiresClosingContextAndCanBeRetracted()
    {
        var catalog=Catalog.LoadDefault();var reducer=new CombatReducer(catalog,"UTC");var boss="Bestia {3273941900591104}:1";
        var b=reducer.Reduce(new(){Clock=new(){Date=new(2026,1,1)}},"source","capture",Records(E("@Local#101","",EffectIds.Enter),E("@Local#101",boss+"|(100/100)",EffectIds.Damage,"(1)",EffectIds.Apply),E(boss+"|(0/100)","@Local#101","123",type:EffectIds.Remove)));
        Assert.DoesNotContain(b.Attempts.Values,a=>a.Result=="won");
        var record=new SourceRecord(b.End,b.End+100,E("@Local#101","",EffectIds.Exit));var win=reducer.Reduce(b.State,"source","capture",[record]);Assert.Contains(win.Attempts.Values,a=>a.Result=="won"&&a.Qualified);
        var contradiction=reducer.Reduce(win.State,"source","capture",[new(record.End,record.End+100,E(boss+"|(80/100)","@Local#101","123",type:EffectIds.Remove))]);Assert.DoesNotContain(contradiction.Attempts.Values,a=>a.Qualified);Assert.DoesNotContain(contradiction.Runs.Values,r=>r.Terminal);
    }
}
