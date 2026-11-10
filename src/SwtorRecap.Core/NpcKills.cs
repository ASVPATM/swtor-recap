namespace SwtorRecap.Core;

public sealed class NpcDamageState
{
    public long MaxHp { get; set; }
    public long Damage { get; set; }
    public bool Uncertain { get; set; }
    public bool Dead { get; set; }
    public long? LastHp { get; set; }
}

public sealed partial class CombatReducer
{
    private void ProcessNpcKills(ReducerState s, BatchProjection b, string source, SourceRecord r, LogEvent e, ResolvedTime time)
    {
        if (s.Owner is null) return;
        if (e.Source.Key == s.Owner && e.Kind == EventKind.EnterCombat)
        {
            if (!s.NpcCombatActive) s.Npcs.Clear();
            s.NpcCombatActive = true;
        }
        if (e.Source.Key == s.Owner && e.Kind == EventKind.ExitCombat) s.NpcCombatActive = false;
        if (e.Target.Key == s.Owner && e.Kind == EventKind.Death) s.NpcCombatActive = false;
        var target = e.Target;
        if (target.Kind != ActorKind.Npc || catalog.IsTraining(target.TemplateId)) return;
        if (e.Kind == EventKind.Revived) { s.Npcs.Remove(target.Key); return; }
        if (!s.Npcs.TryGetValue(target.Key, out var npc))
        {
            if (e.Kind != EventKind.Damage || e.Source.Key != s.Owner || e.Effective is not > 0) return;
            // Bounded checkpoint size. Eviction can only lose credit, never invent it.
            if (s.Npcs.Count >= 512) s.Npcs.Remove(s.Npcs.FirstOrDefault(p => p.Value.Dead).Key ?? s.Npcs.First().Key);
            s.Npcs[target.Key] = npc = new();
        }
        if (npc.Dead) return;
        // A target returning to full health without a logged heal may have evaded.
        // Start the contribution again instead of combining separate pulls.
        if (e.Kind != EventKind.Heal && target.Hp == target.MaxHp && target.MaxHp > 0 && npc.LastHp < target.MaxHp)
            npc.Damage = 0;
        if (target.MaxHp is > 0 && target.Hp <= target.MaxHp)
        {
            if (npc.MaxHp != 0 && npc.MaxHp != target.MaxHp) npc.Uncertain = true;
            npc.MaxHp = target.MaxHp.Value;
        }
        else npc.Uncertain = true;
        npc.LastHp = target.Hp;
        if (e.Kind == EventKind.Damage && e.Source.Key == s.Owner)
        {
            if (e.Effective is { } damage) npc.Damage = checked(npc.Damage + damage);
            else npc.Uncertain = true;
        }
        if (e.Kind != EventKind.Death) return;
        npc.Dead = true;
        // Use division to avoid overflowing when comparing to twice the damage.
        // Integer division also gives the correct strict majority for odd max HP.
        if (!npc.Uncertain && npc.MaxHp > 0 && npc.Damage > npc.MaxHp / 2)
            AddFact(b, source, r, time, "npc_kill", target.Key, new
            {
                owner = s.Owner, target.Name, target.TemplateId, target.SpawnId,
                damage = npc.Damage, maximumHealth = npc.MaxHp, rule = "personal_effective_over_half_v1"
            });
    }
}
