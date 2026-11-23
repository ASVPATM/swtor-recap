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
    public HashSet<string> DeadTargets { get; set; } = [];
    public HashSet<string> DeadParticipants { get; set; } = [];
    public TimeOnly? TerminalAt { get; set; }
    public int TerminalEpoch { get; set; }
    public TimeOnly? SignalAt { get; set; }
    public int SignalEpoch { get; set; }
    public TimeOnly? ClosingExitAt { get; set; }
    public int ClosingExitEpoch { get; set; }
    public bool ImmuneSeen { get; set; }
    public bool InferenceBlocked { get; set; }
    public long EndingMaxHp { get; set; }
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
                var definition = catalog.Document.Activities.Single(x => x.Id == a.ActivityId).Encounters.Single(x => x.Id == a.EncounterId);
                // Coratanni/Pearl and Ruugar are two combat phases of one encounter.
                if (definition.ContinueAfterRoles is { Length: > 0 } phaseRoles && definition.Ending is { } ending
                    && phaseRoles.SelectMany(id => definition.Roles.Single(x => x.Id == id).SlotIds(s.GroupSize)).All(a.DeadTargets.Contains)
                    && !a.Entities.ContainsKey(ending.RoleId))
                { a.BoundaryUncertain = false; continue; }
                // A loot object can cause EnterCombat immediately before its TargetSet record.
                if (definition.Rule == "signal_exit" && Recent(a.ClosingExitAt, a.ClosingExitEpoch, e.Time, time.Epoch, 60)
                    && !a.DeadParticipants.Contains(e.Source.Key) && !a.InferenceBlocked) continue;
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
        b.State.Gap = true;
        foreach (var run in b.State.Runs.Values.Where(run => !run.Terminal))
        { run.Gap = true; b.Runs[run.Id] = Json.Clone(run); }
        foreach (var attempt in b.State.Attempts.Values.Where(a => a.Result != "won").ToArray())
        {
            if (attempt.Result != "unconfirmed") { attempt.Result = "interrupted"; attempt.Qualified = false; }
            b.Attempts[attempt.Id] = Json.Clone(attempt); b.State.Attempts.Remove(attempt.EncounterId);
        }
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
            if (!s.Attempts.TryGetValue(definition.Id, out var attempt))
            {
                if (e.Kind is not (EventKind.Damage or EventKind.Heal) || actor.Hp == 0) continue;
                attempt = NewAttempt(s, b, source, r, activity, definition);
            }
            if (definition.Rule == "signal_exit" && attempt.ClosingExitAt is not null && attempt.Result != "won"
                && e.Kind is EventKind.Damage or EventKind.Heal)
            {
                attempt.Result = "interrupted"; attempt.Qualified = false; b.Attempts[attempt.Id] = Json.Clone(attempt);
                attempt = NewAttempt(s, b, source, r, activity, definition);
            }
            // Several required NPCs can share a template (for example Braxx's two hounds).
            // Bind each spawn to its own slot; switching targets is not a new pull.
            var slots = role.SlotIds(s.GroupSize).ToArray();
            var slot = slots.FirstOrDefault(id => attempt.Entities.GetValueOrDefault(id) == actor.Key)
                ?? slots.FirstOrDefault(id => !attempt.Entities.ContainsKey(id));
            if (slot is null && !role.Required) slot = slots[0]; // Adds may respawn within one fight.
            if (slot is null)
            {
                // A late death from a previous pull cannot replace a live required target.
                if (!(actor.Hp > 0 || e.Kind is EventKind.Damage or EventKind.Heal)) continue;
                if (attempt.Result != "won") { attempt.Result = "interrupted"; attempt.Qualified = false; b.Attempts[attempt.Id] = Json.Clone(attempt); }
                attempt = NewAttempt(s, b, source, r, activity, definition);
                slot = slots[0];
            }
            var threshold = definition.Ending is { } ending && ending.RoleId == role.Id ? ending.HealthPercent : 0;
            if (threshold > 0)
            {
                if (actor.MaxHp is > 0)
                {
                    if (attempt.EndingMaxHp > 0 && attempt.EndingMaxHp != actor.MaxHp) attempt.InferenceBlocked = true;
                    attempt.EndingMaxHp = actor.MaxHp.Value;
                }
                else if (attempt.Terminal.Contains(slot)) attempt.InferenceBlocked = true;
            }
            var aboveTerminal = actor.Hp > 0 && (threshold == 0 || !BelowThreshold(actor, threshold));
            if ((attempt.Result == "outcome_pending" || attempt.Result == "won" && attempt.Basis == "inferred") && aboveTerminal && attempt.Terminal.Contains(slot))
            {
                attempt.Result = "unknown"; attempt.Qualified = false; attempt.Terminal.Clear();
                attempt.TerminalAt = null; attempt.SignalAt = null; attempt.ClosingExitAt = null; attempt.ImmuneSeen = false;
                if (s.Runs.TryGetValue(activity.Id, out var contradicted))
                {
                    contradicted.Wins.Remove(definition.Id); contradicted.Terminal = false; contradicted.Full = false; contradicted.TerminalAttempt = null;
                    b.Runs[contradicted.Id] = Json.Clone(contradicted);
                }
                attempt.Evidence.Add(AddFact(b, source, r, time, "contradiction", actor.Key, new { actor.Hp }));
            }
            attempt.Entities[slot] = actor.Key;
            if (actor.Hp > 0 && attempt.Alive.Add(slot)) attempt.Evidence.Add(AddFact(b, source, r, time, "alive", actor.Key, new { actor.TemplateId, actor.Hp }));
            if (e.Kind is EventKind.Damage or EventKind.Heal)
            {
                foreach (var participant in new[] { e.Source, e.Target }.Where(p => p.Kind == ActorKind.Player))
                    if (attempt.Participants.Add(participant.Key)) attempt.Evidence.Add(AddFact(b, source, r, time, "participation", participant.Key, new { attemptId = attempt.Id }));
            }
            var death = e.Kind == EventKind.Death && e.Target.Key == actor.Key;
            var required = role.IsRequired(s.Difficulty);
            var hpCandidate = attempt.Alive.Contains(slot) && (definition.Rule == "hp_zero_exit" && actor.Hp == 0
                || definition.Rule == "hp_threshold_exit" && threshold > 0 && BelowThreshold(actor, threshold));
            if (death && required) attempt.DeadTargets.Add(slot);
            if ((death || hpCandidate) && required && attempt.Terminal.Add(slot))
            {
                attempt.Evidence.Add(AddFact(b, source, r, time, death ? "death" : "hp_zero", actor.Key, new { actor.TemplateId, actor.SpawnId, actor.Hp }));
                attempt.Result = "outcome_pending";
                attempt.Date = time.ReportingDate; attempt.EndUtc = time.Utc?.ToString("O", CultureInfo.InvariantCulture);
                if (hpCandidate) { attempt.TerminalAt = e.Time; attempt.TerminalEpoch = time.Epoch; }
            }
            if (hpCandidate && e.Target.Key == actor.Key && e.Immune) attempt.ImmuneSeen = true;
            if (hpCandidate && definition.Ending?.AbilityIds?.Contains(e.AbilityId) == true
                && e.Source.Key == actor.Key && e.TypeId == EffectIds.Event && e.EffectId == "836045448945479")
            {
                attempt.SignalAt = e.Time; attempt.SignalEpoch = time.Epoch;
                attempt.Evidence.Add(AddFact(b, source, r, time, "terminal_ability", actor.Key, new { e.AbilityId }));
            }
            var allDead = AllRequiredTargetsDead(definition, attempt, s.Difficulty, s.GroupSize);
            if (death && required && (definition.Rule == "death_any" || definition.Rule == "death_all" && allDead)) Win(s, b, attempt, activity, definition, time, "observed");
            if (death && definition.Rule == "hp_threshold_exit" && allDead
                && RequiredSlots(definition, s.Difficulty, s.GroupSize).All(attempt.DeadTargets.Contains))
                Win(s, b, attempt, activity, definition, time, "observed");
            if (definition.Rule == "scripted" && e.EffectId == definition.VictoryId && e.TypeId == EffectIds.Apply && e.Source.Key == actor.Key && attempt.Alive.Contains(slot))
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
            ProcessInferredEnding(s, b, source, r, e, time, activity, definition, attempt);
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
                if (definition.Rule == "hp_zero_exit" && attempt.Result == "outcome_pending" && AllRequiredTargetsDead(definition, attempt, s.Difficulty, s.GroupSize))
                {
                    attempt.Evidence.Add(AddFact(b, source, r, time, "closing_exit", e.Source.Key, new { attemptId = attempt.Id }));
                    Win(s, b, attempt, activity, definition, time, "inferred");
                }
                else if (attempt.Result != "won") attempt.BoundaryUncertain = true;
                b.Attempts[attempt.Id] = Json.Clone(attempt);
            }
        }
    }
    private static IEnumerable<string> RequiredSlots(EncounterDefinition definition, string? difficulty, int? groupSize)
        => definition.Roles.Where(r => r.IsRequired(difficulty)).SelectMany(r => r.SlotIds(groupSize));
    private static bool AllRequiredTargetsDead(EncounterDefinition definition, AttemptState attempt, string? difficulty, int? groupSize)
    {
        var required = RequiredSlots(definition, difficulty, groupSize).ToArray();
        return required.Length > 0 && required.All(attempt.Terminal.Contains);
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
        if (basis == "inferred" && attempt.InferenceBlocked) { attempt.Result = "unconfirmed"; return; }
        if (!(basis=="observed" && definition.Rule is "death_all" or "death_any" or "hp_threshold_exit") && definition.Status is not ("terminal-tested" or "full-run-tested" or "logic-tested"))
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
