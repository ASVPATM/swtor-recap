using System.Globalization;
using System.Text.RegularExpressions;

namespace SwtorRecap.Core;

public sealed class LogClockState
{
    public DateOnly? Date { get; set; }
    public TimeOnly? Previous { get; set; }
    public int Epoch { get; set; }
    public string SourceZone { get; set; } = "UTC";
    public bool ClockUncertain { get; set; }
    public ResolvedTime Resolve(TimeOnly time, string reportingZone)
    {
        if (Previous is { } prior && time < prior)
        {
            if (prior.Hour >= 23 && time.Hour <= 1 && prior.ToTimeSpan() - time.ToTimeSpan() > TimeSpan.FromHours(20)) Date = Date?.AddDays(1);
            else { Epoch++; ClockUncertain = true; }
        }
        Previous = time;
        if (Date is null) return new(null, null, null, "undated", Epoch);
        var local = Date.Value.ToDateTime(time, DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(SourceZone);
        var report = TimeZoneInfo.FindSystemTimeZoneById(reportingZone);
        if (zone.IsInvalidTime(local)) return new(local, null, null, "invalid_local_time", Epoch);
        if (zone.IsAmbiguousTime(local))
        {
            var dates = zone.GetAmbiguousTimeOffsets(local).Select(offset => TimeZoneInfo.ConvertTime(new DateTimeOffset(local, offset), report).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Distinct().ToArray();
            return new(local, null, dates.Length == 1 ? dates[0] : null, "ambiguous", Epoch);
        }
        var utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
        return new(local, utc, TimeZoneInfo.ConvertTime(utc, report).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ClockUncertain ? "clock_adjusted" : "anchored", Epoch);
    }

    public static DateOnly? DateFromFilename(string path)
    {
        var match = Regex.Match(Path.GetFileName(path), @"^combat_(\d{4}-\d{2}-\d{2})_\d{2}_\d{2}_\d{2}_\d+\.txt$", RegexOptions.CultureInvariant);
        return match.Success && DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }
}

public static class CalendarAllocation
{
    public static IEnumerable<(DateTimeOffset Start, DateTimeOffset End, string Date, long Milliseconds)> Split(DateTimeOffset start, DateTimeOffset end, string zoneId)
    {
        if (end < start) throw new ArgumentOutOfRangeException(nameof(end));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        while (start < end)
        {
            var local = TimeZoneInfo.ConvertTime(start, zone);
            var midnight = DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified);
            while (zone.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(1);
            var next = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, zone), TimeSpan.Zero);
            if (next <= start) throw new InvalidOperationException("Non-increasing calendar boundary.");
            if (next > end) next = end;
            yield return (start, next, local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), checked((long)(next - start).TotalMilliseconds));
            start = next;
        }
    }
}
