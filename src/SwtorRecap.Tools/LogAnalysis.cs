using System.Globalization;
using Microsoft.Data.Sqlite;
using SwtorRecap.Core;
using SwtorRecap.Engine;
using SwtorRecap.Storage;

namespace SwtorRecap.Tools;

public sealed record AnalysisInput(string Id, string Path, string Sha256, string? DateAnchor);
public sealed record AnalysisManifest(AnalysisInput[] Files, string SourceTimezone = "UTC");

/// <summary>Read-only inputs, isolated projections, no expected-result fixture required.</summary>
internal static class LogAnalysis
{
    public static void Run(string manifestPath, string dataDirectory, string output, Catalog catalog)
    {
        var manifest = Json.Read<AnalysisManifest>(File.ReadAllText(manifestPath));
        _ = TimeZoneInfo.FindSystemTimeZoneById(manifest.SourceTimezone);
        var root = DataPaths.Isolated(dataDirectory);
        // Never attach an existing history, even when it happens to contain no sources.
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new ArgumentException("Analysis requires a new empty data directory.");
        if (File.Exists(output)) throw new ArgumentException("Analysis output already exists.");
        if (manifest.Files.Length == 0 || manifest.Files.Select(f => f.Id).Distinct().Count() != manifest.Files.Length)
            throw new ArgumentException("Analysis requires nonempty, uniquely identified inputs.");
        if (manifest.Files.Any(f => !System.Text.RegularExpressions.Regex.IsMatch(f.Id, @"^F[0-9]{3,}$")))
            throw new ArgumentException("Analysis IDs must be F followed by at least three digits.");
        foreach (var input in manifest.Files)
        {
            if (Path.GetFullPath(input.Path) == Path.GetFullPath(output)) throw new ArgumentException("Output cannot replace an input.");
            if (ByteFramer.Digest(input.Path) != input.Sha256) throw new IOException("Input changed: " + input.Id);
        }
        using var guard = new WriterGuard(root);
        var store = new RecapStore(root, manifest.SourceTimezone);
        var engine = new IngestionEngine(store, catalog);
        var results = new List<object>();
        foreach (var input in manifest.Files)
        {
            var container = store.CreateContainer("import");
            var date = input.DateAnchor is null ? (DateOnly?)null : DateOnly.ParseExact(input.DateAnchor, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var source = engine.Register(container, Path.GetFullPath(input.Path), true, date, manifest.SourceTimezone);
            engine.ReadAll(source); engine.FinalizeSource(source);
            var totals = store.Totals(new(ContainerId: container), catalog);
            engine.ReadAll(source); // A repeated file notification must be a no-op.
            if (Json.Write(totals) != Json.Write(store.Totals(new(ContainerId: container), catalog)))
                throw new InvalidDataException("Duplicate read changed totals: " + input.Id);
            if (ByteFramer.Digest(input.Path) != input.Sha256) throw new IOException("Input changed: " + input.Id);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db.Open();
            SqliteCommand Query(string sql)
            {
                var cmd = db.CreateCommand(); cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$source", source); return cmd;
            }
            var outcomes = new List<object>();
            // Aggregate directly; History's UI pagination must not truncate diagnostic counts.
            using (var cmd = Query("SELECT activity_id,encounter_id,result,qualified,count(*) FROM encounter_attempts WHERE source_id=$source GROUP BY activity_id,encounter_id,result,qualified ORDER BY 1,2,3,4"))
            using (var r = cmd.ExecuteReader()) while (r.Read())
                outcomes.Add(new { activityId = r.GetString(0), encounterId = r.GetString(1), result = r.GetString(2), qualified = r.GetBoolean(3), count = r.GetInt64(4) });
            var kills = new List<object>();
            using (var cmd = Query("SELECT start_byte,json_extract(payload,'$.templateId'),json_extract(payload,'$.spawnId') FROM encounter_evidence WHERE source_id=$source AND kind='npc_kill' ORDER BY start_byte"))
            using (var r = cmd.ExecuteReader()) while (r.Read())
                kills.Add(new { startByte = r.GetInt64(0), templateId = r.GetString(1), spawnId = r.GetString(2) });
            var gaps = new List<object>();
            using (var cmd = Query("SELECT dimension,reason,count(*),min(start_byte) FROM coverage_gaps WHERE source_id=$source GROUP BY dimension,reason ORDER BY 1,2"))
            using (var r = cmd.ExecuteReader()) while (r.Read())
                gaps.Add(new { dimension = r.GetString(0), reason = r.GetString(1), count = r.GetInt64(2), firstByte = r.GetInt64(3) });
            using var coverage = Query("SELECT accepted,rejected,unknown_count,next_offset,last_length,integrity FROM source_generations WHERE id=$source");
            using var row = coverage.ExecuteReader(); row.Read();
            results.Add(new { input.Id, totals, accepted = row.GetInt64(0), rejected = row.GetInt64(1), unknown = row.GetInt64(2),
                offset = row.GetInt64(3), length = row.GetInt64(4), integrity = row.GetString(5), outcomes, kills, gaps });
        }
        store.Verify();
        var report = new { version = 1, app = Versions.App, parser = Versions.Parser, metrics = Versions.Metrics, catalog = Versions.Catalog,
            databaseVerified = true, totals = store.Totals(new(), catalog), files = results };
        File.WriteAllText(output, Json.Write(report) + "\n");
        Console.WriteLine(Json.Write(new { ok = true, files = results.Count }));
    }
}
