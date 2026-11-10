using System.Globalization;

namespace SwtorRecap.Core;

public sealed class LifeState
{
    public bool Dead { get; set; }
    public DateTimeOffset? CombatStart { get; set; }
    public long CombatOffset { get; set; }
    public int ClockEpoch { get; set; }
}
public sealed class AttemptState
{
    public string Id { get; set; } = "";
    public string ActivityId { get; set; } = "";
    public string EncounterId { get; set; } = "";
    public string RunId { get; set; } = "";
    public string SegmentId { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string RuleVersion { get; set; } = Versions.Catalog;
    public string Result { get; set; } = "recognized";
    public string Basis { get; set; } = "observed";
    public string? Owner { get; set; }
    public string? Date { get; set; }
    public string? EndUtc { get; set; }
    public Dictionary<string, string> Entities { get; set; } = [];
    public HashSet<string> Alive { get; set; } = [];
    public HashSet<string> Terminal { get; set; } = [];
    public HashSet<string> Participants { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
    public bool Qualified { get; set; }
    public bool BoundaryUncertain { get; set; }
}
public sealed class RunState
{
    public string Id { get; set; } = "";
    public string ActivityId { get; set; } = "";
    public string SegmentId { get; set; } = "";
    public HashSet<string> Wins { get; set; } = [];
    public bool Terminal { get; set; }
    public bool Full { get; set; }
    public bool Gap { get; set; }
    public string? Date { get; set; }
    public string? TerminalAttempt { get; set; }
}
public sealed class ReducerState
{
    public int Version { get; set; } = Versions.Reducer;
    public LogClockState Clock { get; set; } = new();
    public Segment? Segment { get; set; }
    public string? Owner { get; set; }
    public string? OwnerName { get; set; }
    public string? Server { get; set; }
    public string? Area { get; set; }
    public string? Difficulty { get; set; }
    public int? GroupSize { get; set; }
    public string? Format { get; set; }
    public bool UnsupportedFormat { get; set; }
    public bool DiscardPartial { get; set; }
    public bool Gap { get; set; }
    public long LastSourceEnd { get; set; }
    public string? LastExclusionSource { get; set; }
    public long LastExclusionEnd { get; set; } = -1;
    public Dictionary<string, LifeState> Lives { get; set; } = [];
    public Dictionary<string, AttemptState> Attempts { get; set; } = [];
    public Dictionary<string, RunState> Runs { get; set; } = [];
    public Dictionary<string, NpcDamageState> Npcs { get; set; } = [];
    public bool NpcCombatActive { get; set; }
}
public sealed class BatchProjection
{
    public required ReducerState State { get; init; }
    public List<Segment> Segments { get; } = [];
    public Dictionary<string, Bucket> Buckets { get; } = [];
    public List<Fact> Facts { get; } = [];
    public List<Gap> Gaps { get; } = [];
    public List<SourceExclusion> Exclusions { get; } = [];
    public List<RecordedInterval> Intervals { get; } = [];
    public Dictionary<string, AttemptState> Attempts { get; } = [];
    public Dictionary<string, RunState> Runs { get; } = [];
    public long End { get; set; }
    public int Accepted { get; set; }
    public int Rejected { get; set; }
    public int Unknown { get; set; }
}
public sealed record SourceExclusion(string SourceId, long Start, long End);

public sealed partial class CombatReducer(Catalog catalog, string reportingZone)
{
    private readonly CombatLogParser _parser = new();
    public BatchProjection Reduce(ReducerState committed, string sourceId, string containerId, IEnumerable<SourceRecord> records)
    {
        if (committed.Version != Versions.Reducer) throw new InvalidOperationException("Reducer version requires source replay.");
        var state = Json.Clone(committed);
        var batch = new BatchProjection { State = state };
        foreach (var record in records)
        {
            batch.End = record.End;
            if (state.Segment is { } previousSegment && previousSegment.SourceId != sourceId)
            {
                ReplaceSegment(batch, previousSegment with { End = state.LastSourceEnd });
                state.Segment = null;
            }
            if (state.Segment is null) NewSegment(state, batch, sourceId, record.Start);
            state.LastSourceEnd = record.End;
            if (record.Rejection is not null) { Reject(batch, sourceId, record, record.Rejection); continue; }
            var parsed = _parser.Parse(record.Text.AsSpan());
            if (parsed.Status is ParseStatus.Malformed or ParseStatus.Unsupported)
            {
                if (parsed.Status == ParseStatus.Unsupported) state.UnsupportedFormat = true;
                Reject(batch, sourceId, record, parsed.Reason!); continue;
            }
            if (state.UnsupportedFormat) { Reject(batch, sourceId, record, "unsupported_profile"); continue; }
            var e = parsed.Event!;
            var previousEpoch = state.Clock.Epoch;
            var time = state.Clock.Resolve(e.Time, reportingZone);
            if (time.Epoch != previousEpoch)
            {
                Interrupt(state, batch, "clock_discontinuity");
                batch.Gaps.Add(new(sourceId, record.Start, record.End, "time", "clock_discontinuity"));
            }
            if (parsed.Status == ParseStatus.UnknownSemantic) batch.Unknown++;
            batch.Accepted++;
            if (state.Owner == e.Source.Key && state.OwnerName == "Selected character")
            {
                state.OwnerName = e.Source.Name;
                state.Segment = state.Segment! with { Name = e.Source.Name };
            }

            if (e.Kind == EventKind.AreaEntered)
            {
                // A proved new area/character starts a fresh run scope. Leaving the previous
                // scope interrupts its unfinished attempts; it is not missing evidence in
                // the new activity. Repeated same-area metadata must not erase a real gap.
                var freshScope = state.Area != e.AreaId || state.Owner is not null &&
                    (state.Owner != e.Source.Key || state.Server is not null && state.Server != e.Server);
                if (state.Owner is null && state.Lives.Count > 0 && state.Segment!.Start < record.Start)
                {
                    batch.Gaps.Add(new(sourceId,state.Segment.Start,record.Start,"owner","prefix_continuity_unproven"));
                    Interrupt(state,batch,"owner_prefix_unproved");NewSegment(state,batch,sourceId,record.Start);
                }
                if (state.Owner is not null && (state.Owner != e.Source.Key || state.Server is not null && state.Server != e.Server))
                {
                    Interrupt(state, batch, "owner_changed");
                    state.Owner = null; state.Lives.Clear();
                    NewSegment(state, batch, sourceId, record.Start);
                }
                if (state.Area is not null && state.Area != e.AreaId) Interrupt(state, batch, "area_changed");
                if (freshScope) { state.Gap = false; state.Npcs.Clear(); state.NpcCombatActive = false; }
                state.Area = e.AreaId; state.Difficulty = e.DifficultyId; state.GroupSize = e.GroupSize;
                state.Server = e.Server; state.Format = e.Format;
                BindOwner(state, batch, e.Source, "observed_area", sourceId);
                AddFact(batch, sourceId, record, time, "area_owner", e.Source.Key, new { e.AreaId, e.DifficultyId, e.GroupSize, e.Server, e.Format });
            }
            else if (e.Kind == EventKind.EnterCombat && e.Source.Kind == ActorKind.Player && e.Target.Kind == ActorKind.None && state.Owner is null)
            {
                BindOwner(state, batch, e.Source, "inferred_local_enter", sourceId);
                AddFact(batch, sourceId, record, time, "owner_enter", e.Source.Key, new { basis = "local_enter_pattern" });
            }

            if (e.Source.Kind == ActorKind.Player && (e.Kind is EventKind.Damage or EventKind.Heal) && (e.Kind != EventKind.Damage || e.Source.Key != e.Target.Key))
            {
                // Remote candidate aggregates are retained only until the segment owner is known.
                if (state.Owner is null || e.Source.Key == state.Owner)
                {
                    if (state.Owner is null && !state.Lives.ContainsKey(e.Source.Key))
                        AddFact(batch, sourceId, record, time, "owner_candidate", e.Source.Key, new { e.Source.Name });
                    Candidate(state, e.Source.Key);
                    var localMinute = time.Local?.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture) ?? "undated:" + e.Time.ToString("HH:mm", CultureInfo.InvariantCulture);
                    var utcMinute = time.Utc?.ToUnixTimeMilliseconds() / 60000;
                    var context = state.Area ?? "unknown";
                    var key = Keys.Stable(state.Segment!.Id, e.Source.Key, context, localMinute, utcMinute, time.Epoch, time.ReportingDate);
                    if (!batch.Buckets.TryGetValue(key, out var bucket)) batch.Buckets[key] = bucket = new(key, state.Segment.Id, e.Source.Key, context, localMinute, utcMinute, time.ReportingDate, time.Quality, time.Epoch, new());
                    bucket.Amounts.Add(e);
                }
            }

            ProcessNpcKills(state, batch, sourceId, record, e, time);
            ProcessLife(state, batch, sourceId, containerId, record, e, time);
            ProcessEncounters(state, batch, sourceId, record, e, time);
        }
        if (state.Segment is { } segment) ReplaceSegment(batch, segment);
        return batch;
    }

    private static LifeState Candidate(ReducerState state, string key)
    {
        if (state.Lives.TryGetValue(key, out var candidate)) return candidate;
        if (state.Lives.Count >= 64) throw new InvalidOperationException("Owner candidate bound reached; source remains available for explicit attribution and replay.");
        return state.Lives[key] = new();
    }
    private void ProcessLife(ReducerState s, BatchProjection b, string source, string container, SourceRecord r, LogEvent e, ResolvedTime time)
    {
        var actor = e.Kind is EventKind.Death or EventKind.Revived ? e.Target : e.Source;
        if (actor.Kind != ActorKind.Player || s.Owner is not null && s.Owner != actor.Key) return;
        var life = Candidate(s, actor.Key);
        if (e.Kind == EventKind.EnterCombat)
        {
            if (life.CombatStart is null) { life.CombatStart = time.Utc; life.CombatOffset = r.Start; life.ClockEpoch = time.Epoch; }
            foreach (var a in s.Attempts.Values.Where(a => a.BoundaryUncertain && a.Result != "won").ToArray())
            {
                if(a.Result!="unconfirmed"){a.Result = "interrupted"; a.Qualified = false;} b.Attempts[a.Id] = Json.Clone(a);
                s.Attempts.Remove(a.EncounterId);
            }
        }
        if (e.Kind == EventKind.Death && !life.Dead)
        {
            life.Dead = true;
            AddFact(b, source, r, time, "player_death", actor.Key, new { segmentId = s.Segment!.Id });
        }
        if (e.Kind == EventKind.Revived) life.Dead = false;
        if (e.Kind == EventKind.ExitCombat)
        {
            if (life.CombatStart is { } start && time.Utc is { } end && end >= start && life.ClockEpoch == time.Epoch)
            {
                foreach (var part in CalendarAllocation.Split(start, end, reportingZone))
                    b.Intervals.Add(new(Keys.Stable(source, life.CombatOffset, actor.Key, part.Date, "combat"), container, s.Segment!.Id, actor.Key,
                        "combat", part.Start, part.End, part.Milliseconds, part.Date, time.Quality));
            }
            else if (life.CombatStart is not null) b.Gaps.Add(new(source, life.CombatOffset, r.End, "time", "combat_clock_uncertain"));
            life.CombatStart = null;
        }
    }
    private static void BindOwner(ReducerState s, BatchProjection b, Actor actor, string basis, string source)
    {
        s.Owner = actor.Key; s.OwnerName = actor.Name;
        var character = Keys.Stable(s.Server ?? source, actor.PlayerId);
        s.Segment = s.Segment! with { Owner = actor.Key, CharacterId = character, Name = actor.Name, Attribution = basis, Server = s.Server };
        ReplaceSegment(b, s.Segment);
        foreach (var a in s.Attempts.Values)
        {
            a.Owner = s.Owner;
            if (a.Result == "won") a.Qualified = a.Participants.Contains(s.Owner);
            b.Attempts[a.Id] = Json.Clone(a);
        }
    }
    private static void NewSegment(ReducerState s, BatchProjection b, string source, long offset)
    {
        if (s.Segment is { } previous) ReplaceSegment(b, previous with { End = offset });
        s.Segment = new(Keys.Stable(source, offset), source, offset, null, s.Owner,
            s.Owner is null ? null : Keys.Stable(s.Server ?? source, s.Owner[2..]), s.OwnerName, s.Owner is null ? "unknown" : "retained_or_user_supplied", s.Server);
        ReplaceSegment(b, s.Segment);
    }
    private static void ReplaceSegment(BatchProjection b, Segment segment)
    {
        b.Segments.RemoveAll(x => x.Id == segment.Id); b.Segments.Add(segment);
    }
    public static void Interrupt(ReducerState s, BatchProjection b, string reason)
    {
        foreach (var a in s.Attempts.Values.Where(a => a.Result != "won")) { if(a.Result!="unconfirmed")a.Result = "interrupted"; b.Attempts[a.Id] = Json.Clone(a); }
        foreach (var run in s.Runs.Values.Where(r => !r.Terminal)) { run.Gap = true; b.Runs[run.Id] = Json.Clone(run); }
        s.Attempts.Clear(); s.Runs.Clear(); s.Gap = true;
        s.Npcs.Clear(); s.NpcCombatActive = false;
        foreach (var life in s.Lives.Values) life.CombatStart = null;
    }
    private static void Reject(BatchProjection b, string source, SourceRecord r, string reason)
    {
        b.Rejected++; b.Gaps.Add(new(source, r.Start, r.End, "parser", reason));
        b.State.Npcs.Clear(); // Missing records may hide a reset or a changed target.
    }
    private static string AddFact(BatchProjection b, string source, SourceRecord r, ResolvedTime time, string kind, string entity, object payload)
    {
        var id = Keys.Stable(source, r.Start, kind, entity);
        if (!b.Facts.Any(f => f.Id == id)) b.Facts.Add(new(id, source, r.Start, r.End, kind, entity, time, Json.Write(payload)));
        return id;
    }
    private void ProcessEncounters(ReducerState s, BatchProjection b, string source, SourceRecord r, LogEvent e, ResolvedTime time)
    {
        foreach (var actor in new[] { e.Source, e.Target }.DistinctBy(a => a.Key))
        {
            if (actor.Kind != ActorKind.Npc) continue;
            var found = catalog.Find(actor.TemplateId, s.Area);
            if (found is not { } match) continue;
            var (activity, definition, role) = match;
            if (activity.Type == "training") continue;
            if (!role.Required && definition.Rule != "unsupported") continue;
            if (!s.Attempts.TryGetValue(definition.Id, out var attempt))
            {
                if (e.Kind is not (EventKind.Damage or EventKind.Heal) || actor.Hp == 0) continue;
                attempt = NewAttempt(s, b, source, r, activity, definition);
            }
            if (attempt.Entities.TryGetValue(role.Id, out var prior) && prior != actor.Key && (actor.Hp > 0 || e.Kind is EventKind.Damage or EventKind.Heal))
            {
                if (attempt.Result != "won") { attempt.Result = "interrupted"; attempt.Qualified = false; b.Attempts[attempt.Id] = Json.Clone(attempt); }
                attempt = NewAttempt(s, b, source, r, activity, definition);
            }
            if ((attempt.Result == "outcome_pending" || attempt.Result == "won" && attempt.Basis == "inferred") && actor.Hp > 0 && attempt.Terminal.Contains(role.Id))
            {
                attempt.Result = "unknown"; attempt.Qualified = false; attempt.Terminal.Clear();
                if (s.Runs.TryGetValue(activity.Id, out var contradicted))
                {
                    contradicted.Wins.Remove(definition.Id); contradicted.Terminal = false; contradicted.Full = false; contradicted.TerminalAttempt = null;
                    b.Runs[contradicted.Id] = Json.Clone(contradicted);
                }
                attempt.Evidence.Add(AddFact(b, source, r, time, "contradiction", actor.Key, new { actor.Hp }));
            }
            attempt.Entities[role.Id] = actor.Key;
            if (actor.Hp > 0 && attempt.Alive.Add(role.Id)) attempt.Evidence.Add(AddFact(b, source, r, time, "alive", actor.Key, new { actor.TemplateId, actor.Hp }));
            if (e.Kind is EventKind.Damage or EventKind.Heal)
            {
                foreach (var participant in new[] { e.Source, e.Target }.Where(p => p.Kind == ActorKind.Player))
                    if (attempt.Participants.Add(participant.Key)) attempt.Evidence.Add(AddFact(b, source, r, time, "participation", participant.Key, new { attemptId = attempt.Id }));
            }
            var death = e.Kind == EventKind.Death && e.Target.Key == actor.Key;
            var hpCandidate = definition.Rule == "hp_zero_exit" && actor.Hp == 0 && attempt.Alive.Contains(role.Id);
            if ((death || hpCandidate) && role.Required && attempt.Terminal.Add(role.Id))
            {
                attempt.Evidence.Add(AddFact(b, source, r, time, death ? "death" : "hp_zero", actor.Key, new { actor.TemplateId, actor.SpawnId, actor.Hp }));
                attempt.Result = "outcome_pending";
                attempt.Date = time.ReportingDate; attempt.EndUtc = time.Utc?.ToString("O", CultureInfo.InvariantCulture);
            }
            var allDead = definition.Roles.Where(x => x.Required).All(x => attempt.Terminal.Contains(x.Id)) && definition.Roles.Any(x => x.Required);
            if (death && (definition.Rule == "death_any" || definition.Rule == "death_all" && allDead)) Win(s, b, attempt, activity, definition, time, "observed");
            if (definition.Rule == "scripted" && e.EffectId == definition.VictoryId && e.TypeId == EffectIds.Apply && e.Source.Key == actor.Key && attempt.Alive.Contains(role.Id))
            {
                attempt.Evidence.Add(AddFact(b, source, r, time, "scripted_terminal", actor.Key, new { e.EffectId }));
                Win(s, b, attempt, activity, definition, time, "inferred");
            }
            b.Attempts[attempt.Id] = Json.Clone(attempt);
        }
        foreach (var attempt in s.Attempts.Values)
        {
            var activity = catalog.Document.Activities.Single(a => a.Id == attempt.ActivityId);
            var definition = activity.Encounters.Single(a => a.Id == attempt.EncounterId);
            if (e.Kind == EventKind.Heal && e.Source.Kind == ActorKind.Player && s.Lives.TryGetValue(e.Source.Key, out var life) && life.CombatStart is not null
                && attempt.Participants.Contains(e.Target.Key) && attempt.Result != "won" && attempt.Participants.Add(e.Source.Key))
            {
                attempt.Evidence.Add(AddFact(b, source, r, time, "healer_participation", e.Source.Key, new { attemptId = attempt.Id }));
                b.Attempts[attempt.Id] = Json.Clone(attempt);
            }
            if (e.Kind == EventKind.ExitCombat && e.Source.Key == s.Owner)
            {
                if(definition.Rule=="unsupported")
                {attempt.Result="unconfirmed";attempt.Qualified=s.Owner is not null&&attempt.Participants.Contains(s.Owner);attempt.Date=time.ReportingDate;}
                if (definition.Rule == "hp_zero_exit" && attempt.Result == "outcome_pending" && definition.Roles.Where(x => x.Required).All(x => attempt.Terminal.Contains(x.Id)))
                {
                    attempt.Evidence.Add(AddFact(b, source, r, time, "closing_exit", e.Source.Key, new { attemptId = attempt.Id }));
                    Win(s, b, attempt, activity, definition, time, "inferred");
                }
                else if (attempt.Result != "won") attempt.BoundaryUncertain = true;
                b.Attempts[attempt.Id] = Json.Clone(attempt);
            }
        }
    }
    private AttemptState NewAttempt(ReducerState s, BatchProjection b, string source, SourceRecord r, ActivityDefinition activity, EncounterDefinition encounter)
    {
        if (s.Attempts.Count >= 32 && !s.Attempts.ContainsKey(encounter.Id)) throw new InvalidOperationException("Active encounter bound reached; replay required.");
        if (!s.Runs.TryGetValue(activity.Id, out var run) || run.Terminal)
        {
            run = new() { Id = Keys.Stable(source, r.Start, activity.Id, "run"), ActivityId = activity.Id, SegmentId = s.Segment!.Id, Gap = s.Gap };
            s.Runs[activity.Id] = run; b.Runs[run.Id] = Json.Clone(run);
        }
        var attempt = new AttemptState { Id = Keys.Stable(source, r.Start, encounter.Id), ActivityId = activity.Id, EncounterId = encounter.Id,
            RunId = run.Id, SegmentId = s.Segment!.Id, SourceId = source, Owner = s.Owner };
        s.Attempts[encounter.Id] = attempt;
        return attempt;
    }
    private static void Win(ReducerState s, BatchProjection b, AttemptState attempt, ActivityDefinition activity, EncounterDefinition definition, ResolvedTime time, string basis)
    {
        // Explicit deaths of the catalog's required targets use one shared rule.
        // Inferred/scripted endings still require a separately validated rule.
        if (!(basis=="observed" && definition.Rule is "death_all" or "death_any") && definition.Status is not ("terminal-tested" or "full-run-tested" or "logic-tested"))
        { attempt.Result = "unconfirmed"; return; }
        attempt.Result = "won"; attempt.Basis = basis; attempt.Owner = s.Owner;
        attempt.Date = time.ReportingDate; attempt.EndUtc = time.Utc?.ToString("O", CultureInfo.InvariantCulture);
        attempt.Qualified = s.Owner is not null && attempt.Participants.Contains(s.Owner);
        if (!attempt.Qualified || !s.Runs.TryGetValue(activity.Id, out var run)) return;
        run.Wins.Add(definition.Id);
        if (definition.Final) { run.Terminal = true; run.TerminalAttempt = attempt.Id; run.Date = time.ReportingDate; }
        run.Full = run.Terminal && !run.Gap && activity.CompleteObjectives && activity.RequiredGroups.Length > 0 && activity.RequiredGroups.All(g => g.Any(run.Wins.Contains));
        b.Runs[run.Id] = Json.Clone(run);
    }
}
