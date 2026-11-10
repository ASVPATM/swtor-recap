using Microsoft.Data.Sqlite;
using SwtorRecap.Core;
using System.Globalization;

namespace SwtorRecap.Storage;

public sealed partial class RecapStore
{
    private static (string Key, object? Value)[] FilterArgs(QueryFilter filter)
    {
        if (filter.Year is < 1900 or > 2200 || filter.Month is < 1 or > 12 || filter.Month is not null && filter.Year is null || filter.Offset < 0 || filter.Limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(filter));
        var from = filter.Year is { } year ? new DateOnly(year, filter.Month ?? 1, 1) : (DateOnly?)null;
        var until = filter.Month is not null ? from?.AddMonths(1) : from?.AddYears(1);
        return [("$from", from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$until", until?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$character", filter.CharacterId), ("$container", filter.ContainerId)];
    }
    private const string Period = "($from IS NULL OR reporting_date >= $from AND reporting_date < $until)";
    private const string PersonalFilter = Period + " AND ($character IS NULL OR character_id=$character) AND ($container IS NULL OR container_id=$container)";
    private static Amounts ReadAmounts(SqliteDataReader r, int start = 0) => new()
    {
        Damage = r.GetInt64(start), EffectiveDamage = r.GetInt64(start+1), DamageUnknown = r.GetInt64(start+2),
        Healing = r.GetInt64(start+3), EffectiveHealing = r.GetInt64(start+4), HealingUnknown = r.GetInt64(start+5), Events = r.GetInt64(start+6)
    };
    private const string SumAmounts = "COALESCE(sum(damage),0),COALESCE(sum(effective_damage),0),COALESCE(sum(damage_unknown),0),COALESCE(sum(healing),0),COALESCE(sum(effective_healing),0),COALESCE(sum(healing_unknown),0),COALESCE(sum(events),0)";
    private Totals TotalsAt(SqliteConnection c, SqliteTransaction tx, QueryFilter filter, Catalog catalog)
    {
        var args = FilterArgs(filter);
        Amounts? amounts;
        using (var cmd = Command(c, "SELECT " + SumAmounts + " FROM personal_buckets WHERE " + PersonalFilter, tx, args))
        using (var r = cmd.ExecuteReader()) { r.Read(); amounts = ReadAmounts(r); }
        if (Scalar<long>(c, "SELECT count(*) FROM source_segments s JOIN source_generations g ON g.id=s.source_id WHERE g.visible=1 AND s.owner IS NOT NULL AND g.accepted>0 AND ($character IS NULL OR s.character_id=$character) AND ($container IS NULL OR g.container_id=$container)", tx, args) == 0) amounts = null;
        var intervalSql = "SELECT COALESCE(sum(i.milliseconds),0) FROM session_intervals i LEFT JOIN source_segments s ON s.id=i.segment_id WHERE i.kind=$kind AND ($from IS NULL OR i.reporting_date >= $from AND i.reporting_date < $until) AND ($container IS NULL OR i.container_id=$container) AND ($character IS NULL OR s.character_id=$character) AND (i.kind='game' OR i.actor_key=s.owner)";
        var game = Scalar<long>(c, intervalSql, tx, args.Append(("$kind", (object?)"game")).ToArray());
        var combat = Scalar<long>(c, intervalSql, tx, args.Append(("$kind", (object?)"combat")).ToArray());
        const string outcomeFilter = "($from IS NULL OR a.reporting_date >= $from AND a.reporting_date < $until) AND ($character IS NULL OR s.character_id=$character) AND ($container IS NULL OR g.container_id=$container) AND g.visible=1";
        var bosses = Scalar<long>(c, "SELECT count(*) FROM encounter_attempts a JOIN source_segments s ON s.id=a.segment_id JOIN source_generations g ON g.id=s.source_id WHERE a.result='won' AND a.qualified=1 AND " + outcomeFilter, tx, args);
        var unconfirmed = Scalar<long>(c, "SELECT count(*) FROM encounter_attempts a JOIN source_segments s ON s.id=a.segment_id JOIN source_generations g ON g.id=s.source_id WHERE a.result='unconfirmed' AND a.qualified=1 AND " + outcomeFilter, tx, args);
        var clears = new Dictionary<string,long>(); long full = 0;
        using (var cmd = Command(c, "SELECT a.activity_id,count(*),sum(a.full) FROM activity_runs a JOIN source_segments s ON s.id=a.segment_id JOIN source_generations g ON g.id=s.source_id WHERE a.terminal=1 AND " + outcomeFilter + " GROUP BY a.activity_id", tx, args))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var type = catalog.Document.Activities.SingleOrDefault(a => a.Id == r.GetString(0))?.Type ?? "other";
                clears[type] = checked(clears.GetValueOrDefault(type) + r.GetInt64(1)); full = checked(full + r.GetInt64(2));
            }
        var deaths = Scalar<long>(c, "SELECT count(*) FROM encounter_evidence a JOIN source_segments s ON s.id=a.segment_id JOIN source_generations g ON g.id=s.source_id WHERE a.kind='player_death' AND a.entity=s.owner AND " + outcomeFilter, tx, args);
        var kills = Scalar<long>(c, "SELECT count(*) FROM encounter_evidence a JOIN source_segments s ON s.id=a.segment_id JOIN source_generations g ON g.id=s.source_id WHERE a.kind='npc_kill' AND json_extract(a.payload,'$.owner')=s.owner AND " + outcomeFilter, tx, args);
        var killsNeedReprocess = Scalar<long>(c, "SELECT count(*) FROM projection_sets p JOIN source_generations g ON g.active_projection=p.id JOIN source_segments s ON s.source_id=g.id WHERE g.visible=1 AND p.metric_version<>$metrics AND ($container IS NULL OR g.container_id=$container) AND ($character IS NULL OR s.character_id=$character)", tx, args.Append(("$metrics", (object?)Versions.Metrics)).ToArray()) > 0;
        long sessions = 0, imports = 0;
        using (var cmd = Command(c, "SELECT id,kind,started_utc FROM containers WHERE ($container IS NULL OR id=$container) AND ($character IS NULL OR EXISTS(SELECT 1 FROM source_segments s JOIN source_generations g ON g.id=s.source_id WHERE g.container_id=containers.id AND s.character_id=$character))", tx, args))
        using (var r = cmd.ExecuteReader())
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(Scalar<string>(c, "SELECT reporting_zone FROM app_state", tx));
            while (r.Read())
            {
                if (r.GetString(1) == "import")
                {
                    var id = r.GetString(0);
                    if (filter.Year is null || Scalar<long>(c, "SELECT count(*) FROM personal_buckets WHERE container_id=$id AND " + PersonalFilter, tx, args.Append(("$id", (object?)id)).ToArray()) > 0) imports++;
                }
                else if (!r.IsDBNull(2))
                {
                    var time = TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture), zone);
                    if ((filter.Year is null || time.Year == filter.Year) && (filter.Month is null || time.Month == filter.Month)) sessions++;
                }
            }
        }
        return new(amounts, game, combat, bosses, clears.GetValueOrDefault("flashpoint"), clears.GetValueOrDefault("operation"), clears.GetValueOrDefault("lair"), full, deaths, sessions, imports, kills, killsNeedReprocess, unconfirmed);
    }
    public Totals Totals(QueryFilter filter, Catalog catalog)
    { using var c = Open(true); using var tx = c.BeginTransaction(); return TotalsAt(c, tx, filter, catalog); }
    private HistoryItem ItemAt(SqliteConnection c, SqliteTransaction tx, string id, Catalog catalog, QueryFilter? filter = null)
    {
        string kind, state; string? start, end;
        using (var cmd = Command(c, "SELECT kind,state,started_utc,ended_utc FROM containers WHERE id=$id", tx, ("$id", id)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) throw new KeyNotFoundException("Recording not found.");
            kind = r.GetString(0); state = r.GetString(1); start = NullableString(r,2); end = NullableString(r,3);
        }
        var names = new List<string>();
        using (var cmd = Command(c, "SELECT DISTINCT s.name FROM source_segments s JOIN source_generations g ON g.id=s.source_id WHERE g.container_id=$id AND s.owner IS NOT NULL AND s.name IS NOT NULL", tx, ("$id", id)))
        using (var r = cmd.ExecuteReader()) while (r.Read()) names.Add(r.GetString(0));
        if (kind == "import")
        {
            using var cmd = Command(c, "SELECT min(reporting_date),max(reporting_date) FROM personal_buckets WHERE container_id=$id", tx, ("$id", id));
            using var r = cmd.ExecuteReader(); r.Read(); start = NullableString(r,0); end = NullableString(r,1);
        }
        var gaps = Scalar<long>(c, "SELECT count(*) FROM coverage_gaps gap JOIN source_generations g ON g.id=gap.source_id WHERE g.container_id=$id", tx, ("$id", id));
        return new(id, kind, state, start, end, names.ToArray(), TotalsAt(c, tx, (filter??new()) with {ContainerId=id}, catalog), checked((int)gaps));
    }
    public string? CurrentCharacter(string container)
    {
        using var c=Open(true);
        using var cmd=Command(c,"SELECT s.name FROM encounter_evidence e JOIN source_segments s ON s.id=e.segment_id JOIN source_generations g ON g.id=s.source_id WHERE g.container_id=$id AND g.visible=1 AND s.owner IS NOT NULL AND e.kind IN ('area_owner','owner_enter') ORDER BY e.utc_time DESC,g.original_path DESC,e.start_byte DESC LIMIT 1",null,("$id",container));
        return cmd.ExecuteScalar() as string;
    }
    public HistoryItem? Current(Catalog catalog)
    {
        using var c = Open(true); using var tx = c.BeginTransaction();
        using var cmd = Command(c, "SELECT id FROM containers ORDER BY (state='active') DESC,COALESCE(started_utc,(SELECT min(date_anchor) FROM source_generations WHERE container_id=containers.id)) DESC LIMIT 1", tx);
        return cmd.ExecuteScalar() is string id ? ItemAt(c, tx, id, catalog) : null;
    }
    private CharacterSummary[] CharactersAt(SqliteConnection c, SqliteTransaction tx, QueryFilter filter)
    {
        var result = new List<CharacterSummary>(); var args = FilterArgs(filter);
        using var cmd = Command(c, "SELECT DISTINCT ch.id,ch.name,ch.server FROM characters ch JOIN source_segments s ON s.character_id=ch.id JOIN source_generations g ON g.id=s.source_id WHERE g.visible=1 AND ($container IS NULL OR g.container_id=$container) ORDER BY ch.name", tx, args);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetString(0);
            if (filter.CharacterId is not null && filter.CharacterId != id) continue;
            var a = FilterArgs(filter with { CharacterId = id });
            long damage = Scalar<long>(c, "SELECT COALESCE(sum(damage),0) FROM personal_buckets WHERE " + PersonalFilter, tx, a);
            long healing = Scalar<long>(c, "SELECT COALESCE(sum(healing),0) FROM personal_buckets WHERE " + PersonalFilter, tx, a);
            long combat = Scalar<long>(c, "SELECT COALESCE(sum(i.milliseconds),0) FROM session_intervals i JOIN source_segments s ON s.id=i.segment_id WHERE i.kind='combat' AND i.actor_key=s.owner AND s.character_id=$character AND ($container IS NULL OR i.container_id=$container) AND ($from IS NULL OR reporting_date >= $from AND reporting_date < $until)", tx, a);
            result.Add(new(id, r.GetString(1), combat, damage, healing, NullableString(r,2)));
        }
        return result.OrderByDescending(ch => ch.CombatMilliseconds).ThenBy(ch => ch.Name, StringComparer.Ordinal).ToArray();
    }
    public HistoryPage History(QueryFilter filter, Catalog catalog)
    {
        var args = FilterArgs(filter); using var c = Open(true); using var tx = c.BeginTransaction();
        var ids = new List<string>();
        using (var cmd = Command(c, """
SELECT id FROM containers WHERE ($container IS NULL OR id=$container)
AND ($character IS NULL OR EXISTS(SELECT 1 FROM source_segments s JOIN source_generations g ON g.id=s.source_id WHERE g.container_id=containers.id AND s.character_id=$character))
AND ($from IS NULL OR EXISTS(SELECT 1 FROM personal_buckets WHERE container_id=containers.id AND reporting_date >= $from AND reporting_date < $until)
 OR EXISTS(SELECT 1 FROM session_intervals WHERE container_id=containers.id AND reporting_date >= $from AND reporting_date < $until))
ORDER BY (state='active') DESC,COALESCE(started_utc,(SELECT min(date_anchor) FROM source_generations WHERE container_id=containers.id)) DESC,id
LIMIT $limit OFFSET $offset
""", tx, args.Concat(new (string,object?)[]{ ("$limit",filter.Limit+1),("$offset",filter.Offset) }).ToArray()))
        using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetString(0));
        return new(GetRevision(c,tx), ids.Take(filter.Limit).Select(id => ItemAt(c,tx,id,catalog,filter)).ToArray(), ids.Count > filter.Limit, CharactersAt(c,tx,new()));
    }
    public HistoryDetail Detail(string id, Catalog catalog, string? characterId = null)
    {
        using var c = Open(true); using var tx = c.BeginTransaction(); var outcomes = new List<OutcomeSummary>();
        using (var cmd = Command(c, "SELECT a.payload,r.terminal,r.full,s.name FROM encounter_attempts a JOIN source_segments s ON s.id=a.segment_id JOIN source_generations g ON g.id=a.source_id LEFT JOIN activity_runs r ON r.id=a.run_id WHERE g.container_id=$id AND g.visible=1 AND ($character IS NULL OR s.character_id=$character) ORDER BY a.reporting_date,a.id LIMIT 1000", tx, ("$id", id), ("$character",characterId)))
        using (var r = cmd.ExecuteReader()) while (r.Read())
        {
            var a = Json.Read<AttemptState>(r.GetString(0)); var activity = catalog.Document.Activities.Single(x => x.Id == a.ActivityId);
            var definition=activity.Encounters.Single(x=>x.Id==a.EncounterId);
            outcomes.Add(new(a.Id,definition.Name,activity.Name,a.Result,a.Basis,a.Qualified,definition.Final&&!r.IsDBNull(1)&&r.GetBoolean(1),definition.Final&&!r.IsDBNull(2)&&r.GetBoolean(2),a.Date,a.Evidence.ToArray(),NullableString(r,3)));
        }
        var sources = new List<SourceSummary>();
        using (var cmd = Command(c, "SELECT id,original_path,next_offset,last_length,accepted,rejected,integrity,(SELECT name FROM source_segments WHERE source_id=g.id ORDER BY start_byte DESC LIMIT 1),date_anchor FROM source_generations g WHERE container_id=$id", tx, ("$id",id)))
        using (var r = cmd.ExecuteReader()) while(r.Read()) sources.Add(new(r.GetString(0),Path.GetFileName(r.GetString(1)),r.GetInt64(2),r.GetInt64(3),r.GetInt32(4),r.GetInt32(5),r.GetString(6),NullableString(r,7),NullableString(r,8)));
        for(var i=0;i<sources.Count;i++)
        {
            var candidates=new List<OwnerCandidate>();
            using var cmd=Command(c,"""
SELECT DISTINCT b.actor_key,COALESCE((SELECT json_extract(e.payload,'$.name') FROM encounter_evidence e
 WHERE e.source_id=s.source_id AND e.entity=b.actor_key AND e.kind='owner_candidate' LIMIT 1),b.actor_key)
FROM metric_buckets b JOIN source_segments s ON s.id=b.segment_id
WHERE s.source_id=$source AND s.owner IS NULL ORDER BY 2 LIMIT 64
""",tx,("$source",sources[i].Id));
            using var r=cmd.ExecuteReader();while(r.Read())candidates.Add(new(r.GetString(0)[2..],r.GetString(1)));
            sources[i]=sources[i] with{Candidates=candidates.ToArray()};
        }
        var gaps = new List<Gap>();
        using(var cmd = Command(c,"SELECT gap.source_id,start_byte,end_byte,dimension,reason,recoverable FROM coverage_gaps gap JOIN source_generations g ON g.id=gap.source_id WHERE g.container_id=$id LIMIT 1000",tx,("$id",id)))
        using(var r=cmd.ExecuteReader()) while(r.Read()) gaps.Add(new(r.GetString(0),r.GetInt64(1),r.GetInt64(2),r.GetString(3),r.GetString(4),r.GetBoolean(5)));
        var kills = new List<NpcKillSummary>();
        using (var cmd = Command(c, """
SELECT json_extract(a.payload,'$.name'),json_extract(a.payload,'$.templateId'),count(*)
FROM encounter_evidence a JOIN source_segments s ON s.id=a.segment_id JOIN source_generations g ON g.id=s.source_id
WHERE g.container_id=$id AND g.visible=1 AND ($character IS NULL OR s.character_id=$character) AND a.kind='npc_kill' AND json_extract(a.payload,'$.owner')=s.owner
GROUP BY json_extract(a.payload,'$.templateId'),json_extract(a.payload,'$.name') ORDER BY count(*) DESC,1 LIMIT 1000
""", tx, ("$id", id),("$character",characterId)))
        using (var r = cmd.ExecuteReader()) while (r.Read()) kills.Add(new(r.GetString(0),r.GetString(1),r.GetInt64(2)));
        return new(GetRevision(c,tx),ItemAt(c,tx,id,catalog,new(CharacterId:characterId)),outcomes.ToArray(),sources.ToArray(),gaps.ToArray(),kills.ToArray(),CharactersAt(c,tx,new(ContainerId:id)),characterId);
    }
    public RecapSnapshot Recap(QueryFilter filter, Catalog catalog, DateTimeOffset? now = null)
    {
        if (filter.Year is null) throw new ArgumentException("A recap year is required.");
        var args=FilterArgs(filter); using var c=Open(true); using var tx=c.BeginTransaction();
        var settings=Json.Read<TrackerSettings>(Scalar<string>(c,"SELECT settings_json FROM app_state",tx));
        var points=new Dictionary<string,PeriodPoint>();
        using(var cmd=Command(c,"SELECT reporting_date,sum(damage),sum(healing) FROM personal_buckets WHERE "+PersonalFilter+" GROUP BY reporting_date ORDER BY reporting_date",tx,args))
        using(var r=cmd.ExecuteReader()) while(r.Read()) points[r.GetString(0)]=new(r.GetString(0),r.GetInt64(1),r.GetInt64(2),0,0);
        using(var cmd=Command(c,"SELECT i.reporting_date,i.kind,sum(i.milliseconds) FROM session_intervals i LEFT JOIN source_segments s ON s.id=i.segment_id WHERE ($from IS NULL OR i.reporting_date >= $from AND i.reporting_date < $until) AND ($container IS NULL OR i.container_id=$container) AND ($character IS NULL OR s.character_id=$character) AND (i.kind='game' OR i.actor_key=s.owner) GROUP BY i.reporting_date,i.kind",tx,args))
        using(var r=cmd.ExecuteReader()) while(r.Read())
        {
            var date=r.GetString(0);var p=points.GetValueOrDefault(date)??new(date,0,0,0,0);
            points[date]=r.GetString(1)=="game" ? p with {GameMilliseconds=r.GetInt64(2)} : p with {CombatMilliseconds=r.GetInt64(2)};
        }
        var activities=new List<ActivitySummary>();
        using(var cmd=Command(c,"SELECT a.activity_id,count(*),min(a.reporting_date) FROM activity_runs a JOIN source_segments s ON s.id=a.segment_id JOIN source_generations g ON g.id=s.source_id WHERE a.terminal=1 AND ($from IS NULL OR a.reporting_date >= $from AND a.reporting_date < $until) AND ($character IS NULL OR s.character_id=$character) AND ($container IS NULL OR g.container_id=$container) GROUP BY a.activity_id ORDER BY count(*) DESC",tx,args))
        using(var r=cmd.ExecuteReader()) while(r.Read())
        {
            var a=catalog.Document.Activities.Single(x=>x.Id==r.GetString(0)); activities.Add(new(a.Name,a.Type,r.GetInt64(1),NullableString(r,2)));
        }
        var coverage=new List<string>();
        if(Scalar<long>(c,"SELECT count(*) FROM coverage_gaps",tx)>0) coverage.Add("Some recordings have gaps; inspect History for affected sources.");
        if(Scalar<long>(c,"SELECT count(*) FROM personal_buckets WHERE reporting_date IS NULL",tx)>0) coverage.Add("Undated or ambiguous amounts remain in History and are excluded from calendar totals.");
        if(Scalar<long>(c,"SELECT count(*) FROM session_intervals WHERE reporting_date IS NULL",tx)>0) coverage.Add("Some observed time cannot be assigned to a calendar date; it remains in recording details.");
        if(Scalar<long>(c,"SELECT count(*) FROM containers WHERE kind='import'",tx)>0) coverage.Add("Imported captures do not establish game-open time or observed sessions.");
        coverage.Add("Only recorded history is included. Known death targets use shared counting rules; unsupported scripted endings remain unconfirmed.");
        var local=TimeZoneInfo.ConvertTime(now??DateTimeOffset.UtcNow,TimeZoneInfo.FindSystemTimeZoneById(settings.ReportingZone));
        var open=filter.Year==local.Year&&(filter.Month is null||filter.Month==local.Month);
        using var longest=Command(c,"SELECT i.container_id FROM session_intervals i WHERE i.kind='game' AND ($from IS NULL OR i.reporting_date >= $from AND i.reporting_date < $until) GROUP BY i.container_id ORDER BY sum(milliseconds) DESC LIMIT 1",tx,args);
        var longestItem=filter.CharacterId is null&&longest.ExecuteScalar() is string id ? ItemAt(c,tx,id,catalog) : null;
        return new(GetRevision(c,tx),filter.Year.Value,filter.Month,settings.ReportingZone,TotalsAt(c,tx,filter,catalog),points.Values.OrderBy(p=>p.Date,StringComparer.Ordinal).ToArray(),CharactersAt(c,tx,filter),activities.ToArray(),coverage.ToArray(),open,longestItem,filter.CharacterId);
    }
    public void PutJob(JobSummary job, string request, Revision expected)
    {
        using var c=Open(); Execute(c,"INSERT INTO jobs VALUES($id,$kind,$state,$processed,$total,$message,$request,$epoch,$revision) ON CONFLICT(id) DO UPDATE SET state=excluded.state,processed_bytes=excluded.processed_bytes,total_bytes=excluded.total_bytes,message=excluded.message",null,
            ("$id",job.Id),("$kind",job.Kind),("$state",job.State),("$processed",job.ProcessedBytes),("$total",job.TotalBytes),("$message",job.Message),("$request",request),("$epoch",expected.HistoryEpoch),("$revision",expected.DataRevision));
    }
    public JobSummary[] Jobs()
    {
        using var c=Open(true); using var cmd=Command(c,"SELECT id,kind,state,processed_bytes,total_bytes,message FROM jobs ORDER BY rowid DESC LIMIT 20");
        using var r=cmd.ExecuteReader(); var list=new List<JobSummary>(); while(r.Read()) list.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetInt64(4),NullableString(r,5))); return list.ToArray();
    }
    public void InterruptPendingJobs()
    {using var c=Open();Execute(c,"UPDATE jobs SET state='interrupted',message='Tracker restarted. Reimport the same files to resume; activated sources are retained.' WHERE state IN ('queued','running','parsing','waiting_for_game')");}
    public string? Receipt(string id)
    { using var c=Open(true); using var cmd=Command(c,"SELECT result_json FROM mutation_receipts WHERE id=$id AND epoch=(SELECT history_epoch FROM app_state)",null,("$id",id));return cmd.ExecuteScalar() as string; }
    public void SaveReceipt(string id,string result)
    { using var c=Open(); Execute(c,"INSERT OR IGNORE INTO mutation_receipts VALUES($id,(SELECT history_epoch FROM app_state),$result)",null,("$id",id),("$result",result)); }
}
