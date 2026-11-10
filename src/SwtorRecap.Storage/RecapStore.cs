using Microsoft.Data.Sqlite;
using SwtorRecap.Core;
using System.Globalization;

namespace SwtorRecap.Storage;

public sealed record SourceCheckpoint(string Id, string ContainerId, string Path, string? PhysicalIdentity, string Encoding,
    string? DateAnchor, string SourceZone, long Offset, long Revision, long Length, int PrefixLength, string PrefixHash,
    long BoundaryStart, string BoundaryHash, string? Digest, string Integrity, string Projection, bool Sealed, string ContextId, long ContextRevision, ReducerState State);

/// <summary>Connections belong to one call/worker. Every accepted byte and all its effects commit together.</summary>
public sealed partial class RecapStore
{
    public string Root { get; }
    public string DatabasePath => Path.Combine(Root, "recap.db");
    public string SqliteVersion { get; }
    public string SqliteSourceId { get; }
    public Action<string>? Failpoint { get; set; }
    public RecapStore(string root, string? reportingZone = null)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) throw new ArgumentException("An explicit absolute data directory is required.");
        Root = Path.GetFullPath(root); Directory.CreateDirectory(Root);
        SQLitePCL.Batteries_V2.Init();
        using var connection = Open();
        SqliteVersion = Scalar<string>(connection, "SELECT sqlite_version()");
        SqliteSourceId = Scalar<string>(connection, "SELECT sqlite_source_id()");
        if (Version.Parse(SqliteVersion) < new Version(3, 51, 3)) throw new InvalidOperationException("The bundled SQLite runtime is too old for safe WAL operation.");
        var exists = Scalar<long>(connection, "SELECT count(*) FROM sqlite_master WHERE name='app_state'") != 0;
        if (exists && Scalar<long>(connection, "SELECT schema_version FROM app_state WHERE id=1") != Versions.Schema)
            throw new InvalidOperationException("History schema is incompatible. Open it with the matching application; this file has not been modified.");
        if (Scalar<string>(connection, "PRAGMA journal_mode=WAL").ToLowerInvariant() != "wal") throw new InvalidOperationException("WAL could not be enabled.");
        Execute(connection, "PRAGMA synchronous=FULL");
        if (!exists)
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            Execute(connection, Schema.Sql, transaction);
            var zone = reportingZone ?? TimeZoneInfo.Local.Id;
            _ = TimeZoneInfo.FindSystemTimeZoneById(zone);
            Execute(connection, "INSERT INTO app_state VALUES(1,$schema,$epoch,0,0,$zone,$settings)", transaction,
                ("$schema", Versions.Schema), ("$epoch", Guid.NewGuid().ToString("N")), ("$zone", zone), ("$settings", Json.Write(new TrackerSettings(null, false, false, false, zone))));
            Execute(connection, "INSERT INTO schema_migrations VALUES(1,$hash,$now)", transaction, ("$hash", Keys.Stable(Schema.Sql)), ("$now", DateTimeOffset.UtcNow.ToString("O")));
            transaction.Commit();
        }
    }
    internal SqliteConnection Open(bool readOnly = false)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 2 }.ToString());
        c.Open(); Execute(c, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=2000;");
        if (!readOnly) Execute(c, "PRAGMA synchronous=FULL");
        return c;
    }
    internal static SqliteCommand Command(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string Key, object? Value)[] args)
    {
        var command = c.CreateCommand(); command.CommandText = sql; command.Transaction = tx; command.CommandTimeout = 2;
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    internal static int Execute(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string Key, object? Value)[] args)
    { using var command = Command(c, sql, tx, args); return command.ExecuteNonQuery(); }
    internal static T Scalar<T>(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string Key, object? Value)[] args)
    { using var command = Command(c, sql, tx, args); return (T)Convert.ChangeType(command.ExecuteScalar() ?? throw new InvalidOperationException("Missing database value."), typeof(T), CultureInfo.InvariantCulture); }
    private static string? NullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    internal static Revision GetRevision(SqliteConnection c, SqliteTransaction? tx = null)
    {
        using var cmd = Command(c, "SELECT history_epoch,data_revision,settings_revision FROM app_state WHERE id=1", tx);
        using var r = cmd.ExecuteReader(); r.Read(); return new(r.GetString(0), r.GetInt64(1), r.GetInt64(2));
    }
    public Revision Revision { get { using var c = Open(true); return GetRevision(c); } }
    public TrackerSettings Settings { get { using var c = Open(true); return Json.Read<TrackerSettings>(Scalar<string>(c, "SELECT settings_json FROM app_state WHERE id=1")); } }
    public void UpdateSettings(long expected, TrackerSettings settings)
    {
        _ = TimeZoneInfo.FindSystemTimeZoneById(settings.ReportingZone);
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        if (Execute(c, "UPDATE app_state SET settings_json=$json,reporting_zone=$zone,settings_revision=settings_revision+1,data_revision=data_revision+1 WHERE id=1 AND settings_revision=$expected", tx,
            ("$json", Json.Write(settings)), ("$zone", settings.ReportingZone), ("$expected", expected)) != 1) throw new InvalidOperationException("Settings changed; refresh before saving.");
        tx.Commit();
    }
    public string CreateContainer(string kind, string? processKey = null, DateTimeOffset? start = null)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        if (processKey is not null)
        {
            using var find = Command(c, "SELECT id FROM containers WHERE process_key=$key AND state='active'", tx, ("$key", processKey));
            if (find.ExecuteScalar() is string existing) return existing;
        }
        var id = Guid.NewGuid().ToString("N");
        Execute(c, "INSERT INTO containers VALUES($id,$kind,$state,$start,NULL,NULL,$process,$start,$quality)", tx,
            ("$id", id), ("$kind", kind), ("$state", kind == "session" ? "active" : "imported"), ("$start", start?.ToString("O")), ("$process", processKey), ("$quality", kind == "session" ? "observed_from_attach" : "log_only"));
        Bump(c, tx); tx.Commit(); return id;
    }
    public void FinalizeSession(string id, DateTimeOffset? end, string reason)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        Execute(c, "UPDATE containers SET state='completed',ended_utc=COALESCE($end,last_observed_utc),closure_reason=$reason WHERE id=$id AND state='active'", tx,
            ("$id", id), ("$end", end?.ToString("O")), ("$reason", reason)); Bump(c, tx); tx.Commit();
    }
    public void RecoverSessions(string? liveProcessKey)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        var count = Execute(c, "UPDATE containers SET state='completed',ended_utc=last_observed_utc,closure_reason='tracker_interrupted' WHERE kind='session' AND state='active' AND ($key IS NULL OR process_key<>$key)", tx, ("$key", liveProcessKey));
        if (count > 0) Bump(c, tx); tx.Commit();
    }
    public void RecordAwake(string containerId, DateTimeOffset start, DateTimeOffset end, long awakeMilliseconds, string zone)
    {
        if (awakeMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(awakeMilliseconds));
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        var wall = (long)(end - start).TotalMilliseconds;
        if (wall >= 0 && Math.Abs(wall - awakeMilliseconds) <= 1500)
        {
            var parts = CalendarAllocation.Split(start, end, zone).ToArray();
            long allocated = 0;
            for (var i = 0; i < parts.Length; i++)
            {
                var p = parts[i]; var ms = i == parts.Length - 1 ? awakeMilliseconds - allocated : Math.Min(p.Milliseconds, awakeMilliseconds - allocated);
                PutInterval(c, tx, new(Keys.Stable(containerId, start, p.Date), containerId, null, "", "game", p.Start, p.End, ms, p.Date, "observed")); allocated += ms;
            }
        }
        else PutInterval(c, tx, new(Keys.Stable(containerId, start, "uncertain"), containerId, null, "", "game", start, end, awakeMilliseconds, null, "sleep_or_clock_gap"));
        Execute(c, "UPDATE containers SET last_observed_utc=$now WHERE id=$id", tx, ("$now", end.ToString("O")), ("$id", containerId));
        Bump(c, tx); tx.Commit();
    }
    public string CreateSource(string container, string path, string? physicalIdentity, string encoding, DateOnly? date,
        string zone, long length, int prefixLength, string prefixHash, bool sealedSource, string? digest = null, string? ownerId = null)
    {
        var id = Guid.NewGuid().ToString("N"); var projection = Guid.NewGuid().ToString("N");
        var state = new ReducerState { Clock = new() { Date = date, SourceZone = zone } };
        if (ownerId is not null) { state.Owner = "p:" + new GameId(ownerId).Value; state.OwnerName = "Selected character"; }
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        Execute(c, """
INSERT INTO source_generations(id,container_id,physical_identity,original_path,encoding,date_anchor,source_zone,last_length,prefix_length,prefix_hash,digest,parser_version,integrity,active_projection,sealed,context_id)
VALUES($id,$container,$physical,$path,$encoding,$date,$zone,$length,$prefixLength,$prefix,$digest,$parser,'verified',$projection,$sealed,$id)
""", tx, ("$id", id), ("$container", container), ("$physical", physicalIdentity), ("$path", path), ("$encoding", encoding), ("$date", date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$zone", zone), ("$length", length), ("$prefixLength", prefixLength), ("$prefix", prefixHash), ("$digest", digest), ("$parser", Versions.Parser), ("$projection", projection), ("$sealed", sealedSource ? 1 : 0));
        Execute(c, "INSERT INTO projection_sets VALUES($id,$source,$parser,$metrics,'active')", tx, ("$id", projection), ("$source", id), ("$parser", Versions.Parser), ("$metrics", Versions.Metrics));
        Execute(c, "INSERT INTO engine_checkpoint VALUES($id,0,$version,$state)", tx, ("$id", id), ("$version", Versions.Reducer), ("$state", Json.Write(state)));
        if(ownerId is not null)Execute(c,"INSERT INTO owner_selections VALUES($id,$owner)",tx,("$id",id),("$owner",new GameId(ownerId).Value));
        Execute(c, "INSERT INTO source_aliases VALUES($id,$path)", tx, ("$id", id), ("$path", path)); Bump(c, tx); tx.Commit(); return id;
    }
    public SourceCheckpoint LoadSource(string id)
    {
        using var c = Open(true);
        using var cmd = Command(c, "SELECT g.id,container_id,original_path,physical_identity,encoding,date_anchor,source_zone,next_offset,checkpoint_revision,last_length,prefix_length,prefix_hash,boundary_start,boundary_hash,digest,integrity,active_projection,sealed,context_id,e.revision,state_json FROM source_generations g JOIN engine_checkpoint e ON e.source_id=g.context_id WHERE g.id=$id", null, ("$id", id));
        using var r = cmd.ExecuteReader(); if (!r.Read()) throw new KeyNotFoundException("Source missing.");
        return new(r.GetString(0), r.GetString(1), r.GetString(2), NullableString(r,3), r.GetString(4), NullableString(r,5), r.GetString(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.GetInt32(10), r.GetString(11), r.GetInt64(12), r.GetString(13), NullableString(r,14), r.GetString(15), r.GetString(16), r.GetBoolean(17), r.GetString(18), r.GetInt64(19), Json.Read<ReducerState>(r.GetString(20)));
    }
    public string[] SourceIds(string? container = null)
    {
        using var c = Open(true); using var cmd = Command(c, "SELECT id FROM source_generations WHERE $container IS NULL OR container_id=$container ORDER BY date_anchor,original_path,id", null, ("$container", container));
        using var r = cmd.ExecuteReader(); var ids = new List<string>(); while (r.Read()) ids.Add(r.GetString(0)); return ids.ToArray();
    }
    public void AddAlias(string id, string path)
    { using var c = Open(); Execute(c, "INSERT OR IGNORE INTO source_aliases VALUES($id,$path)", null, ("$id", id), ("$path", path)); }
    public void Commit(SourceCheckpoint expected, BatchProjection batch, long length, long boundaryStart, string boundaryHash)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        Failpoint?.Invoke("begin");
        if (Execute(c, "UPDATE source_generations SET next_offset=$end,checkpoint_revision=checkpoint_revision+1,last_length=$length,boundary_start=$boundary,boundary_hash=$hash,encoding=$encoding,accepted=accepted+$accepted,rejected=rejected+$rejected,unknown_count=unknown_count+$unknown WHERE id=$id AND next_offset=$offset AND checkpoint_revision=$revision AND parser_version=$parser", tx,
            ("$end", batch.End), ("$length", length), ("$boundary", boundaryStart), ("$hash", boundaryHash), ("$encoding",expected.Encoding), ("$accepted", batch.Accepted), ("$rejected", batch.Rejected), ("$unknown", batch.Unknown), ("$id", expected.Id), ("$offset", expected.Offset), ("$revision", expected.Revision), ("$parser", Versions.Parser)) != 1)
            throw new InvalidOperationException("Stale source checkpoint; reload before retry.");
        if (Execute(c, "UPDATE engine_checkpoint SET revision=revision+1,state_json=$state WHERE source_id=$id AND revision=$revision AND state_version=$version", tx,
            ("$state", Json.Write(batch.State)), ("$id", expected.ContextId), ("$revision", expected.ContextRevision), ("$version", Versions.Reducer)) != 1) throw new InvalidOperationException("Stale reducer context.");
        Failpoint?.Invoke("checkpoint");
        foreach (var s in batch.Segments)
        {
            if (s.CharacterId is not null)
            {
                Execute(c, "INSERT INTO characters VALUES($id,$owner,$server,$name,$quality) ON CONFLICT(id) DO UPDATE SET name=excluded.name", tx,
                    ("$id", s.CharacterId), ("$owner", s.Owner), ("$server", s.Server), ("$name", s.Name ?? "Unknown character"), ("$quality", s.Attribution));
                Execute(c, "INSERT OR IGNORE INTO character_aliases VALUES($id,$name,$source)", tx, ("$id", s.CharacterId), ("$name", s.Name ?? "Unknown character"), ("$source", s.SourceId));
            }
            Execute(c, "INSERT INTO source_segments VALUES($id,$source,$start,$end,$owner,$character,$name,$attribution,$server) ON CONFLICT(id) DO UPDATE SET end_byte=excluded.end_byte,owner=excluded.owner,character_id=excluded.character_id,name=excluded.name,attribution=excluded.attribution,server=excluded.server", tx,
                ("$id", s.Id), ("$source", s.SourceId), ("$start", s.Start), ("$end", s.End), ("$owner", s.Owner), ("$character", s.CharacterId), ("$name", s.Name), ("$attribution", s.Attribution), ("$server", s.Server));
        }
        foreach (var b in batch.Buckets.Values)
        {
            var a = b.Amounts;
            Execute(c, """
INSERT INTO metric_buckets VALUES($id,$projection,$segment,$actor,$context,$local,$utc,$date,$quality,$epoch,$damage,$ed,$du,$heal,$eh,$hu,$events)
ON CONFLICT(id) DO UPDATE SET damage=damage+excluded.damage,effective_damage=effective_damage+excluded.effective_damage,damage_unknown=damage_unknown+excluded.damage_unknown,
 healing=healing+excluded.healing,effective_healing=effective_healing+excluded.effective_healing,healing_unknown=healing_unknown+excluded.healing_unknown,events=events+excluded.events
""", tx, ("$id", b.Key), ("$projection", expected.Projection), ("$segment", b.SegmentId), ("$actor", b.ActorKey), ("$context", b.Context), ("$local", b.LocalMinute), ("$utc", b.UtcMinute), ("$date", b.Date), ("$quality", b.Quality), ("$epoch", b.ClockEpoch),
                ("$damage", a.Damage), ("$ed", a.EffectiveDamage), ("$du", a.DamageUnknown), ("$heal", a.Healing), ("$eh", a.EffectiveHealing), ("$hu", a.HealingUnknown), ("$events", a.Events));
        }
        Failpoint?.Invoke("metrics");
        foreach (var f in batch.Facts)
        {
            var segment = batch.Segments.LastOrDefault(s => s.SourceId == f.SourceId && s.Start <= f.Start && (s.End is null || s.End > f.Start))?.Id ?? batch.State.Segment?.Id;
            Execute(c, "INSERT OR IGNORE INTO encounter_evidence VALUES($id,$source,$start,$end,$kind,$entity,$segment,$date,$utc,$quality,$payload)", tx,
                ("$id", f.Id), ("$source", f.SourceId), ("$start", f.Start), ("$end", f.End), ("$kind", f.Kind), ("$entity", f.Entity), ("$segment", segment), ("$date", f.Time.ReportingDate), ("$utc", f.Time.Utc?.ToString("O")), ("$quality", f.Time.Quality), ("$payload", f.Payload));
        }
        foreach (var a in batch.Attempts.Values)
        {
            Execute(c, "INSERT INTO encounter_attempts VALUES($id,$source,$segment,$run,$activity,$encounter,$result,$basis,$qualified,$date,$payload) ON CONFLICT(id) DO UPDATE SET result=excluded.result,basis=excluded.basis,qualified=excluded.qualified,reporting_date=excluded.reporting_date,payload=excluded.payload", tx,
                ("$id", a.Id), ("$source", a.SourceId), ("$segment", a.SegmentId), ("$run", a.RunId), ("$activity", a.ActivityId), ("$encounter", a.EncounterId), ("$result", a.Result), ("$basis", a.Basis), ("$qualified", a.Qualified ? 1 : 0), ("$date", a.Date), ("$payload", Json.Write(a)));
            foreach (var evidence in a.Evidence) Execute(c, "INSERT OR IGNORE INTO result_evidence_links VALUES($attempt,$evidence)", tx, ("$attempt", a.Id), ("$evidence", evidence));
        }
        foreach (var run in batch.Runs.Values)
            Execute(c, "INSERT INTO activity_runs VALUES($id,$segment,$activity,$terminal,$full,$date,$payload) ON CONFLICT(id) DO UPDATE SET terminal=excluded.terminal,full=excluded.full,reporting_date=excluded.reporting_date,payload=excluded.payload", tx,
                ("$id", run.Id), ("$segment", run.SegmentId), ("$activity", run.ActivityId), ("$terminal", run.Terminal ? 1 : 0), ("$full", run.Full ? 1 : 0), ("$date", run.Date), ("$payload", Json.Write(run)));
        foreach (var gap in batch.Gaps) PutGap(c, tx, gap);
        foreach (var exclusion in batch.Exclusions)
            Execute(c, "INSERT OR IGNORE INTO source_exclusions VALUES($source,$start,$end)", tx,
                ("$source", exclusion.SourceId), ("$start", exclusion.Start), ("$end", exclusion.End));
        foreach (var interval in batch.Intervals) PutInterval(c, tx, interval);
        Failpoint?.Invoke("evidence"); Bump(c, tx); Failpoint?.Invoke("before_commit"); tx.Commit(); Failpoint?.Invoke("after_commit");
    }
    internal static void Bump(SqliteConnection c, SqliteTransaction tx) => Execute(c, "UPDATE app_state SET data_revision=data_revision+1 WHERE id=1", tx);
    private static void PutInterval(SqliteConnection c, SqliteTransaction tx, RecordedInterval i) => Execute(c, "INSERT OR IGNORE INTO session_intervals VALUES($id,$container,$segment,$actor,$kind,$start,$end,$ms,$date,$quality)", tx,
        ("$id", i.Id), ("$container", i.ContainerId), ("$segment", i.SegmentId), ("$actor", i.ActorKey), ("$kind", i.Kind), ("$start", i.Start?.ToString("O")), ("$end", i.End?.ToString("O")), ("$ms", i.Milliseconds), ("$date", i.Date), ("$quality", i.Quality));
    private static void PutGap(SqliteConnection c, SqliteTransaction tx, Gap g) => Execute(c, "INSERT OR IGNORE INTO coverage_gaps VALUES($id,$source,$start,$end,$dimension,$reason,$recoverable)", tx,
        ("$id", Keys.Stable(g.SourceId, g.Start, g.End, g.Dimension, g.Reason)), ("$source", g.SourceId), ("$start", g.Start), ("$end", g.End), ("$dimension", g.Dimension), ("$reason", g.Reason), ("$recoverable", g.Recoverable ? 1 : 0));
    public void MarkIntegrity(string id, string reason, long length)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        var offset = Scalar<long>(c, "SELECT next_offset FROM source_generations WHERE id=$id", tx, ("$id", id));
        Execute(c, "UPDATE source_generations SET integrity=$reason WHERE id=$id", tx, ("$reason", reason), ("$id", id));
        PutGap(c, tx, new(id, offset, Math.Max(offset, length), "source", reason, false)); Bump(c, tx); tx.Commit();
    }
    public SourceExclusion? NextExclusion(string sourceId, long offset,bool includeEmpty=true)
    {
        using var c=Open(true);using var cmd=Command(c,"SELECT start_byte,end_byte FROM source_exclusions WHERE source_id=$id AND (end_byte>$offset OR $empty=1 AND start_byte=end_byte AND end_byte=$offset) ORDER BY start_byte,end_byte LIMIT 1",null,("$id",sourceId),("$offset",offset),("$empty",includeEmpty?1:0));
        using var r=cmd.ExecuteReader();return r.Read()?new(sourceId,r.GetInt64(0),r.GetInt64(1)):null;
    }
}
