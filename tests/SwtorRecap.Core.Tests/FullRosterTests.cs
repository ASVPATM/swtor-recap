using SwtorRecap.Core;

namespace SwtorRecap.Core.Tests;

public sealed class FullRosterTests
{
    private static readonly Catalog Catalog=Catalog.LoadDefault();
    private static string E(string source,string target,string effect,string tail="",string type=EffectIds.Event)
        =>$"[20:00:00.000] [{source}] [{target}] [] [X {{{type}}}: Y {{{effect}}}] {tail}";
    private static List<string> Lines(ActivityDefinition activity,int branch,bool omitFirst=false)
    {
        var lines=new List<string>{E("@Local#101","",activity.AreaIds[0],type:EffectIds.Area),E("@Local#101","",EffectIds.Enter)};
        foreach(var group in activity.RequiredGroups.Skip(omitFirst?1:0))
        {
            var encounter=activity.Encounters.Single(e=>e.Id==group[branch==0?0:group.Length-1]);
            var spawn=1;
            foreach(var role in encounter.Roles.Where(r=>r.IsRequired(null)))
                foreach(var _ in role.SlotIds())
                {
                    var npc=$"Boss {{{role.TemplateIds[0]}}}:{spawn++}";
                    lines.Add(E("@Local#101",npc+"|(99/100)",EffectIds.Damage,"(1)",EffectIds.Apply));
                    lines.Add(E("@Other#202",npc+"|(0/100)",EffectIds.Death));
                }
        }
        return lines;
    }
    private static BatchProjection Replay(IEnumerable<string> lines)
    {
        long offset=0;var records=lines.Select(l=>{var r=new SourceRecord(offset,offset+l.Length+1,l);offset=r.End;return r;});
        return new CombatReducer(Catalog,"UTC").Reduce(new(){Clock=new(){Date=new(2026,11,1)}},"source","session",records);
    }
    [Theory][InlineData("assault_on_tython")][InlineData("korriban_incursion")][InlineData("mandalorian_raiders")][InlineData("objective_meridian")][InlineData("black_talon")]
    public void EitherCompleteFactionRouteCountsWithoutBonusBosses(string id)
    {
        var activity=Catalog.Document.Activities.Single(a=>a.Id==id);
        foreach(var branch in new[]{0,1})
        {
            Assert.True(Assert.Single(Replay(Lines(activity,branch)).Runs.Values).Full);
            var partial=Assert.Single(Replay(Lines(activity,branch,true)).Runs.Values);
            Assert.True(partial.Terminal);Assert.False(partial.Full);
        }
    }
    [Fact]public void IlumRequiresBothFolexAndKrelThakAndExcludesTheBonusBoss()
    {
        var activity=Catalog.Document.Activities.Single(a=>a.Id=="battle_of_ilum");
        Assert.Contains(activity.RequiredGroups,g=>g.SequenceEqual(new[]{"krel_thak"}));
        Assert.Contains(activity.RequiredGroups,g=>g.SequenceEqual(new[]{"commander_folex"}));
        Assert.DoesNotContain(activity.RequiredGroups,g=>g.Contains("guid_patriarch"));
        Assert.True(Assert.Single(Replay(Lines(activity,0)).Runs.Values).Full);
    }
    [Fact]public void MalformedGapBlocksFullCoverageAndCannotCombinePartialBossDeaths()
    {
        var activity=Catalog.Document.Activities.Single(a=>a.Id=="cademimu");var lines=Lines(activity,0);
        lines.Insert(lines.Count-2,"malformed");var run=Assert.Single(Replay(lines).Runs.Values);
        Assert.True(run.Terminal);Assert.False(run.Full);
        lines=Lines(activity,0);lines.Insert(5,"malformed");
        Assert.DoesNotContain(Replay(lines).Attempts.Values,a=>a.EncounterId=="officer_xander_en4c"&&a.Result=="won");
    }
    [Theory][InlineData("eternity_vault")][InlineData("kuat_drive_yards")][InlineData("colicoid_war_game")][InlineData("kaon_under_siege")]
    public void IncompleteObjectiveCoverageCannotClaimAFullRun(string id)
    {Assert.False(Catalog.Document.Activities.Single(a=>a.Id==id).CompleteObjectives);}
    [Fact]public void BonusBossesDoNotEnterRequiredGroups()
    {
        foreach(var activity in Catalog.Document.Activities)
            foreach(var name in activity.RequiredGroups.SelectMany(g=>g))Assert.False(activity.Encounters.Single(e=>e.Id==name).Optional);
        Assert.True(Catalog.Document.Activities.Single(a=>a.Id=="a_traitor_among_the_chiss").Encounters.Single(e=>e.Id=="temple_cat").Optional);
    }
}
