using SwtorRecap.Core;

namespace SwtorRecap.Core.Tests;

public sealed class NpcKillTests
{
    private const string Local="@Local#101", Other="@Other#202", Npc="Guard {999}:1";
    private static string E(string source,string target,string effect,string detail="",string type=EffectIds.Event)
        =>$"[20:00:00.000] [{source}] [{target}] [] [X {{{type}}}: Y {{{effect}}}] {detail}";
    private static string Hit(long raw,long effective,string target=Npc,string source=Local,string health="|(40/100)")
        =>E(source,target+health,EffectIds.Damage,$"({raw} ~{effective})",EffectIds.Apply);
    private static string Die(string target=Npc,string health="|(0/100)")=>E(Other,target+health,EffectIds.Death);
    private static SourceRecord[] Records(IEnumerable<string> lines)
    {long offset=0;return lines.Select(l=>{var r=new SourceRecord(offset,offset+l.Length+1,l);offset=r.End;return r;}).ToArray();}
    private static BatchProjection Reduce(params string[] lines)=>new CombatReducer(Catalog.LoadDefault(),"UTC").Reduce(
        new(){Clock=new(){Date=new(2026,11,1)}},"source","capture",Records(new[]{E(Local,"",EffectIds.Enter)}.Concat(lines)));
    private static Fact[] Kills(BatchProjection b)=>b.Facts.Where(f=>f.Kind=="npc_kill").ToArray();

    [Theory][InlineData(49,0)][InlineData(50,0)][InlineData(51,1)][InlineData(100,1)]
    public void StrictMajorityUsesEffectiveDamageAndDoesNotRequireFinalBlow(long damage,int count)
    {Assert.Equal(count,Kills(Reduce(Hit(9999,damage),Die())).Length);}
    [Fact]public void OddMaximumHealthUsesExactStrictMajority()
    {Assert.Single(Kills(Reduce(Hit(51,51,health:"|(50/101)"),Die(health:"|(0/101)"))));}
    [Fact]public void CompanionAndOtherPlayersDoNotIncreaseContribution()
    {Assert.Empty(Kills(Reduce(Hit(30,30),Hit(100,100,source:Other),Hit(100,100,source:"@Local#101/Companion {888}:2"),Die())));}
    [Theory][InlineData("@Other#202")][InlineData("@Other#202/Companion {888}:2")]
    public void PlayerAndCompanionTargetsAreExcluded(string target)
    {Assert.Empty(Kills(Reduce(Hit(100,100,target),Die(target))));}
    [Fact]public void BossTargetsCountIndividuallyButTrainingDummiesDoNot()
    {
        var catalog=Catalog.LoadDefault();
        foreach(var activity in catalog.Document.Activities)
            foreach(var template in activity.Encounters.SelectMany(e=>e.Roles.SelectMany(r=>r.TemplateIds)).Distinct())
                Assert.Equal(catalog.IsTraining(template)?0:1,Kills(Reduce(Hit(60,60,$"Boss {{{template}}}:1"),Die($"Boss {{{template}}}:1"))).Length);
    }
    [Fact]public void DyingBeforeGroupFinishesPreservesIndividualKillContribution()
    {
        Assert.Single(Kills(Reduce(Hit(60,60),E(Other,Local,EffectIds.Death),E(Local,"",EffectIds.Exit),Die())));
        Assert.Empty(Kills(Reduce(Hit(50,50),E(Other,Local,EffectIds.Death),E(Local,"",EffectIds.Exit),Die())));
    }
    [Fact]public void DeathIsRequiredAndRepeatedDeathIsNotAnotherKill()
    {Assert.Empty(Kills(Reduce(Hit(100,100,health:"|(0/100)"),E(Local,"",EffectIds.Exit))));Assert.Single(Kills(Reduce(Hit(60,60),Die(),Die())));}
    [Fact]public void HealthMustBeKnownAndConsistent()
    {
        Assert.Empty(Kills(Reduce(Hit(60,60,health:""),Die())));
        Assert.Empty(Kills(Reduce(Hit(60,60),Die(health:"|(0/200)"))));
        Assert.Empty(Kills(Reduce(Hit(60,60,health:"|(0/0)"),Die(health:"|(0/0)"))));
    }
    [Fact]public void SeparateSpawnsOfSameEnemyAreSeparateKills()
    {Assert.Equal(2,Kills(Reduce(Hit(60,60),Die(),Hit(60,60,"Guard {999}:2"),Die("Guard {999}:2"))).Length);}
    [Fact]public void NewCombatAndFullHealthResetCannotCombineOldContribution()
    {
        Assert.Empty(Kills(Reduce(Hit(40,40),E(Local,"",EffectIds.Exit),E(Local,"",EffectIds.Enter),Hit(20,20),Die())));
        Assert.Empty(Kills(Reduce(Hit(40,40),E(Other,Local,EffectIds.Death),E(Local,"",EffectIds.Enter),Hit(20,20),Die())));
        Assert.Empty(Kills(Reduce(Hit(40,40),E("",Npc+"|(100/100)","1"),Hit(20,20),Die())));
    }
    [Fact]public void DuplicateEnterDoesNotDiscardValidContribution()
    {Assert.Single(Kills(Reduce(Hit(30,30),E(Local,"",EffectIds.Enter),Hit(30,30),Die())));}
    [Fact]public void AreaOrOwnerChangesCannotCarryContribution()
    {
        Assert.Empty(Kills(Reduce(Hit(60,60),E(Local,"","123",type:EffectIds.Area),Die())));
        Assert.Empty(Kills(Reduce(Hit(60,60),E(Other,"","123",type:EffectIds.Area),Die())));
    }
    [Fact]public void CheckpointsKeepContributionAndDeathDeduplication()
    {
        var reducer=new CombatReducer(Catalog.LoadDefault(),"UTC");var state=new ReducerState{Clock=new(){Date=new(2026,11,1)}};
        var facts=new List<Fact>();
        foreach(var record in Records([E(Local,"",EffectIds.Enter),Hit(30,30),Hit(30,30),Die(),Die()]))
        {var b=reducer.Reduce(Json.Clone(state),"source","capture",[record]);facts.AddRange(b.Facts);state=b.State;}
        var kill=Assert.Single(facts,f=>f.Kind=="npc_kill");Assert.Contains("\"damage\":60",kill.Payload);
    }
    [Fact]public void InterruptedOrRejectedRecordsDropUnfinishedContribution()
    {
        var b=Reduce(Hit(60,60));CombatReducer.Interrupt(b.State,b,"pause");
        var reducer=new CombatReducer(Catalog.LoadDefault(),"UTC");
        Assert.Empty(Kills(reducer.Reduce(b.State,"source","capture",[new(b.End,b.End+100,Die())])));
        Assert.Empty(Kills(Reduce(Hit(60,60),"malformed",Die())));
    }
    [Fact]public void HealthParserRetainsBothValuesAndAlias()
    {
        var actor=CombatLogParser.ParseActor(Npc+"|(1,2,3,4)|(40/100)",Actor.Empty);
        Assert.Equal(40,actor.Hp);Assert.Equal(100,actor.MaxHp);Assert.Equal(actor,CombatLogParser.ParseActor("=",actor));
    }
}
