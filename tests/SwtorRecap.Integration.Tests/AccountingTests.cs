using System.Text;
using SwtorRecap.Core;
using SwtorRecap.Engine;
using SwtorRecap.Storage;
using Microsoft.Data.Sqlite;

namespace SwtorRecap.Integration.Tests;

public sealed class AccountingTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"swtor-test-"+Guid.NewGuid().ToString("N"));
    private readonly Catalog _catalog=Catalog.LoadDefault();
    private const string Enter="[20:00:00.000] [@Local#101] [] [] [Event {836045448945472}: EnterCombat {836045448945489}]";
    private const string Damage="[20:00:01.000] [@Local#101] [Boss {999}:1] [] [ApplyEffect {836045448945477}: Damage {836045448945501}] (100 ~80)";
    public AccountingTests()=>Directory.CreateDirectory(_root);
    public void Dispose()=>Directory.Delete(_root,true);
    private (RecapStore Store,IngestionEngine Engine,string Source,string Container,string Path) Setup(string? text=null,bool sealedSource=false)
    {
        var store=new RecapStore(Path.Combine(_root,"data"),"UTC");var engine=new IngestionEngine(store,_catalog);var path=Path.Combine(_root,"combat_2026-11-01_20_00_00_000000.txt");File.WriteAllText(path,text??Enter+"\r\n"+Damage+"\r\n",new UTF8Encoding(false));var container=store.CreateContainer("import");var source=engine.Register(container,path,sealedSource,sourceZone:"UTC");return(store,engine,source,container,path);
    }
    [Fact]public void LiveTailAndDuplicateSignalsCommitOnce()
    {var (s,e,id,c,p)=Setup(Enter+"\n"+Damage);e.ReadAll(id);Assert.Equal(0,s.Totals(new(),_catalog).Amounts!.Damage);Assert.True(s.LoadSource(id).Offset<new FileInfo(p).Length);File.AppendAllText(p,"\n");e.ReadAll(id);e.ReadAll(id);Assert.Equal(100,s.Totals(new(),_catalog).Amounts!.Damage);Assert.Equal(new FileInfo(p).Length,s.LoadSource(id).Offset);}
    [Fact]public void SealedImportAcceptsValidUnterminatedLastRecord()
    {var(s,e,id,_,_)=Setup(Enter+"\n"+Damage,true);e.ReadAll(id);Assert.Equal(100,s.Totals(new(),_catalog).Amounts!.Damage);}
    [Fact]public void ReadingReprocessingAndAutomaticBackupLeaveCombatLogUntouched()
    {
        var(s,e,id,_,path)=Setup(sealedSource:true);var bytes=File.ReadAllBytes(path);var written=File.GetLastWriteTimeUtc(path);
        using(var reader=ByteFramer.OpenRead(path)){Assert.False(reader.CanWrite);e.ReadOnce(id,reader);}
        new HistoryMaintenance(s,_catalog).Rebuild();s.DailyBackup();
        Assert.Equal(bytes,File.ReadAllBytes(path));Assert.Equal(written,File.GetLastWriteTimeUtc(path));
        Assert.Single(Directory.GetFiles(_root));Assert.True(Directory.Exists(Path.Combine(s.Root,"backups")));
    }
    [Fact]public void IdenticalLinesAtDifferentOffsetsBothCount()
    {var(s,e,id,_,_)=Setup(Enter+"\n"+Damage+"\n"+Damage+"\n");e.ReadAll(id);Assert.Equal(200,s.Totals(new(),_catalog).Amounts!.Damage);}
    [Theory][InlineData("begin")][InlineData("checkpoint")][InlineData("metrics")][InlineData("evidence")][InlineData("before_commit")]
    public void FailureRollsBackCursorCountersAndReducer(string phase)
    {
        var(s,e,id,_,_)=Setup();s.Failpoint=p=>{if(p==phase)throw new IOException("Injected failure");};Assert.Throws<IOException>(()=>e.ReadOnce(id));Assert.Equal(0,s.LoadSource(id).Offset);Assert.Null(s.LoadSource(id).State.Owner);
        s.Failpoint=null;e.ReadAll(id);Assert.Equal(100,s.Totals(new(),_catalog).Amounts!.Damage);s.Verify();
    }
    [Fact]public void LostCommitAcknowledgmentReloadsDurableOffset()
    {var(s,e,id,_,_)=Setup();s.Failpoint=p=>{if(p=="after_commit")throw new IOException("Lost acknowledgment");};Assert.Throws<IOException>(()=>e.ReadOnce(id));s.Failpoint=null;e.ReadAll(id);Assert.Equal(100,s.Totals(new(),_catalog).Amounts!.Damage);}
    [Fact]public void StaleCheckpointCannotApplyAdditiveEffects()
    {var(s,e,id,_,_)=Setup();var old=s.LoadSource(id);e.ReadAll(id);var batch=new BatchProjection{State=old.State,End=100};Assert.Throws<InvalidOperationException>(()=>s.Commit(old,batch,100,0,""));Assert.Equal(100,s.Totals(new(),_catalog).Amounts!.Damage);}
    [Fact]public void TruncateAndRegrowDoesNotReuseCursor()
    {var(s,e,id,_,p)=Setup();e.ReadAll(id);File.WriteAllText(p,Enter+"\n"+Damage.Replace("100 ~80","900 ~80")+"\n"+Damage+"\n");Assert.Equal(0,e.ReadOnce(id));Assert.Equal("source_replaced_or_truncated",s.LoadSource(id).Integrity);Assert.Equal(100,s.Totals(new(),_catalog).Amounts!.Damage);}
    [Fact]public void ShortPrefixHashKeepsItsOriginalLengthOnAppend()
    {var(s,e,id,_,p)=Setup(Enter+"\n");var count=s.LoadSource(id).PrefixLength;e.ReadAll(id);File.AppendAllText(p,Damage+"\n");e.ReadAll(id);Assert.Equal(count,s.LoadSource(id).PrefixLength);Assert.Equal(100,s.Totals(new(),_catalog).Amounts!.Damage);}
    [Fact]public void RenameRetainedHandleDrainsOriginalBytes()
    {var(s,e,id,_,p)=Setup(Enter+"\n");using var handle=ByteFramer.OpenRead(p);e.ReadOnce(id,handle);var renamed=p+".moved";File.Move(p,renamed);File.AppendAllText(renamed,Damage+"\n");e.ReadOnce(id,handle);Assert.Equal(100,s.Totals(new(),_catalog).Amounts!.Damage);}
    [Fact]public void PausePartialLineDoesNotLeakIntoResumedMetrics()
    {var(s,e,id,_,p)=Setup(Enter+"\n"+Damage+"\n");e.ReadAll(id);File.AppendAllText(p,Damage[..40]);e.SkipToCurrent(id);File.AppendAllText(p,Damage[40..]+"\n"+Damage+"\n");e.ReadAll(id);Assert.Equal(200,s.Totals(new(),_catalog).Amounts!.Damage);Assert.Contains(s.Detail(s.LoadSource(id).ContainerId,_catalog).Gaps,g=>g.Reason=="paused_bytes");}
    [Fact]public void OversizedLineRecoversAtDelimiter()
    {var(s,e,id,_,_)=Setup(Enter+"\n"+new string('x',70000)+"\n"+Damage+"\n");e.ReadAll(id);Assert.Equal(100,s.Totals(new(),_catalog).Amounts!.Damage);Assert.Contains(s.Detail(s.LoadSource(id).ContainerId,_catalog).Gaps,g=>g.Reason=="oversized_record");}
    [Fact]public void BackupIncludesWalAndRestoreChangesEpoch()
    {var(s,e,id,_,_)=Setup();e.ReadAll(id);var backup=Path.Combine(_root,"backup.db");s.Backup(backup);var epoch=s.Revision.HistoryEpoch;s.ActivateStaging(backup,s.Revision,true,true);Assert.NotEqual(epoch,s.Revision.HistoryEpoch);Assert.Equal(100,s.Totals(new(),_catalog).Amounts!.Damage);s.Verify();}
    [Fact]public async Task DuplicateAndRenamedImportAreNoOps()
    {
        var path=Path.Combine(_root,"combat_2026-11-01_20_00_00_000000.txt");File.WriteAllText(path,Enter+"\n"+Damage+"\n");var s=new RecapStore(Path.Combine(_root,"data"),"UTC");var maintenance=new HistoryMaintenance(s,_catalog);
        await maintenance.ImportAsync("one",new([path],"UTC"));var renamed=Path.Combine(_root,"copy.txt");File.Copy(path,renamed);
        await maintenance.ImportAsync("two",new([renamed],"UTC","2026-11-01"));Assert.Equal(100,s.Totals(new(),_catalog).Amounts!.Damage);Assert.Single(s.SourceIds());Assert.Equal(0,s.Totals(new(),_catalog).Sessions);
    }
    [Fact]public async Task DifferentEstablishedDatesKeepIdenticalBytesDistinct()
    {
        var path=Path.Combine(_root,"capture.txt");File.WriteAllText(path,Enter+"\n"+Damage+"\n");var s=new RecapStore(Path.Combine(_root,"data"),"UTC");var maintenance=new HistoryMaintenance(s,_catalog);
        await maintenance.ImportAsync("one",new([path],"UTC","2026-11-01"));await maintenance.ImportAsync("two",new([path],"UTC","2026-11-02"));Assert.Equal(200,s.Totals(new(),_catalog).Amounts!.Damage);
    }
    [Fact]public void RebuildReplacesInsteadOfAdding()
    {var(s,e,id,_,_)=Setup(sealedSource:true);e.ReadAll(id);var before=s.Totals(new(),_catalog);new HistoryMaintenance(s,_catalog).Rebuild();Assert.Equal(before.Amounts!.Damage,s.Totals(new(),_catalog).Amounts!.Damage);s.Verify();}
    [Fact]public void NpcKillsPersistFilterAndReprocessWithoutDuplicates()
    {
        var hit=Damage.Replace("Boss {999}:1","Guard {999}:1|(40/100)").Replace("100 ~80","90 ~60");
        var death="[20:00:02.000] [@Other#202] [Guard {999}:1|(0/100)] [] [Event {836045448945472}: Death {836045448945493}]";
        var(s,e,id,c,_)=Setup(Enter+"\n"+hit+"\n"+death+"\n"+death+"\n",true);e.ReadAll(id);e.ReadAll(id);
        Assert.Equal(1,s.Totals(new(),_catalog).Kills);Assert.Equal(1,s.Recap(new(Year:2026,Month:11),_catalog).Totals.Kills);
        Assert.Equal(0,s.Recap(new(Year:2026,Month:10),_catalog).Totals.Kills);
        Assert.Equal(0,s.Totals(new(CharacterId:"other"),_catalog).Kills);
        Assert.Equal("Guard",Assert.Single(s.Detail(c,_catalog).Kills!).Name);
        new HistoryMaintenance(s,_catalog).Rebuild();
        Assert.Equal(1,s.Totals(new(),_catalog).Kills);Assert.False(s.Totals(new(),_catalog).KillsNeedReprocess);
        var backup=Path.Combine(_root,"kills-backup.db");s.Backup(backup);s.ActivateStaging(backup,s.Revision,true,true);
        Assert.Equal(1,s.Totals(new(),_catalog).Kills);s.Verify();
    }
    [Fact]public void OlderMetricsAreMarkedUntilReprocessingCompletes()
    {
        var(s,e,id,_,_)=Setup(sealedSource:true);e.ReadAll(id);
        using(var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=s.DatabasePath,Pooling=false}.ToString()))
        {c.Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE projection_sets SET metric_version='1.0.0'; UPDATE source_generations SET parser_version='1.0.1'";cmd.ExecuteNonQuery();}
        Assert.True(s.Totals(new(),_catalog).KillsNeedReprocess);
        new HistoryMaintenance(s,_catalog).Rebuild();Assert.False(s.Totals(new(),_catalog).KillsNeedReprocess);
    }
    [Fact]public void StaleStagingActivationLeavesHistoryUntouched()
    {var(s,e,id,_,_)=Setup();e.ReadAll(id);var backup=Path.Combine(_root,"stage.db");s.Backup(backup);var expected=s.Revision;s.UpdateSettings(expected.SettingsRevision,s.Settings with{Paused=true});Assert.Throws<InvalidOperationException>(()=>s.ActivateStaging(backup,expected,true));Assert.True(s.Settings.Paused);Assert.Equal(100,s.Totals(new(),_catalog).Amounts!.Damage);}
    [Theory][InlineData("activation_begin")][InlineData("activation_source_segments")][InlineData("activation_before_commit")]
    public void ActivationFailureLeavesCompleteOldClosure(string phase)
    {var(s,e,id,_,_)=Setup();e.ReadAll(id);var backup=Path.Combine(_root,"stage.db");s.Backup(backup);var revision=s.Revision;s.Failpoint=p=>{if(p==phase)throw new IOException("Injected");};Assert.Throws<IOException>(()=>s.ActivateStaging(backup,revision,true));Assert.Equal(revision,s.Revision);Assert.Equal(100,s.Totals(new(),_catalog).Amounts!.Damage);s.Verify();}
    [Fact]public void UnknownNewerSchemaIsNotModified()
    {var(s,_,_,_,_)=Setup();using(var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=s.DatabasePath,Pooling=false}.ToString())){c.Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE app_state SET schema_version=999";cmd.ExecuteNonQuery();}var before=File.ReadAllBytes(s.DatabasePath);Assert.Throws<InvalidOperationException>(()=>new RecapStore(s.Root));Assert.Equal(before,File.ReadAllBytes(s.DatabasePath));}
    [Fact]public void PreviewDataDirectoryIsRequired()=>Assert.Throws<ArgumentException>(()=>DataPaths.Isolated(null));
    [Fact]public void SingleWriterGuardRejectsDuplicate()=>AssertGuard();
    private void AssertGuard(){using var one=new WriterGuard(_root);Assert.Throws<IOException>(()=>new WriterGuard(_root));}
    [Fact]public void UnattributedAmountsRemainUnavailable()
    {var(s,e,id,_,_)=Setup(Damage+"\n");e.ReadAll(id);Assert.Null(s.Totals(new(),_catalog).Amounts);}
    [Fact]public void NoImportDateDoesNotPopulateToday()
    {var(s,e,id,_,_)=Setup();using(var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=s.DatabasePath,Pooling=false}.ToString())){c.Open();using var cmd=c.CreateCommand();var state=s.LoadSource(id).State;state.Clock.Date=null;cmd.CommandText="UPDATE engine_checkpoint SET state_json=$json";cmd.Parameters.AddWithValue("$json",Json.Write(state));cmd.ExecuteNonQuery();}e.ReadAll(id);Assert.Equal(100,s.Totals(new(),_catalog).Amounts!.Damage);Assert.Equal(0,s.Recap(new(Year:2026),_catalog).Totals.Amounts!.Damage);}
    [Fact]public void ReprocessingPreservesPausedRangesAndPartialBoundary()
    {
        var(s,e,id,_,p)=Setup();e.ReadAll(id);File.AppendAllText(p,Damage+"\n"+Damage[..40]);e.SkipToCurrent(id);
        File.AppendAllText(p,Damage[40..]+"\n"+Damage+"\n");e.ReadAll(id);
        Assert.Equal(200,s.Totals(new(),_catalog).Amounts!.Damage);
        new HistoryMaintenance(s,_catalog).Rebuild();Assert.Equal(200,s.Totals(new(),_catalog).Amounts!.Damage);s.Verify();
    }
    [Fact]public void OwnerChoiceUsesRetainedCandidatesAndSurvivesLaterReprocessing()
    {
        var(s,e,id,c,_)=Setup(Damage+"\n",true);e.ReadAll(id);
        var candidate=Assert.Single(Assert.Single(s.Detail(c,_catalog).Sources).Candidates!);Assert.Equal("101",candidate.Id);Assert.Equal("Local",candidate.Name);
        var maintenance=new HistoryMaintenance(s,_catalog);maintenance.Rebuild(ownerSource:id,ownerId:candidate.Id);
        Assert.Equal(100,s.Totals(new(),_catalog).Amounts!.Damage);maintenance.Rebuild();Assert.Equal(100,s.Totals(new(),_catalog).Amounts!.Damage);
    }
    [Fact]public async Task UnknownDateCopyRequiresReviewInsteadOfAddingTwice()
    {
        var(s,_,_,_,p)=Setup();var target=new RecapStore(Path.Combine(_root,"imports"),"UTC");var maintenance=new HistoryMaintenance(target,_catalog);
        await maintenance.ImportAsync("dated",new([p],"UTC"));var renamed=Path.Combine(_root,"renamed.txt");File.Copy(p,renamed);
        await maintenance.ImportAsync("unknown",new([renamed],"UTC"));Assert.Equal(100,target.Totals(new(),_catalog).Amounts!.Damage);
        Assert.Equal("overlap_review",target.Jobs().Single(j=>j.Id=="unknown").State);
    }
    [Fact]public async Task ImportPublishesThroughTheSharedWriterGate()
    {
        var(_,_,_,_,p)=Setup();var store=new RecapStore(Path.Combine(_root,"imports"),"UTC");using var gate=new SemaphoreSlim(0,1);
        var import=new HistoryMaintenance(store,_catalog,gate).ImportAsync("gate",new([p],"UTC"));
        Assert.Empty(store.SourceIds());Assert.False(import.IsCompleted);gate.Release();await import;Assert.Single(store.SourceIds());
    }
    [Fact]public void ReportingZoneRebuildMovesGameTimeAcrossYearBoundary()
    {
        var(s,e,id,_,_)=Setup(sealedSource:true);e.ReadAll(id);
        var start=new DateTimeOffset(2027,1,1,0,0,0,TimeSpan.Zero);var session=s.CreateContainer("session",start:start);
        s.RecordAwake(session,start,start.AddHours(1),3600000,"UTC");s.FinalizeSession(session,start.AddHours(1),"test");
        Assert.Equal(0,s.Recap(new(Year:2026),_catalog).Totals.GameMilliseconds);
        new HistoryMaintenance(s,_catalog).Rebuild("America/Los_Angeles");
        Assert.Equal(3600000,s.Recap(new(Year:2026),_catalog).Totals.GameMilliseconds);Assert.Equal(0,s.Recap(new(Year:2027),_catalog).Totals.GameMilliseconds);
    }
    [Fact]public void IntegerOverflowRollsBackTheWholeNewBatch()
    {
        var(s,e,id,_,p)=Setup(Enter+"\n"+Damage.Replace("100 ~80",long.MaxValue+" ~80")+"\n");e.ReadAll(id);var offset=s.LoadSource(id).Offset;
        File.AppendAllText(p,Damage+"\n");Assert.Throws<SqliteException>(()=>e.ReadOnce(id));
        Assert.Equal(offset,s.LoadSource(id).Offset);Assert.Equal(long.MaxValue,s.Totals(new(),_catalog).Amounts!.Damage);
    }
    [Fact]public void IncompleteTailGrowthIsVisibleEvenWithoutAnotherCompleteRecord()
    {
        var(s,e,id,c,p)=Setup();e.ReadAll(id);File.AppendAllText(p,Damage[..20]);e.ReadAll(id);e.FinalizeSource(id);
        Assert.Equal(new FileInfo(p).Length,s.LoadSource(id).Length);Assert.Contains(s.Detail(c,_catalog).Gaps,g=>g.Reason=="incomplete_live_tail");
    }
    [Fact]public async Task LongerVerifiedCopyReplacesItsPrefixInsteadOfDuplicatingIt()
    {
        var(_,_,_,_,p)=Setup();var store=new RecapStore(Path.Combine(_root,"imports"),"UTC");var maintenance=new HistoryMaintenance(store,_catalog);
        await maintenance.ImportAsync("short",new([p],"UTC"));File.AppendAllText(p,Damage+"\n");
        await maintenance.ImportAsync("long",new([p],"UTC"));Assert.Single(store.SourceIds());Assert.Equal(200,store.Totals(new(),_catalog).Amounts!.Damage);store.Verify();
    }
    [Fact]public void EmptyLiveFileRecognizesBomWhenFirstRecordArrives()
    {
        var(s,e,id,_,p)=Setup("");File.WriteAllText(p,Enter.Replace("Local","Élan")+"\n"+Damage.Replace("Local","Élan")+"\n",new UTF8Encoding(true));
        e.ReadAll(id);Assert.Equal("utf8-bom",s.LoadSource(id).Encoding);Assert.Equal("Élan",s.LoadSource(id).State.OwnerName);Assert.Equal(100,s.Totals(new(),_catalog).Amounts!.Damage);
    }
    [Fact]public void OneTimesReplayFollowsForwardTimesAndMidnight()
    {
        var timeline=new ReplayTimeline();Assert.Equal(TimeSpan.Zero,timeline.At(new(23,59,59)));
        Assert.Equal(TimeSpan.FromSeconds(2),timeline.At(new(0,0,1)));Assert.Equal(TimeSpan.FromSeconds(2),timeline.At(new(0,0,0)));
        Assert.Equal(TimeSpan.FromSeconds(3),timeline.At(new(0,0,1)));
    }
    [Fact]public void PauseWithNoNewBytesStillBreaksCombatContinuityAfterRebuild()
    {
        var(s,e,id,_,p)=Setup();e.ReadAll(id);e.SkipToCurrent(id);
        File.AppendAllText(p,"[20:01:00.000] [@Local#101] [] [] [Event {836045448945472}: ExitCombat {836045448945490}]\n");e.ReadAll(id);
        Assert.Equal(0,s.Totals(new(),_catalog).CombatMilliseconds);new HistoryMaintenance(s,_catalog).Rebuild();Assert.Equal(0,s.Totals(new(),_catalog).CombatMilliseconds);
    }
}
