using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SwtorRecap.Core;
using SwtorRecap.Engine;
using SwtorRecap.Storage;

namespace SwtorRecap.Tools;

public sealed record FixtureExpected(long Damage,long EffectiveDamage,long Healing,long EffectiveHealing,long Bosses,long Flashpoints,long Operations,long FullRuns,long Rejected=0,long? Kills=null);
public sealed record FixtureEntry(string FixtureId,string Path,string Kind,string Sha256,long ByteLength,string EncodingProfile,
    string? FilenameDateAnchor,string SourceTimezone,string KnownOwnerIdentity,string OwnerBasis,string SourceUrl,string Commit,string License,string VerificationNotes,FixtureExpected Expected);
public sealed record FixtureManifest(int Version,FixtureEntry[] Fixtures);
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            string? Option(string name){var i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:null;}
            var catalog=Catalog.LoadDefault();
            if(args.Length>=2&&args[0]=="catalog"&&args[1]=="validate")
            {
                catalog.Validate();var report=new{catalog.Document.Version,activities=catalog.Document.Activities.Length,coverage=catalog.Document.Activities.Select(a=>new{activity=a.Id,a.Type,a.CompleteObjectives,encounters=a.Encounters.Select(e=>new{e.Id,e.Status,e.Rule,e.Final,positive=e.PositiveFixtures,negative=e.NegativeFixtures,recognizedIds=e.Roles.SelectMany(r=>r.TemplateIds),reason=e.Status=="candidate"?"Identifiers only; terminal evidence not validated.":e.Status=="logic-tested"?"Synthetic invariant coverage; real encounter validation pending.":"Fixture-backed scoped rule."})})};
                var json=JsonSerializer.Serialize(report,new JsonSerializerOptions(Json.Options){WriteIndented=true});Console.WriteLine(json);if(Option("--output") is { } output)File.WriteAllText(output,json+"\n");return 0;
            }
            var root=DataPaths.Isolated(Option("--data-dir"));using var guard=new WriterGuard(root);var store=new RecapStore(root,"UTC");
            if(args.Length>=2&&args[0]=="database"&&args[1]=="verify"){store.Verify();Console.WriteLine(Json.Write(new{ok=true,store.SqliteVersion,store.SqliteSourceId,revision=store.Revision}));return 0;}
            if(args.FirstOrDefault()=="seed-preview"){PreviewSeeder.Seed(store,catalog);Console.WriteLine(Json.Write(store.Recap(new(Year:2026),catalog)));return 0;}
            if(args.FirstOrDefault()=="fault-child")
            {
                var phase=Option("--phase")??"before_commit";var engine=new IngestionEngine(store,catalog);var file=Option("--file")??throw new ArgumentException("--file is required.");
                var id=store.SourceIds().FirstOrDefault()??engine.Register(store.CreateContainer("import"),Path.GetFullPath(file),true,new DateOnly(2026,1,1),"UTC");
                store.Failpoint=p=>{if(p==phase)Environment.Exit(77);};engine.ReadAll(id);return 0;
            }
            if(args.FirstOrDefault() is not ("replay" or "benchmark"))throw new ArgumentException("Commands: replay --manifest PATH --data-dir DIR [--speed max|1] [--seed N] [--verify]; catalog validate; database verify --data-dir DIR; seed-preview --data-dir DIR; benchmark --manifest PATH --data-dir DIR.");
            if(store.SourceIds().Length>0)throw new ArgumentException("Replay requires an empty isolated data root. Use a new directory to preserve previous evidence.");
            var manifestPath=Path.GetFullPath(Option("--manifest")??throw new ArgumentException("--manifest is required."));var manifest=Json.Read<FixtureManifest>(File.ReadAllText(manifestPath));
            var random=new Random(int.Parse(Option("--seed")??"7741",CultureInfo.InvariantCulture));var speed=Option("--speed")??"max";
            if(speed is not ("max" or "1"))throw new ArgumentException("Replay speed must be max or 1.");
            var process=Process.GetCurrentProcess();var cpuStart=process.TotalProcessorTime;var allocated=GC.GetTotalAllocatedBytes();var watch=Stopwatch.StartNew();var results=new List<object>();var failures=0;long totalBytes=0;
            var engineReplay=new IngestionEngine(store,catalog);
            foreach(var fixture in manifest.Fixtures)
            {
                var original=Path.Combine(Path.GetDirectoryName(manifestPath)!,fixture.Path);
                if(ByteFramer.Digest(original)!=fixture.Sha256||new FileInfo(original).Length!=fixture.ByteLength)throw new InvalidDataException("Fixture checksum/length mismatch: "+fixture.FixtureId);
                var folder=Path.Combine(root,"fake-logs");Directory.CreateDirectory(folder);var path=Path.Combine(folder,fixture.FixtureId+".txt");
                await File.WriteAllBytesAsync(path,[]);
                var container=store.CreateContainer("import");var id=engineReplay.Register(container,path,false,fixture.FilenameDateAnchor is null?null:DateOnly.Parse(fixture.FilenameDateAnchor,CultureInfo.InvariantCulture),fixture.SourceTimezone);
                using(var input=ByteFramer.OpenRead(original))
                using(var output=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete))
                {
                    var buffer=new byte[65536];var timeline=new ReplayTimeline();var pace=Stopwatch.StartNew();var parser=new CombatLogParser();long offset=0;
                    while(offset<input.Length)
                    {
                        var end=Math.Min(input.Length,offset+random.Next(256,65536));
                        if(speed=="1")
                        {
                            var record=ByteFramer.Read(input,offset,fixture.EncodingProfile,true,maxRecords:1).Records.First();end=record.End;
                            if(parser.Parse(record.Text.AsSpan()).Event is { } parsed)
                            {
                                var wait=timeline.At(parsed.Time)-pace.Elapsed;
                                if(wait>TimeSpan.Zero)await Task.Delay(wait);
                            }
                        }
                        input.Position=offset;
                        while(offset<end)
                        {
                            var n=await input.ReadAsync(buffer.AsMemory(0,(int)Math.Min(end-offset,random.Next(1,65536))));
                            if(n==0)throw new IOException("Replay fixture ended unexpectedly.");offset+=n;
                            await output.WriteAsync(buffer.AsMemory(0,n));await output.FlushAsync();
                            if(random.Next(3)!=0)engineReplay.ReadOnce(id,maxRecords:random.Next(1,2001),chunkSize:random.Next(1,65537));
                            if(random.Next(4)==0)engineReplay.ReadOnce(id); // duplicate notification
                        }
                    }
                }
                engineReplay.ReadAll(id);var totals=store.Totals(new(ContainerId:container),catalog);var detail=store.Detail(container,catalog);var expected=fixture.Expected;var actual=totals.Amounts;
                var discrepancies=new List<string>();
                void Check(string metric,long expectedValue,long? actualValue){if(expectedValue!=actualValue)discrepancies.Add($"{metric}: expected {expectedValue}, actual {actualValue?.ToString()??"unavailable"}");}
                Check("damage",expected.Damage,actual?.Damage);Check("effectiveDamage",expected.EffectiveDamage,actual?.EffectiveDamage);Check("healing",expected.Healing,actual?.Healing);Check("effectiveHealing",expected.EffectiveHealing,actual?.EffectiveHealing);
                Check("bosses",expected.Bosses,totals.Bosses);Check("flashpoints",expected.Flashpoints,totals.Flashpoints);Check("operations",expected.Operations,totals.Operations);Check("fullRuns",expected.FullRuns,totals.FullRuns);Check("rejected",expected.Rejected,detail.Sources.Sum(s=>(long)s.Rejected));
                if(expected.Kills is { } kills)Check("kills",kills,totals.Kills);
                failures+=discrepancies.Count;totalBytes+=fixture.ByteLength;
                results.Add(new{fixture.FixtureId,totals,coverage=detail.Sources,outcomes=detail.Outcomes,kills=detail.Kills,discrepancies});
            }
            store.Verify();watch.Stop();process.Refresh();var cpu=(process.TotalProcessorTime-cpuStart).TotalSeconds;
            var queries=new List<double>();for(var i=0;i<30;i++){var q=Stopwatch.StartNew();store.Recap(new(Year:2026),catalog);queries.Add(q.Elapsed.TotalMilliseconds);}queries.Sort();
            var reportResult=new{ok=failures==0,fixtureVersion=manifest.Version,seed=Option("--seed")??"7741",store.SqliteVersion,store.SqliteSourceId,os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,runtime=Environment.Version.ToString(),architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),elapsedSeconds=watch.Elapsed.TotalSeconds,totalBytes,bytesPerSecond=totalBytes/watch.Elapsed.TotalSeconds,oneCoreCpuPercent=100*cpu/watch.Elapsed.TotalSeconds,machineCpuPercent=100*cpu/watch.Elapsed.TotalSeconds/Environment.ProcessorCount,privateBytes=OperatingSystem.IsWindows()?(long?)process.PrivateMemorySize64:null,workingSet=process.WorkingSet64,allocatedBytes=GC.GetTotalAllocatedBytes()-allocated,recapP95Milliseconds=queries[(int)(queries.Count*.95)],results};
            var reportJson=JsonSerializer.Serialize(reportResult,new JsonSerializerOptions(Json.Options){WriteIndented=true});Console.WriteLine(reportJson);File.WriteAllText(Path.Combine(root,"replay-result.json"),reportJson+Environment.NewLine);
            return args.Contains("--verify")&&failures!=0?1:0;
        }
        catch(Exception ex){Console.Error.WriteLine(Json.Write(new{ok=false,error=ex.Message}));return 1;}
    }
}
