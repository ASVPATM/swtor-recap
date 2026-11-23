namespace SwtorRecap.Core;

public sealed record EntityRole(string Id, string Name, string[] TemplateIds, bool Required, int Count = 1,
    string[]? NotRequiredDifficulties = null, Dictionary<int, int>? CountsByGroupSize = null)
{
    // Unknown difficulties retain every required target. A relaxed rule must name a proved mode.
    public bool IsRequired(string? difficulty) => Required && !(NotRequiredDifficulties?.Contains(difficulty) ?? false);
    public int RequiredCount(int? groupSize) => groupSize is { } size && CountsByGroupSize?.TryGetValue(size, out var count) == true ? count : Count;
    public IEnumerable<string> SlotIds(int? groupSize = null) => Enumerable.Range(0, RequiredCount(groupSize)).Select(i => i == 0 ? Id : $"{Id}#{i}");
}
public sealed record EncounterEnding(string RoleId, int HealthPercent, string[]? AbilityIds = null,
    bool RequireImmune = false, int WindowSeconds = 60);
public sealed record VictorySignal(string Kind, string Id, string? RoleId = null);
public sealed record EncounterDefinition(string Id, string Name, EntityRole[] Roles, string Rule, string? VictoryId,
    bool Final, bool Optional, string Status, string[] PositiveFixtures, string[] NegativeFixtures,
    EncounterEnding? Ending = null, string[]? ContinueAfterRoles = null, VictorySignal[]? VictorySignals = null);
public sealed record ActivityDefinition(string Id, string Name, string Type, string[] AreaIds, EncounterDefinition[] Encounters,
    string[][] RequiredGroups, bool CompleteObjectives, string Provenance, string Commit, string License);
public sealed record CatalogDocument(string Version, ActivityDefinition[] Activities);

public sealed class Catalog
{
    public CatalogDocument Document { get; }
    private readonly Dictionary<string, List<(ActivityDefinition Activity, EncounterDefinition Encounter, EntityRole Role)>> _templates = [];
    public Catalog(CatalogDocument document)
    {
        Document = document;
        Validate();
        foreach (var a in document.Activities)
        foreach (var e in a.Encounters)
        foreach (var role in e.Roles)
        foreach (var id in role.TemplateIds.Distinct(StringComparer.Ordinal))
        {
            if (!_templates.TryGetValue(id, out var entries)) _templates[id] = entries = [];
            entries.Add((a, e, role));
        }
    }
    public static Catalog LoadDefault()
    {
        using var stream = typeof(Catalog).Assembly.GetManifestResourceStream("SwtorRecap.Core.catalog.json") ?? throw new InvalidOperationException("Catalog missing.");
        using var reader = new StreamReader(stream);
        return new(Json.Read<CatalogDocument>(reader.ReadToEnd()));
    }
    public (ActivityDefinition Activity, EncounterDefinition Encounter, EntityRole Role)? Find(string? template, string? area)
    {
        if (template is null || !_templates.TryGetValue(template, out var candidates)) return null;
        var matches = candidates.Where(c => area is null || c.Activity.AreaIds.Contains(area)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    // Identity and victory support are separate: an unverified ending is still a known boss.
    public bool IsBossOrTraining(string? template) => template is not null && _templates.ContainsKey(template);
    public bool IsTraining(string? template) => template is not null && _templates.TryGetValue(template,out var entries)
        && entries.Any(e=>e.Activity.Type=="training");
    public void Validate()
    {
        var seen = new HashSet<string>();
        foreach (var a in Document.Activities)
        {
            if (!seen.Add(a.Id) || a.License.Length == 0 || a.Commit.Length != 40 || !Uri.TryCreate(a.Provenance, UriKind.Absolute, out _)) throw new FormatException("Catalog provenance or duplicate activity.");
            if (a.Type is not ("flashpoint" or "operation" or "lair" or "world_boss" or "training" or "other")) throw new FormatException("Catalog content type.");
            var encounters = new HashSet<string>();
            foreach (var e in a.Encounters)
            {
                if (!encounters.Add(e.Id) || e.Final && e.Optional) throw new FormatException("Conflicting encounter.");
                if (e.Rule is not ("death_all" or "death_any" or "scripted" or "hp_zero_exit" or "hp_threshold_exit" or "signal_exit" or "unsupported")) throw new FormatException("Unknown rule.");
                if (e.Status is "terminal-tested" or "full-run-tested" && (e.PositiveFixtures.Length == 0 || e.NegativeFixtures.Length == 0)) throw new FormatException("Promoted rule requires fixtures.");
                if (e.Rule == "scripted" && e.VictoryId is null) throw new FormatException("Scripted rule requires a stable identifier.");
                if (e.Rule == "hp_threshold_exit" && (e.Ending is not { } ending || ending.HealthPercent is < 1 or > 20
                    || ending.WindowSeconds is < 1 or > 120 || !e.Roles.Any(r => r.Id == ending.RoleId && r.Required)
                    || !ending.RequireImmune && ending.AbilityIds is not { Length: > 0 }))
                    throw new FormatException("Threshold ending requires a bounded, required target.");
                foreach (var ability in e.Ending?.AbilityIds ?? []) _ = new GameId(ability);
                if (e.Rule == "signal_exit" && e.VictorySignals is not { Length: > 0 }) throw new FormatException("Missing victory signals.");
                foreach (var signal in e.VictorySignals ?? [])
                {
                    _ = new GameId(signal.Id);
                    if (signal.Kind is not ("ability" or "damage" or "loot" or "remove")) throw new FormatException("Unknown victory signal.");
                    if (signal.RoleId is not null && !e.Roles.Any(r => r.Id == signal.RoleId)) throw new FormatException("Unknown signal role.");
                }
                foreach (var id in e.ContinueAfterRoles ?? []) if (!e.Roles.Any(r => r.Id == id && r.Required)) throw new FormatException("Unknown phase target.");
                var slots = new HashSet<string>(StringComparer.Ordinal);
                foreach (var role in e.Roles)
                {
                    if (string.IsNullOrWhiteSpace(role.Id) || role.Count is < 1 or > 32 || role.TemplateIds.Length == 0)
                        throw new FormatException("Invalid encounter role.");
                    foreach (var slot in role.SlotIds()) if (!slots.Add(slot)) throw new FormatException("Duplicate encounter role slot.");
                    foreach (var id in role.TemplateIds) _ = new GameId(id);
                    foreach (var difficulty in role.NotRequiredDifficulties ?? []) _ = new GameId(difficulty);
                    foreach (var (size, count) in role.CountsByGroupSize ?? [])
                        if (size is not (8 or 16) || count < 1 || count > role.Count) throw new FormatException("Invalid group target count.");
                }
            }
            foreach (var group in a.RequiredGroups)
                if (group.Length == 0 || group.Distinct().Count() != group.Length || group.Any(id => !encounters.Contains(id) || a.Encounters.Single(e => e.Id == id).Optional))
                    throw new FormatException("Missing, duplicate or optional required encounter.");
            if (a.CompleteObjectives && (a.RequiredGroups.Length == 0 || !a.Encounters.Any(e => e.Final && a.RequiredGroups.Any(g => g.Contains(e.Id)))))
                throw new FormatException("A complete roster needs its terminal encounter.");
            foreach (var area in a.AreaIds) _ = new GameId(area);
        }
    }
}
