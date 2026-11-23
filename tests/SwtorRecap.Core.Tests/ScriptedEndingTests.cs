using SwtorRecap.Core;

namespace SwtorRecap.Core.Tests;

public sealed class ScriptedEndingTests
{
    private const string Local="@Local#101", Other="@Other#202";
    private static readonly Catalog Catalog=Catalog.LoadDefault();
    private static string E(string source,string target,string effect,string detail="",string type=EffectIds.Event,string ability="",string time="20:00:01.000")
        =>$"[{time}] [{source}] [{target}] [{ability}] [X {{{type}}}: Y {{{effect}}}] {detail}";
    private static string Area(ActivityDefinition a,int size=8)=>E(Local,"",a.AreaIds[0],type:EffectIds.Area)
        .Replace($"Y {{{a.AreaIds[0]}}}]",$"Y {{{a.AreaIds[0]}}} {size} Player Story {{836045448953651}}]");
    private static string Npc(EntityRole role,int spawn=1,int hp=90)=>$"Boss {{{role.TemplateIds[0]}}}:{spawn}|({hp}/100)";
    private static BatchProjection Replay(IEnumerable<string> lines,bool checkpoint=true)
    {
        long offset=0;var records=lines.Select(l=>{var r=new SourceRecord(offset,offset+l.Length+1,l);offset=r.End;return r;}).ToArray();
        var reducer=new CombatReducer(Catalog,"UTC");var state=new ReducerState{Clock=new(){Date=new(2026,11,1)}};
        if(!checkpoint)return reducer.Reduce(state,"source","session",records);
        var result=new BatchProjection{State=state};
        foreach(var record in records)
        {
            var b=reducer.Reduce(Json.Clone(state),"source","session",[record]);state=b.State;
            foreach(var(id,a)in b.Attempts)result.Attempts[id]=a;
            foreach(var(id,r)in b.Runs)result.Runs[id]=r;
            result.Facts.AddRange(b.Facts);
        }
        return result;
    }
    public static IEnumerable<object[]> Thresholds()=>Catalog.Document.Activities.SelectMany(a=>a.Encounters
        .Where(e=>e.Rule=="hp_threshold_exit").Select(e=>new object[]{a.Id,e.Id}));
    private static (ActivityDefinition,EncounterDefinition,List<string>) Prefix(string activity,string encounter)
    {
        var a=Catalog.Document.Activities.Single(a=>a.Id==activity);var e=a.Encounters.Single(e=>e.Id==encounter);
        var lines=new List<string>{Area(a),E(Local,"",EffectIds.Enter)};
        foreach(var role in e.Roles.Where(r=>r.Required))
        {
            lines.Add(E(Local,Npc(role),EffectIds.Damage,"(10)",EffectIds.Apply));
            if(role.Id!=e.Ending?.RoleId)lines.Add(E(Other,Npc(role,hp:0),EffectIds.Death));
        }
        return(a,e,lines);
    }
    private static string[] Finish(EncounterDefinition encounter,bool signal=true)
    {
        var ending=encounter.Ending!;var role=encounter.Roles.Single(r=>r.Id==ending.RoleId);var boss=Npc(role,hp:ending.HealthPercent);
        var lines=new List<string>{E(Local,boss,EffectIds.Damage,"(80)",EffectIds.Apply)};
        if(signal && ending.AbilityIds is {Length:>0})lines.Add(E(boss,"=","836045448945479",ability:$"Finish {{{ending.AbilityIds[0]}}}"));
        if(signal && ending.RequireImmune)lines.Add(E(Local,boss,EffectIds.Damage,"(0 -immune {836045448945506})",EffectIds.Apply));
        lines.Add(E(Local,"",EffectIds.Exit));return lines.ToArray();
    }
    [Theory][MemberData(nameof(Thresholds))]
    public void ConfiguredEndingCountsOnceWithClosingEvidenceAndSurvivesCheckpoints(string activity,string encounter)
    {
        var(_,e,lines)=Prefix(activity,encounter);lines.AddRange(Finish(e));lines.Add(E(Local,"",EffectIds.Exit));
        foreach(var checkpoint in new[]{false,true})
        {
            var b=Replay(lines,checkpoint);var win=Assert.Single(b.Attempts.Values);
            Assert.Equal("won",win.Result);Assert.Equal("inferred",win.Basis);Assert.True(win.Qualified);
            Assert.DoesNotContain(b.Facts,f=>f.Kind=="npc_kill"&&f.Entity.Contains(e.Roles.Single(r=>r.Id==e.Ending!.RoleId).TemplateIds[0]));
        }
    }
    [Theory][MemberData(nameof(Thresholds))]
    public void LowHealthAndExitWithoutEndingSignalCannotWin(string activity,string encounter)
    {var(_,e,lines)=Prefix(activity,encounter);lines.AddRange(Finish(e,false));Assert.DoesNotContain(Replay(lines).Attempts.Values,a=>a.Result=="won");}
    [Theory][InlineData("death")][InlineData("reset")][InlineData("missing_health")][InlineData("changed_maximum")][InlineData("health_lost_after_candidate")][InlineData("timeout")][InlineData("gap")][InlineData("wrong_actor")][InlineData("wrong_id")][InlineData("exit_before_threshold")][InlineData("no_exit")]
    public void RevanWipesGapsAndUnrelatedRecordsCannotFinishTheFight(string failure)
    {
        var(_,e,lines)=Prefix("foundry","mysterious_jedi");var finish=Finish(e).ToList();var role=e.Roles[0];
        switch(failure)
        {
            case "death":finish.Insert(0,E(Other,Local,EffectIds.Death));break;
            case "reset":finish.Insert(finish.Count-1,E(Npc(role,hp:100),"=","123"));break;
            case "missing_health":finish=finish.Select(l=>l.Replace("|(5/100)","")).ToList();break;
            case "changed_maximum":finish=finish.Select(l=>l.Replace("|(5/100)","|(5/200)")).ToList();break;
            case "health_lost_after_candidate":finish[1]=finish[1].Replace("|(5/100)","");break;
            case "timeout":finish[^1]=finish[^1].Replace("20:00:01.000","20:02:00.000");break;
            case "gap":finish.Insert(finish.Count-1,"malformed");break;
            case "wrong_actor":finish[1]=finish[1].Replace(Npc(role,hp:5),Other);break;
            case "wrong_id":finish[1]=finish[1].Replace(e.Ending!.AbilityIds![0],"123");break;
            case "exit_before_threshold":lines.Add(E(Local,"",EffectIds.Exit,time:"20:00:00.999"));finish.RemoveAt(finish.Count-1);break;
            case "no_exit":finish.RemoveAt(finish.Count-1);break;
        }
        lines.AddRange(finish);Assert.DoesNotContain(Replay(lines).Attempts.Values,a=>a.Result=="won");
    }
    [Fact]public void ARecordedSurvivingGroupExitCanFinishAfterLocalDeath()
    {
        var(_,e,lines)=Prefix("foundry","mysterious_jedi");
        lines.Add(E(Other,Npc(e.Roles[0]),EffectIds.Damage,"(10)",EffectIds.Apply));
        lines.Add(E(Other,Local,EffectIds.Death));lines.AddRange(Finish(e));
        Assert.DoesNotContain(Replay(lines).Attempts.Values,a=>a.Result=="won");
        lines.Add(E(Other,"",EffectIds.Exit));Assert.True(Assert.Single(Replay(lines).Attempts.Values).Qualified);
    }
    [Fact]public void RenewedBossHealthRetractsAnInferredClear()
    {
        var(_,e,lines)=Prefix("foundry","mysterious_jedi");lines.AddRange(Finish(e));
        lines.Add(E(Npc(e.Roles[0],hp:90),"=","123"));var b=Replay(lines);
        Assert.DoesNotContain(b.Attempts.Values,a=>a.Qualified);Assert.False(Assert.Single(b.Runs.Values).Terminal);
    }
    [Theory][InlineData("(0 -immune {123})")][InlineData("(1 -immune {836045448945506})")][InlineData("(0)")]
    public void AnImmunityLabelOrZeroDamageAloneCannotEndMalgus(string detail)
    {
        var(_,e,lines)=Prefix("objective_meridian","darth_malgus");
        var finish=Finish(e);finish[1]=finish[1].Replace("(0 -immune {836045448945506})",detail);
        lines.AddRange(finish);Assert.DoesNotContain(Replay(lines).Attempts.Values,a=>a.Result=="won");
    }
    [Fact]public void CoratanniEvidenceCannotSurviveASecondPullAfterRuugarWasSeen()
    {
        var(_,e,lines)=Prefix("the_ravagers","coratanni");lines.Add(E(Local,"",EffectIds.Exit));lines.Add(E(Local,"",EffectIds.Enter));
        lines.AddRange(Finish(e));Assert.DoesNotContain(Replay(lines).Attempts.Values,a=>a.Result=="won");
    }
    [Fact]public void CoratanniPhaseBreakIsNotAWinAndPearlRemainsRequired()
    {
        var a=Catalog.Document.Activities.Single(a=>a.Id=="the_ravagers");var e=a.Encounters.Single(e=>e.Id=="coratanni");
        var cora=e.Roles[0];var ruugar=e.Roles[1];var pearl=e.Roles[2];
        var lines=new List<string>{Area(a),E(Local,"",EffectIds.Enter),E(Local,Npc(cora),EffectIds.Damage,"(10)",EffectIds.Apply),
            E(Other,Npc(pearl,hp:0),EffectIds.Death),E(Local,"",EffectIds.Exit)};
        Assert.DoesNotContain(Replay(lines).Attempts.Values,a=>a.Result=="won");
        lines.AddRange([E(Local,"",EffectIds.Enter),E(Local,Npc(ruugar),EffectIds.Damage,"(10)",EffectIds.Apply),..Finish(e)]);
        Assert.Equal("won",Assert.Single(Replay(lines).Attempts.Values).Result);
        lines.RemoveAt(3);Assert.DoesNotContain(Replay(lines).Attempts.Values,a=>a.Result=="won");
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void LootSignalNeedsParticipationAndExitAndCanFollowAnExit(bool after)
    {
        var a=Catalog.Document.Activities.Single(a=>a.Id=="dxun");var e=a.Encounters.Single(e=>e.Id=="holding_pens");
        var lines=new List<string>{Area(a),E(Local,"",EffectIds.Enter),E(Local,Npc(e.Roles[0]),EffectIds.Damage,"(10)",EffectIds.Apply)};
        var loot=E("Chest {4340614208421888}:2|(100/100)",Local,"836045448953668");
        if(after)lines.AddRange([E(Local,"",EffectIds.Exit),E(Local,"",EffectIds.Enter),loot]);
        else lines.AddRange([loot,E(Local,"",EffectIds.Exit)]);
        Assert.Equal("won",Assert.Single(Replay(lines).Attempts.Values).Result);
        Assert.DoesNotContain(Replay(lines.Where(l=>!l.Contains(EffectIds.Exit))).Attempts.Values,a=>a.Result=="won");
        Assert.DoesNotContain(Replay(lines.Select(l=>l.Replace("4340614208421888","999"))).Attempts.Values,a=>a.Result=="won");
        Assert.DoesNotContain(Replay(lines.Select(l=>l.Contains(EffectIds.Damage)?l.Replace(Local,Other):l)).Attempts.Values,a=>a.Qualified);
    }
    [Theory][InlineData(8,8,true)][InlineData(8,7,false)][InlineData(16,16,true)][InlineData(16,8,false)]
    public void CouncilNeedsTheWholeRecordedGroupRoster(int size,int deaths,bool won)
    {
        var a=Catalog.Document.Activities.Single(a=>a.Id=="eternity_vault");var e=a.Encounters.Single(e=>e.Id=="infernal_council");
        var lines=new List<string>{Area(a,size),E(Local,"",EffectIds.Enter)};
        for(var i=0;i<size;i++)lines.Add(E(Local,Npc(e.Roles[0],i+1),EffectIds.Damage,"(10)",EffectIds.Apply));
        for(var i=0;i<deaths;i++)lines.Add(E(Other,Npc(e.Roles[0],i+1,0),EffectIds.Death));
        // These removals occur on wipes too and must never fill missing deaths.
        lines.Add(E(Local,"=","2508531483803912",type:EffectIds.Remove));lines.Add(E(Local,"",EffectIds.Exit));
        Assert.Equal(won,Assert.Single(Replay(lines).Attempts.Values).Result=="won");
    }
    [Fact]public void LorrickHumanPhaseCannotCountAsTheFinalDefeat()
    {
        var a=Catalog.Document.Activities.Single(a=>a.Id=="lost_island");var e=a.Encounters.Single(e=>e.Id=="doctor_lorrick");
        var lines=new List<string>{Area(a),E(Local,"",EffectIds.Enter),E(Local,Npc(e.Roles[0]),EffectIds.Damage,"(10)",EffectIds.Apply),E(Other,Npc(e.Roles[0],hp:0),EffectIds.Death)};
        Assert.DoesNotContain(Replay(lines).Attempts.Values,a=>a.Result=="won");
        lines.Add(E(Other,Npc(e.Roles[1],hp:0),EffectIds.Death));
        Assert.Equal("won",Assert.Single(Replay(lines).Attempts.Values).Result);
    }
}
