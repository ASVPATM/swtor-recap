namespace SwtorRecap.Core;

public sealed record EntityRole(string Id, string Name, string[] TemplateIds, bool Required);
public sealed record EncounterDefinition(string Id, string Name, EntityRole[] Roles, string Rule, string? VictoryId,
    bool Final, bool Optional, string Status, string[] PositiveFixtures, string[] NegativeFixtures);
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
        foreach (var id in role.TemplateIds)
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
    // Candidate bosses are still bosses. Do not classify them as ordinary kills
    // just because their victory rule or current area is not yet supported.
    public bool IsBossOrTraining(string? template) => template is not null && _templates.TryGetValue(template, out var entries)
        && entries.Any(e => e.Role.Required || e.Activity.Type == "training");
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
                if (e.Rule is not ("death_all" or "death_any" or "scripted" or "hp_zero_exit" or "unsupported")) throw new FormatException("Unknown rule.");
                if (e.Status is "terminal-tested" or "full-run-tested" && (e.PositiveFixtures.Length == 0 || e.NegativeFixtures.Length == 0)) throw new FormatException("Promoted rule requires fixtures.");
                if (e.Rule == "scripted" && e.VictoryId is null) throw new FormatException("Scripted rule requires a stable identifier.");
                foreach (var role in e.Roles) foreach (var id in role.TemplateIds) _ = new GameId(id);
            }
            foreach (var group in a.RequiredGroups) if (group.Length == 0 || group.Any(id => !encounters.Contains(id))) throw new FormatException("Missing required encounter.");
            foreach (var area in a.AreaIds) _ = new GameId(area);
        }
    }
}
