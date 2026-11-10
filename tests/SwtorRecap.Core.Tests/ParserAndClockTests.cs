using SwtorRecap.Core;

namespace SwtorRecap.Core.Tests;

public sealed class ParserAndClockTests
{
    private static string Metric(string amount,string effect=EffectIds.Damage,string source="@Local#101",string target="Boss {123}:456")=>$"[23:59:59.999] [{source}] [{target}] [Localized {{1234}}] [Effekt {{{EffectIds.Apply}}}: Treffer {{{effect}}}] {amount}";
    [Theory]
    [InlineData("(5000 ~3500 kinetic {836045448940873} (1500 absorbed {836045448945511})) <99999>",5000,3500)]
    [InlineData("(41422 ~0 energy {836045448940874})",41422,0)]
    [InlineData("(5000*) <2500>",5000,5000)]
    [InlineData("(116010 kinetic {836045448940873}(reflected {836045448953649}))",116010,116010)]
    [InlineData("(2583* energy {836045448940874} -shield {836045448945509} (1150 absorbed {836045448945511}))",2583,2583)]
    [InlineData("(100 ~101)",100,101)]
    public void AmountsPreserveRawEffectiveAndDoNotCountNestedFields(string details,long raw,long effective)
    {var result=new CombatLogParser().Parse(Metric(details));Assert.Equal(ParseStatus.Accepted,result.Status);Assert.Equal(raw,result.Event!.Raw);Assert.Equal(effective,result.Event.Effective);}
    [Theory]
    [InlineData("(-1)")][InlineData("(999999999999999999999999999)")][InlineData("(10 ~-1)")][InlineData("(10 ~99999999999999999999)")][InlineData("(10")][InlineData("(abc)")]
    public void InvalidAmountsAreRejected(string details)=>Assert.Equal(ParseStatus.Malformed,new CombatLogParser().Parse(Metric(details)).Status);
    [Fact]public void LegacyEffectiveIsUnknown()=>Assert.Null(new CombatLogParser().Parse(Metric("(100)"),false).Event!.Effective);
    [Fact]public void AliasIsThisSourceNotLogOwner()
    {var e=new CombatLogParser().Parse(Metric("(100)",source:"@Other#202",target:"=")).Event!;Assert.Equal(e.Source,e.Target);Assert.Equal("p:202",e.Target.Key);}
    [Fact]public void CompanionHasDistinctIdentityAndExplicitOwner()
    {var a=CombatLogParser.ParseActor("@Élan#101/Temple {493328533553152}:87481369009487|(0,0,0,0)|(288866/288866)",Actor.Empty);Assert.Equal(ActorKind.Companion,a.Kind);Assert.Equal("101",a.CompanionOwnerId);Assert.Equal(288866,a.Hp);}
    [Theory][InlineData("16141082698337403481","16141082698337403481")][InlineData("000123","123")][InlineData("0","0")]
    public void GameIdsAreDecimalText(string input,string output)=>Assert.Equal(output,new GameId(input).Value);
    [Theory][InlineData("")][InlineData("-1")][InlineData("1.5")][InlineData("1e20")]
    public void InvalidIdentifiersNeverBecomeZero(string input)=>Assert.Throws<FormatException>(()=>new GameId(input));
    [Fact]public void LocalizedAreaPreservesFormatAndMetadata()
    {
        var e=new CombatLogParser().Parse("[18:28:08.183] [@Élan#101] [] [] [Gebiet {836045448953664}: Palais {137438993410} 8 Spieler, Meister {836045448953655}] (he4001) <v7.0.0b>").Event!;
        Assert.Equal("137438993410",e.AreaId);Assert.Equal(8,e.GroupSize);Assert.Equal("he4001",e.Server);Assert.Equal("v7.0.0b",e.Format);
    }
    [Fact]public void FutureFormatDoesNotPretendToBeSupported()=>Assert.Equal(ParseStatus.Unsupported,new CombatLogParser().Parse("[18:28:08.183] [@Local#101] [] [] [Area {836045448953664}: X {123}] (server) <v99>").Status);
    [Fact]public void UnknownSemanticIsNotMalformed()=>Assert.Equal(ParseStatus.UnknownSemantic,new CombatLogParser().Parse("[18:28:08.183] [] [] [] [New {123}: New {456}]").Status);
    [Fact]public void MultipleMidnightsAdvanceDates()
    {
        var clock=new LogClockState{Date=new(2025,12,31),SourceZone="UTC"};
        clock.Resolve(new(23,59,59),"UTC");Assert.Equal("2026-01-01",clock.Resolve(new(0,0,0),"UTC").ReportingDate);
        clock.Resolve(new(23,59,59),"UTC");Assert.Equal("2026-01-02",clock.Resolve(new(0,0,0),"UTC").ReportingDate);
    }
    [Fact]public void BackwardClockDoesNotInventAnotherDay()
    {var clock=new LogClockState{Date=new(2026,4,10),SourceZone="UTC"};clock.Resolve(new(12,0),"UTC");var t=clock.Resolve(new(11,0),"UTC");Assert.Equal("2026-04-10",t.ReportingDate);Assert.Equal("clock_adjusted",t.Quality);}
    [Fact]public void AmbiguousSourceInstantMayCrossReportingDate()
    {var clock=new LogClockState{Date=new(2026,11,1),SourceZone="America/Los_Angeles"};var t=clock.Resolve(new(1,30),"Etc/GMT+9");Assert.Null(t.Utc);Assert.Null(t.ReportingDate);Assert.Equal("ambiguous",t.Quality);}
    [Fact]public void InvalidSpringTimeStaysUnallocated()
    {var clock=new LogClockState{Date=new(2026,3,8),SourceZone="America/Los_Angeles"};Assert.Null(clock.Resolve(new(2,30),"UTC").ReportingDate);}
    [Fact]public void UndatedCaptureNeverGetsImportDay()=>Assert.Null(new LogClockState().Resolve(new(12,0),"UTC").ReportingDate);
    [Fact]public void SourceZoneIsIndependentOfReportingZone()
    {var clock=new LogClockState{Date=new(2026,1,1),SourceZone="Asia/Tokyo"};Assert.Equal("2025-12-31",clock.Resolve(new(0,30),"America/Los_Angeles").ReportingDate);}
    [Fact]public void IntervalsSplitAtNewYearWithoutDoubleCounting()
    {var pieces=CalendarAllocation.Split(new(2025,12,31,23,59,30,TimeSpan.Zero),new(2026,1,1,0,0,30,TimeSpan.Zero),"UTC").ToArray();Assert.Equal(2,pieces.Length);Assert.Equal(60000,pieces.Sum(p=>p.Milliseconds));Assert.Equal("2025-12-31",pieces[0].Date);Assert.Equal("2026-01-01",pieces[1].Date);}
    [Fact]public void CheckedTotalsRejectOverflow()
    {var amounts=new Amounts{Damage=long.MaxValue};Assert.Throws<OverflowException>(()=>amounts.Add(new Amounts{Damage=1}));}
    [Fact]public void CatalogRejectsExecutableRules()
    {var original=Catalog.LoadDefault().Document;var activity=original.Activities[0];var e=activity.Encounters[0] with{Rule="eval"};Assert.Throws<FormatException>(()=>new Catalog(original with{Activities=[activity with{Encounters=[e]}]}));}
}
