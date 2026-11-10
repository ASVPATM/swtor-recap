namespace SwtorRecap.Core;

public sealed record Revision(string HistoryEpoch, long DataRevision, long SettingsRevision);
public sealed record TrackerSettings(string? LogFolder, bool Paused, bool StartWithWindows, bool SessionOnce, string ReportingZone);
public sealed record QueryFilter(int? Year = null, int? Month = null, string? CharacterId = null, string? ContainerId = null, int Offset = 0, int Limit = 50);
public sealed record Totals(Amounts? Amounts, long GameMilliseconds, long CombatMilliseconds, long Bosses, long Flashpoints,
    long Operations, long Lairs, long FullRuns, long Deaths, long Sessions, long Imports, long Kills = 0, bool KillsNeedReprocess = false);
public sealed record CharacterSummary(string Id, string Name, long CombatMilliseconds, long Damage, long Healing);
public sealed record HistoryItem(string Id, string Kind, string State, string? StartedUtc, string? EndedUtc, string[] Characters, Totals Totals, int GapCount);
public sealed record ActivitySummary(string Name, string Type, long Clears, string? FirstRecordedDate);
public sealed record PeriodPoint(string Date, long Damage, long Healing, long CombatMilliseconds, long GameMilliseconds);
public sealed record OutcomeSummary(string Id, string Name, string Activity, string Result, string Basis, bool Qualified, bool Terminal, bool Full, string? Date, string[] Evidence);
public sealed record OwnerCandidate(string Id, string Name);
public sealed record SourceSummary(string Id, string Filename, long Offset, long Length, int Accepted, int Rejected, string Integrity, string? Owner, string? DateAnchor, OwnerCandidate[]? Candidates = null);
public sealed record JobSummary(string Id, string Kind, string State, long ProcessedBytes, long TotalBytes, string? Message);
public sealed record TrackerSnapshot(Revision Revision, string State, string Message, TrackerSettings Settings, HistoryItem? Current,
    string SqliteVersion, string ParserVersion, string CatalogVersion, bool StartupVerified, JobSummary[] Jobs,
    string? ResolvedLogFolder = null, string? DataDirectory = null);
public sealed record HistoryPage(Revision Revision, HistoryItem[] Items, bool HasMore, CharacterSummary[] Characters);
public sealed record NpcKillSummary(string Name, string TemplateId, long Count);
public sealed record HistoryDetail(Revision Revision, HistoryItem Item, OutcomeSummary[] Outcomes, SourceSummary[] Sources, Gap[] Gaps, NpcKillSummary[]? Kills = null);
public sealed record RecapSnapshot(Revision Revision, int Year, int? Month, string ReportingZone, Totals Totals,
    PeriodPoint[] Points, CharacterSummary[] Characters, ActivitySummary[] Activities, string[] Coverage, bool PeriodOpen, HistoryItem? LongestSession, string? CharacterFilter = null);
public sealed record MutationResult(Revision Revision, string Message, string? JobId = null, string? Path = null);
public sealed record ImportRequest(string[] Paths, string SourceZone, string? DateAnchor = null, string? OwnerId = null, bool ConfirmIndependent = false);
public sealed record SettingsUpdate(long ExpectedRevision, string? LogFolder, bool StartWithWindows, bool SessionOnce);
public sealed record IpcRequest(int ProtocolVersion, string RequestId, string Command, System.Text.Json.JsonElement Parameters, string? ExpectedEpoch = null);
public sealed record IpcResponse(int ProtocolVersion, string RequestId, bool Success, string? Error, Revision Revision, System.Text.Json.JsonElement Payload);

public interface ITrackerClient : IAsyncDisposable
{
    Task<T> RequestAsync<T>(string command, object? parameters = null, CancellationToken cancellationToken = default);
}
public interface ITrackerReconnect
{
    void PermitAgentLaunch();
}
