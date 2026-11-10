using Microsoft.Data.Sqlite;
using SwtorRecap.Core;

namespace SwtorRecap.Storage;

public sealed partial class RecapStore
{
    // Topological order is also the complete dependency closure used for replacement.
    private static readonly string[] DerivedTables = ["containers","source_generations","source_dependencies","source_aliases","owner_selections","source_exclusions","projection_sets","characters","character_aliases","source_segments","engine_checkpoint","metric_buckets","encounter_evidence","encounter_attempts","activity_runs","result_evidence_links","coverage_gaps","session_intervals"];
    public void Verify()
    {
        using var c=Open(true);
        if(Scalar<string>(c,"PRAGMA integrity_check")!="ok") throw new InvalidDataException("History integrity check failed.");
        using var cmd=Command(c,"PRAGMA foreign_key_check");using var r=cmd.ExecuteReader();if(r.Read()) throw new InvalidDataException("History references are inconsistent.");
    }
    public void Backup(string destination)
    {
        destination=Path.GetFullPath(destination);
        if(destination==DatabasePath || File.Exists(destination)) throw new IOException("Choose a new backup filename.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using(var source=Open())
        using(var target=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=destination,Pooling=false}.ToString()))
        { target.Open();source.BackupDatabase(target); }
        VerifyFile(destination);
    }
    public static void VerifyFile(string path)
    {
        if(!File.Exists(path)) throw new FileNotFoundException("Backup is missing.",path);
        using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.GetFullPath(path),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());c.Open();
        if(Scalar<string>(c,"PRAGMA integrity_check")!="ok" || Scalar<long>(c,"SELECT schema_version FROM app_state")!=Versions.Schema) throw new InvalidDataException("Backup is corrupt or uses an incompatible schema.");
        using var cmd=Command(c,"PRAGMA foreign_key_check");using var r=cmd.ExecuteReader();if(r.Read()) throw new InvalidDataException("Backup references are inconsistent.");
    }
    public void DailyBackup()
    {
        var dir=Path.Combine(Root,"backups");Directory.CreateDirectory(dir);
        var path=Path.Combine(dir,DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")+".db");
        if(!File.Exists(path)) Backup(path);
        foreach(var old in Directory.EnumerateFiles(dir,"????-??-??.db").OrderByDescending(Path.GetFileName,StringComparer.Ordinal).Skip(7)) File.Delete(old);
    }
    public void ActivateStaging(string stagingPath, Revision expected, bool replace, bool restore=false)
    {
        VerifyFile(stagingPath);
        if(replace)
        {
            var dir=Path.Combine(Root,"backups");Directory.CreateDirectory(dir);
            Backup(Path.Combine(dir,(restore?"pre-restore-":"pre-rebuild-")+Guid.NewGuid().ToString("N")+".db"));
        }
        using var staged=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.GetFullPath(stagingPath),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        staged.Open();using var stagedRead=staged.BeginTransaction();
        using var c=Open();
        // Separate read-only staging connection. Only production is written; there is no
        // transaction spanning two WAL databases, and staging data is streamed one row at a time.
        using var tx=c.BeginTransaction(deferred:false);
        if(GetRevision(c,tx)!=expected) throw new InvalidOperationException("History/settings changed while staging. Rebuild again from the new revision.");
        if(replace && Scalar<long>(c,"SELECT count(*) FROM containers WHERE state='active'",tx)>0) throw new InvalidOperationException("Replacement waits until the game session is finalized.");
        Execute(c,"PRAGMA defer_foreign_keys=ON",tx);
        Failpoint?.Invoke("activation_begin");
        if(replace) foreach(var table in DerivedTables.Reverse()) Execute(c,"DELETE FROM "+table,tx);
        foreach(var table in DerivedTables)
        {
            using var select=Command(staged,"SELECT * FROM "+table,stagedRead);using var rows=select.ExecuteReader();
            using var insert=Command(c,"INSERT "+(table=="characters"&&!replace?"OR IGNORE ":"")+"INTO "+table+" VALUES("+string.Join(',',Enumerable.Range(0,rows.FieldCount).Select(i=>"$p"+i))+")",tx);
            for(var i=0;i<rows.FieldCount;i++)insert.Parameters.Add(new SqliteParameter("$p"+i,DBNull.Value));
            while(rows.Read()){for(var i=0;i<rows.FieldCount;i++)insert.Parameters[i].Value=rows.GetValue(i);insert.ExecuteNonQuery();}
            Failpoint?.Invoke("activation_"+table);
        }
        if(replace)
        {
            Execute(c,"UPDATE app_state SET reporting_zone=$zone,settings_json=$settings,settings_revision=settings_revision+1",tx,
                ("$zone",Scalar<string>(staged,"SELECT reporting_zone FROM app_state",stagedRead)),("$settings",Scalar<string>(staged,"SELECT settings_json FROM app_state",stagedRead)));
        }
        if(restore)
        {
            Execute(c,"UPDATE app_state SET history_epoch=$epoch",tx,("$epoch",Guid.NewGuid().ToString("N")));
            Execute(c,"DELETE FROM jobs; DELETE FROM mutation_receipts;",tx);
            Execute(c,"UPDATE containers SET state='completed',ended_utc=last_observed_utc,closure_reason='restored_interrupted' WHERE state='active'",tx);
        }
        using(var check=Command(c,"PRAGMA foreign_key_check",tx)) using(var r=check.ExecuteReader()) if(r.Read()) throw new InvalidDataException("Staged closure contains invalid references.");
        Bump(c,tx);Failpoint?.Invoke("activation_before_commit");tx.Commit();Failpoint?.Invoke("activation_after_commit");
    }
    public void ResetDerivedSource(string sourceId, string? ownerId=null, string? sourceZone=null)
    {
        var old=LoadSource(sourceId);using var c=Open();using var tx=c.BeginTransaction(deferred:false);
        if(ownerId is not null)Execute(c,"INSERT INTO owner_selections VALUES($id,$owner) ON CONFLICT(source_id) DO UPDATE SET player_id=excluded.player_id",tx,("$id",sourceId),("$owner",new GameId(ownerId).Value));
        using(var choice=Command(c,"SELECT player_id FROM owner_selections WHERE source_id=$id",tx,("$id",sourceId)))ownerId=choice.ExecuteScalar() as string;
        Execute(c,"DELETE FROM encounter_evidence WHERE source_id=$id; DELETE FROM source_segments WHERE source_id=$id; DELETE FROM coverage_gaps WHERE source_id=$id; DELETE FROM projection_sets WHERE source_id=$id",tx,("$id",sourceId));
        var projection=Guid.NewGuid().ToString("N");
        Execute(c,"INSERT INTO projection_sets VALUES($projection,$id,$parser,$metric,'active')",tx,("$projection",projection),("$id",sourceId),("$parser",Versions.Parser),("$metric",Versions.Metrics));
        var state=new ReducerState{Clock=new(){Date=old.DateAnchor is null?null:DateOnly.Parse(old.DateAnchor),SourceZone=sourceZone??old.SourceZone}};
        if(ownerId is not null){state.Owner="p:"+new GameId(ownerId).Value;state.OwnerName="Selected character";}
        Execute(c,"UPDATE source_generations SET next_offset=0,checkpoint_revision=0,boundary_start=0,boundary_hash='',accepted=0,rejected=0,unknown_count=0,active_projection=$projection,parser_version=$parser,source_zone=$zone,context_id=$id WHERE id=$id",tx,("$projection",projection),("$parser",Versions.Parser),("$id",sourceId),("$zone",state.Clock.SourceZone));
        Execute(c,"INSERT INTO engine_checkpoint VALUES($id,0,$version,$state) ON CONFLICT(source_id) DO UPDATE SET revision=0,state_version=excluded.state_version,state_json=excluded.state_json",tx,("$version",Versions.Reducer),("$state",Json.Write(state)),("$id",sourceId));tx.Commit();
    }
    public Dictionary<string,string> Continuations()
    {
        using var c=Open(true);using var cmd=Command(c,"SELECT next_id,prior_id FROM source_dependencies");using var r=cmd.ExecuteReader();var result=new Dictionary<string,string>();while(r.Read())result[r.GetString(0)]=r.GetString(1);return result;
    }
    public void ReplaceSourceInput(string sourceId,string path,string encoding,long length,int prefixLength,string prefixHash,string digest)
    {
        using var c=Open();using var tx=c.BeginTransaction(deferred:false);
        Execute(c,"UPDATE source_generations SET original_path=$path,encoding=$encoding,last_length=$length,prefix_length=$prefixLength,prefix_hash=$prefix,digest=$digest,sealed=1,integrity='verified' WHERE id=$id AND next_offset=0",tx,
            ("$id",sourceId),("$path",path),("$encoding",encoding),("$length",length),("$prefixLength",prefixLength),("$prefix",prefixHash),("$digest",digest));
        tx.Commit();
    }
    public void ReprojectGameIntervals(string zone)
    {
        _=TimeZoneInfo.FindSystemTimeZoneById(zone);using var c=Open();using var tx=c.BeginTransaction(deferred:false);
        var intervals=new List<(string Id,string Container,DateTimeOffset Start,DateTimeOffset End,long Milliseconds)>();
        using(var cmd=Command(c,"SELECT id,container_id,start_utc,end_utc,milliseconds FROM session_intervals WHERE kind='game' AND quality='observed'",tx))
        using(var r=cmd.ExecuteReader())while(r.Read())intervals.Add((r.GetString(0),r.GetString(1),DateTimeOffset.Parse(r.GetString(2)),DateTimeOffset.Parse(r.GetString(3)),r.GetInt64(4)));
        foreach(var interval in intervals)
        {
            Execute(c,"DELETE FROM session_intervals WHERE id=$id",tx,("$id",interval.Id));
            var parts=CalendarAllocation.Split(interval.Start,interval.End,zone).ToArray();long allocated=0;
            for(var i=0;i<parts.Length;i++)
            {
                var p=parts[i];var ms=i==parts.Length-1?interval.Milliseconds-allocated:Math.Min(p.Milliseconds,interval.Milliseconds-allocated);allocated+=ms;
                PutInterval(c,tx,new(Keys.Stable(interval.Container,p.Start,p.End,"game"),interval.Container,null,"","game",p.Start,p.End,ms,p.Date,"observed"));
            }
        }
        tx.Commit();
    }
    public void LinkContinuation(SourceCheckpoint prior,SourceCheckpoint next)
    {
        if(prior.ContainerId!=next.ContainerId||next.Offset!=0||prior.Id==next.Id)throw new InvalidOperationException("Invalid source continuation.");
        using var c=Open();using var tx=c.BeginTransaction(deferred:false);
        if(Scalar<long>(c,"SELECT checkpoint_revision FROM source_generations WHERE id=$id",tx,("$id",prior.Id))!=prior.Revision)throw new InvalidOperationException("Prior source changed during handoff.");
        if(Execute(c,"UPDATE source_generations SET context_id=$context WHERE id=$id AND next_offset=0 AND checkpoint_revision=$revision",tx,("$context",prior.ContextId),("$id",next.Id),("$revision",next.Revision))!=1)throw new InvalidOperationException("Continuation source changed.");
        Execute(c,"INSERT OR REPLACE INTO source_dependencies VALUES($prior,$next,'same_owner_server_area_chronology')",tx,("$prior",prior.Id),("$next",next.Id));
        Execute(c,"DELETE FROM engine_checkpoint WHERE source_id=$id",tx,("$id",next.Id));Bump(c,tx);tx.Commit();
    }
}
