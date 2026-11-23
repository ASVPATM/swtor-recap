namespace SwtorRecap.Core;

public sealed partial class CombatReducer
{
    private static bool BelowThreshold(Actor actor, int percent)
        => actor.Hp is >= 0 && actor.MaxHp is > 0 && (decimal)actor.Hp.Value * 100 <= (decimal)actor.MaxHp.Value * percent;

    private static bool Recent(TimeOnly? start, int epoch, TimeOnly end, int endEpoch, int seconds)
    {
        if (start is null || epoch != endEpoch) return false;
        var elapsed = (end - start.Value).TotalSeconds;
        if (elapsed < 0) elapsed += 86400;
        return elapsed <= seconds;
    }

    private void ProcessInferredEnding(ReducerState s, BatchProjection b, string source, SourceRecord r, LogEvent e,
        ResolvedTime time, ActivityDefinition activity, EncounterDefinition definition, AttemptState attempt)
    {
        if (e.Kind == EventKind.Death && e.Target.Kind == ActorKind.Player) attempt.DeadParticipants.Add(e.Target.Key);
        if (e.Kind == EventKind.Revived) attempt.DeadParticipants.Remove(e.Target.Key);
        if (definition.Rule is not ("hp_threshold_exit" or "signal_exit") || attempt.Result == "won" || attempt.InferenceBlocked) return;
        var changed = false;
        if (e.Kind == EventKind.ExitCombat && attempt.Participants.Contains(e.Source.Key) && !attempt.DeadParticipants.Contains(e.Source.Key))
        {
            attempt.ClosingExitAt = e.Time; attempt.ClosingExitEpoch = time.Epoch; changed = true;
            attempt.Evidence.Add(AddFact(b, source, r, time, "closing_exit", e.Source.Key, new { attemptId = attempt.Id }));
        }
        foreach (var signal in definition.VictorySignals ?? [])
        {
            var role = definition.Roles.SingleOrDefault(x => x.Id == signal.RoleId);
            if (role is not null && !role.TemplateIds.Contains(e.Source.TemplateId)) continue;
            var matches = signal.Kind switch
            {
                "ability" => e.TypeId == EffectIds.Event && e.EffectId == "836045448945479" && e.AbilityId == signal.Id,
                "damage" => e.Kind == EventKind.Damage && e.AbilityId == signal.Id,
                "remove" => e.TypeId == EffectIds.Remove && e.EffectId == signal.Id,
                "loot" => e.TypeId == EffectIds.Event && e.EffectId == "836045448953668"
                    && new[] { e.Source, e.Target }.Any(a => a.Kind == ActorKind.Npc && a.TemplateId == signal.Id),
                _ => false
            };
            if (!matches) continue;
            attempt.SignalAt = e.Time; attempt.SignalEpoch = time.Epoch; changed = true;
            attempt.Evidence.Add(AddFact(b, source, r, time, "victory_signal", e.Source.Key, new { signal.Kind, signal.Id }));
        }
        var window = definition.Ending?.WindowSeconds ?? 60;
        var hasExit = Recent(attempt.ClosingExitAt, attempt.ClosingExitEpoch, e.Time, time.Epoch, window);
        var hasSignal = Recent(attempt.SignalAt, attempt.SignalEpoch, e.Time, time.Epoch, window);
        var ready = definition.Rule == "signal_exit" ? hasSignal : definition.Ending is { } ending
            && AllRequiredTargetsDead(definition, attempt, s.Difficulty, s.GroupSize)
            && Recent(attempt.TerminalAt, attempt.TerminalEpoch, e.Time, time.Epoch, window)
            && (ending.AbilityIds is not { Length: > 0 } || hasSignal) && (!ending.RequireImmune || attempt.ImmuneSeen)
            // A death/stealth exit earlier in the pull cannot finish a later low-health phase.
            && Recent(attempt.TerminalAt, attempt.TerminalEpoch, attempt.ClosingExitAt ?? e.Time, attempt.ClosingExitEpoch, window);
        if (hasExit && ready) { Win(s, b, attempt, activity, definition, time, "inferred"); changed = true; }
        if (changed) b.Attempts[attempt.Id] = Json.Clone(attempt);
    }
}
