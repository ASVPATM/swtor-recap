using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwtorRecap.Core;

public readonly record struct GameId
{
    public string Value { get; }
    public GameId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 40 || value.Any(c => c is < '0' or > '9'))
            throw new FormatException("Game identifiers must be unsigned decimal text.");
        Value = value.TrimStart('0');
        if (Value.Length == 0) Value = "0";
    }
    public override string ToString() => Value;
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 48, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new FormatException("Missing JSON value.");
    public static T Clone<T>(T value) => Read<T>(Write(value));
}

public static class Versions
{
    public const int Schema = 1, Reducer = 1, Protocol = 1;
    public const string Parser = "1.1.0", Metrics = "1.2.0", Catalog = "2026.11.09.2";
    public static string App => typeof(Versions).Assembly.GetName().Version!.ToString(3);
}

public static class EffectIds
{
    public const string Event = "836045448945472", Apply = "836045448945477", Remove = "836045448945478",
        Enter = "836045448945489", Exit = "836045448945490", Death = "836045448945493", Revive = "836045448945494",
        Heal = "836045448945500", Damage = "836045448945501", Area = "836045448953664", Discipline = "836045448953665";
}

public static class MetricDefinitions
{
    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        ["damage"] = "Raw logged damage from the resolved local player, excluding self-damage and companions.",
        ["healing"] = "Your raw logged healing, including self-heals, periodic effects and passives attributed to you. Includes overhealing; excludes healing from companions and other players. Absorption is not reconstructed.",
        ["effective"] = "Effective amounts from supported records. Missing effective values remain unknown.",
        ["gameTime"] = "Observed awake time while SWTOR was running; includes AFK time, excludes sleep and recording pauses.",
        ["combatTime"] = "Union of supported local-owner combat intervals. Interrupted boundaries remain partial.",
        ["bosses"] = "Participated, supported encounter occurrences won, not individual NPC deaths or final-blow credit.",
        ["deaths"] = "Recorded deaths of your identified character.",
        ["kills"] = "NPC deaths after your character dealt more than 50% of the target's logged maximum health in effective damage. Includes individual boss targets, separately from encounter wins. Excludes companions, players and training dummies. Requires known health and a recorded death; you may be dead and another player may land the final blow.",
        ["clears"] = "Supported final encounter wins. Does not establish mission turn-in, rewards, or a full run.",
        ["fullRuns"] = "All required encounter/objective groups observed with continuity under a complete catalog definition.",
        ["sessions"] = "Distinct observed SWTOR process instances. Imported captures are counted separately."
    };
}

public enum ActorKind { None, Player, Npc, Companion }
public sealed record Actor(ActorKind Kind, string Key, string? Name = null, string? PlayerId = null,
    string? TemplateId = null, string? SpawnId = null, string? CompanionOwnerId = null, long? Hp = null, long? MaxHp = null)
{
    public static readonly Actor Empty = new(ActorKind.None, "");
}
public enum EventKind { Other, Damage, Heal, EnterCombat, ExitCombat, Death, Revived, AreaEntered, DisciplineChanged }
public enum ParseStatus { Accepted, Irrelevant, UnknownSemantic, Malformed, Unsupported }
public sealed record LogEvent(TimeOnly Time, Actor Source, Actor Target, EventKind Kind,
    string TypeId, string? EffectId, string? AbilityId, long? Raw = null, long? Effective = null, bool Critical = false,
    string? AreaId = null, string? DifficultyId = null, int? GroupSize = null, string? Server = null, string? Format = null);
public sealed record ParseResult(ParseStatus Status, LogEvent? Event, string? Reason = null);
public sealed record SourceRecord(long Start, long End, string? Text, string? Rejection = null);
public sealed record ResolvedTime(DateTime? Local, DateTimeOffset? Utc, string? ReportingDate, string Quality, int Epoch);
public sealed record Gap(string SourceId, long Start, long End, string Dimension, string Reason, bool Recoverable = true);
public sealed record Fact(string Id, string SourceId, long Start, long End, string Kind, string Entity,
    ResolvedTime Time, string Payload);

public sealed class Amounts
{
    public long Damage { get; set; }
    public long EffectiveDamage { get; set; }
    public long DamageUnknown { get; set; }
    public long Healing { get; set; }
    public long EffectiveHealing { get; set; }
    public long HealingUnknown { get; set; }
    public long Events { get; set; }
    public void Add(Amounts other)
    {
        checked
        {
            Damage += other.Damage; EffectiveDamage += other.EffectiveDamage; DamageUnknown += other.DamageUnknown;
            Healing += other.Healing; EffectiveHealing += other.EffectiveHealing; HealingUnknown += other.HealingUnknown; Events += other.Events;
        }
    }
    public void Add(LogEvent e)
    {
        var a = new Amounts { Events = 1 };
        if (e.Kind == EventKind.Damage) { a.Damage = e.Raw!.Value; a.EffectiveDamage = e.Effective ?? 0; a.DamageUnknown = e.Effective is null ? 1 : 0; }
        else { a.Healing = e.Raw!.Value; a.EffectiveHealing = e.Effective ?? 0; a.HealingUnknown = e.Effective is null ? 1 : 0; }
        Add(a);
    }
}

public sealed record Segment(string Id, string SourceId, long Start, long? End, string? Owner,
    string? CharacterId, string? Name, string Attribution, string? Server);
public sealed record Bucket(string Key, string SegmentId, string ActorKey, string Context, string LocalMinute,
    long? UtcMinute, string? Date, string Quality, int ClockEpoch, Amounts Amounts);
public sealed record RecordedInterval(string Id, string ContainerId, string? SegmentId, string ActorKey,
    string Kind, DateTimeOffset? Start, DateTimeOffset? End, long Milliseconds, string? Date, string Quality);

public static class Keys
{
    public static string Stable(params object?[] parts) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(string.Join('\u001f', parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture) ?? ""))))).ToLowerInvariant();
}
