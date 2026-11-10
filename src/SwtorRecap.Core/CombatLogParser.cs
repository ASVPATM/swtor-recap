using System.Globalization;
using System.Text.RegularExpressions;

namespace SwtorRecap.Core;

/// <summary>Independent structural parser; labels never determine gameplay semantics.</summary>
public sealed partial class CombatLogParser
{
    [GeneratedRegex(@"\{([0-9]+)\}", RegexOptions.CultureInvariant)] private static partial Regex Ids();
    [GeneratedRegex(@"^@(?<name>[^#|/]+)#(?<id>[0-9]+)(?:/(?<comp>.+))?$", RegexOptions.CultureInvariant)] private static partial Regex Player();
    [GeneratedRegex(@"^(?<name>.*?)\s*\{(?<template>[0-9]+)\}:(?<spawn>[0-9]+)$", RegexOptions.CultureInvariant)] private static partial Regex Npc();
    [GeneratedRegex(@"\|\((?<hp>[0-9]+)/(?<max>[0-9]+)\)$", RegexOptions.CultureInvariant)] private static partial Regex Health();
    [GeneratedRegex(@"^\((?<raw>[0-9]+)(?<crit>\*)?(?:\s+~(?<effective>[0-9]+))?(?=[\s)])", RegexOptions.CultureInvariant)] private static partial Regex Amount();
    [GeneratedRegex(@"\}\s+(8|16)\s+", RegexOptions.CultureInvariant)] private static partial Regex Size();

    public ParseResult Parse(ReadOnlySpan<char> line, bool modernEffective = true)
    {
        try
        {
            Span<Range> fields = stackalloc Range[5];
            var position = 0;
            for (var i = 0; i < 5; i++)
            {
                while (position < line.Length && char.IsWhiteSpace(line[position])) position++;
                if (position == line.Length || line[position++] != '[') return Bad("envelope");
                var start = position;
                while (position < line.Length && line[position] != ']') position++;
                if (position == line.Length) return Bad("envelope");
                fields[i] = start..position++;
            }
            if (!TimeOnly.TryParseExact(line[fields[0]], ["HH:mm:ss.fff", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return Bad("timestamp");
            var source = ParseActor(line[fields[1]].ToString(), Actor.Empty);
            var target = ParseActor(line[fields[2]].ToString(), source);
            var semantic = line[fields[4]].ToString();
            var ids = Ids().Matches(semantic).Select(m => new GameId(m.Groups[1].Value).Value).ToArray();
            if (ids.Length == 0) return Bad("semantic_identifier");
            var type = ids[0]; var effect = ids.Length > 1 ? ids[1] : null;
            var ability = Ids().Match(line[fields[3]].ToString());
            var abilityId = ability.Success ? new GameId(ability.Groups[1].Value).Value : null;
            var kind = (type, effect) switch
            {
                (EffectIds.Apply, EffectIds.Damage) => EventKind.Damage,
                (EffectIds.Apply, EffectIds.Heal) => EventKind.Heal,
                (EffectIds.Event, EffectIds.Enter) => EventKind.EnterCombat,
                (EffectIds.Event, EffectIds.Exit) => EventKind.ExitCombat,
                (EffectIds.Event, EffectIds.Death) => EventKind.Death,
                (EffectIds.Event, EffectIds.Revive) => EventKind.Revived,
                (EffectIds.Area, _) => EventKind.AreaEntered,
                (EffectIds.Discipline, _) => EventKind.DisciplineChanged,
                _ => EventKind.Other
            };
            var tail = line[position..].Trim().ToString();
            if (!BalancedDetails(tail)) return Bad("details_delimiters");
            long? raw = null, effective = null; var critical = false;
            if (kind is EventKind.Damage or EventKind.Heal)
            {
                var match = Amount().Match(tail);
                if (!match.Success || !long.TryParse(match.Groups["raw"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return Bad("amount");
                raw = value; critical = match.Groups["crit"].Success;
                if (match.Groups["effective"].Success)
                {
                    if (!long.TryParse(match.Groups["effective"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out value)) return Bad("effective_amount");
                    effective = value;
                }
                else if (tail.Contains('~')) return Bad("effective_amount");
                else if (modernEffective) effective = raw;
                // The licensed contemporary fixture contains a one-point effective/raw rounding
                // difference. Preserve the two independently logged nonnegative amounts.
            }
            string? server = null, format = null;
            if (kind == EventKind.AreaEntered)
            {
                if (effect is null || source.Kind != ActorKind.Player) return Bad("area_owner");
                var close = tail.IndexOf(')');
                if (tail.StartsWith('(') && close > 0) server = tail[1..close];
                var open = tail.IndexOf('<');
                if (open >= 0 && tail.EndsWith('>')) format = tail[(open + 1)..^1];
                if (format is not null && format != "v7.0.0b") return new(ParseStatus.Unsupported, null, "log_format:" + format);
            }
            var size = Size().Match(semantic);
            var e = new LogEvent(time, source, target, kind, type, effect, abilityId, raw, effective, critical,
                kind == EventKind.AreaEntered ? effect : null, kind == EventKind.AreaEntered && ids.Length > 2 ? ids[2] : null,
                size.Success ? int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture) : null, server, format);
            return new(kind != EventKind.Other ? ParseStatus.Accepted : type is EffectIds.Apply or EffectIds.Remove or EffectIds.Event ? ParseStatus.Irrelevant : ParseStatus.UnknownSemantic, e);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException) { return Bad("actor_or_identifier"); }
    }

    private static bool BalancedDetails(string value)
    {
        var depth = 0;
        foreach (var c in value)
        {
            if (c == '(') depth++;
            if (c == ')' && --depth < 0) return false;
        }
        return depth == 0;
    }
    private static ParseResult Bad(string reason) => new(ParseStatus.Malformed, null, reason);
    public static Actor ParseActor(string value, Actor source)
    {
        if (value.Length == 0) return Actor.Empty;
        if (value == "=") return source;
        var hpMatch = Health().Match(value);
        long? hp = hpMatch.Success ? long.Parse(hpMatch.Groups["hp"].Value, CultureInfo.InvariantCulture) : null;
        long? maxHp = hpMatch.Success ? long.Parse(hpMatch.Groups["max"].Value, CultureInfo.InvariantCulture) : null;
        var identity = value.Split('|', 2)[0].Trim();
        var player = Player().Match(identity);
        string? owner = null;
        if (player.Success)
        {
            var id = new GameId(player.Groups["id"].Value).Value;
            if (!player.Groups["comp"].Success) return new(ActorKind.Player, "p:" + id, player.Groups["name"].Value, id, Hp: hp, MaxHp: maxHp);
            owner = id; identity = player.Groups["comp"].Value;
        }
        var npc = Npc().Match(identity);
        if (!npc.Success) throw new FormatException("Unsupported actor grammar.");
        var template = new GameId(npc.Groups["template"].Value).Value;
        var spawn = new GameId(npc.Groups["spawn"].Value).Value;
        return new(owner is null ? ActorKind.Npc : ActorKind.Companion, "n:" + template + ":" + spawn,
            npc.Groups["name"].Value.Trim(), TemplateId: template, SpawnId: spawn, CompanionOwnerId: owner, Hp: hp, MaxHp: maxHp);
    }
}
