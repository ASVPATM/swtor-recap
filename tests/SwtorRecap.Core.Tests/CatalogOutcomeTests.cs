using SwtorRecap.Core;
namespace SwtorRecap.Core.Tests;
public sealed class CatalogOutcomeTests
{
    public static IEnumerable<object[]> DeathEncounters()=>Catalog.LoadDefault().Document.Activities
        .Where(a=>a.Type!="training").SelectMany(a=>a.Encounters.Where(e=>e.Rule=="death_all").Select(e=>new object[]{a.Id,e.Id}));
    private static SourceRecord[] Records(IEnumerable<string> lines)
    {long offset=0;return lines.Select(l=>{var r=new SourceRecord(offset,offset+l.Length+1,l);offset=r.End;return r;}).ToArray();}
    private static string E(string source,string target,string effect,string type=EffectIds.Event,string tail="")
        =>$"[20:00:00.000] [{source}] [{target}] [] [X {{{type}}}: Y {{{effect}}}] {tail}";
    [Theory][MemberData(nameof(DeathEncounters))]
    public void SharedDeathRuleCoversEveryCatalogDeathEncounter(string activityId,string encounterId)
    {
        var catalog=Catalog.LoadDefault();var a=catalog.Document.Activities.Single(a=>a.Id==activityId);var e=a.Encounters.Single(e=>e.Id==encounterId);
        var lines=new List<string>{E("@Local#101","",a.AreaIds[0],EffectIds.Area,"(test) <v7.0.0b>"),E("@Local#101","",EffectIds.Enter)};
        var roles=e.Roles.Where(r=>r.Required).ToArray();Assert.NotEmpty(roles);
        for(var i=0;i<roles.Length;i++)
        {
            var target=$"Boss {{{roles[i].TemplateIds[0]}}}:{i+1}";
            lines.Add(E("@Local#101",target+"|(99/100)",EffectIds.Damage,EffectIds.Apply,"(1)"));
            lines.Add(E("@Other#202",target+"|(0/100)",EffectIds.Death));
        }
        var reducer=new CombatReducer(catalog,"UTC");
        var b=reducer.Reduce(new(){Clock=new(){Date=new(2026,11,1)}},"source","session",Records(lines));
        var attempt=Assert.Single(b.Attempts.Values);Assert.Equal("won",attempt.Result);Assert.True(attempt.Qualified);
        Assert.Equal(e.Final,Assert.Single(b.Runs.Values).Terminal);Assert.DoesNotContain(b.Facts,f=>f.Kind=="npc_kill");
        // Missing the final required death must never become a win on combat exit.
        lines.RemoveAt(lines.Count-1);lines.Add(E("@Local#101","",EffectIds.Exit));
        var missing=reducer.Reduce(new(){Clock=new(){Date=new(2026,11,1)}},"source","session",Records(lines));
        Assert.DoesNotContain(missing.Attempts.Values,a=>a.Result=="won");
        // Seeing a group defeat without local damage/healing is not personal credit.
        lines=lines.Where(l=>!l.Contains(EffectIds.Damage)).ToList();
        var observer=reducer.Reduce(new(){Clock=new(){Date=new(2026,11,1)}},"source","session",Records(lines));
        Assert.DoesNotContain(observer.Attempts.Values,a=>a.Qualified);
    }
    [Fact]public void UnsupportedEndingIsReportedWithoutInventingVictory()
    {
        var catalog=Catalog.LoadDefault();var a=catalog.Document.Activities.First(a=>a.Encounters.Any(e=>e.Rule=="unsupported"&&e.Roles.Length>0));var e=a.Encounters.First(e=>e.Rule=="unsupported"&&e.Roles.Length>0);
        var target=$"Boss {{{e.Roles[0].TemplateIds[0]}}}:1";
        var b=new CombatReducer(catalog,"UTC").Reduce(new(){Clock=new(){Date=new(2026,11,1)}},"source","session",Records([
            E("@Local#101","",a.AreaIds[0],EffectIds.Area),E("@Local#101","",EffectIds.Enter),E("@Local#101",target+"|(99/100)",EffectIds.Damage,EffectIds.Apply,"(1)"),E("@Local#101","",EffectIds.Exit)]));
        Assert.Contains(b.Attempts.Values,a=>a.Result=="unconfirmed"&&a.Qualified);Assert.DoesNotContain(b.Runs.Values,r=>r.Terminal);
    }
}
