using SwtorRecap.Core;
using SwtorRecap.Storage;

namespace SwtorRecap.Engine;

public sealed class HistoryMaintenance(RecapStore store,Catalog catalog,SemaphoreSlim? writer=null)
{
    private async Task WriteAsync(Action action,CancellationToken token=default)
    {
        if(writer is not null)await writer.WaitAsync(token);
        try{action();}finally{writer?.Release();}
    }
    public async Task<MutationResult> ImportAsync(string jobId,ImportRequest request,Func<bool>? liveActive=null,CancellationToken cancellationToken=default)
    {
        _=TimeZoneInfo.FindSystemTimeZoneById(request.SourceZone);
        if(request.Paths.Length is <1 or >1000)throw new ArgumentException("Select between 1 and 1,000 files.");
        if(request.DateAnchor is not null&&!DateOnly.TryParseExact(request.DateAnchor,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out _))throw new ArgumentException("Use YYYY-MM-DD for the recording date.");
        if(request.OwnerId is not null)_=new GameId(request.OwnerId);
        var paths=request.Paths.SelectMany(p=>Directory.Exists(p)?Directory.EnumerateFiles(p,"*.txt",SearchOption.TopDirectoryOnly):new[]{p}).Distinct(StringComparer.Ordinal).Take(1001).Order(StringComparer.Ordinal).ToArray();
        if(paths.Length>1000)throw new ArgumentException("Import at most 1,000 files per job.");
        var total=paths.Sum(p=>new FileInfo(p).Length);long processed=0;var imported=0;var duplicates=0;
        var stagingRoot=Path.Combine(store.Root,"staging",jobId);Directory.CreateDirectory(stagingRoot);
        var requestJson=Json.Write(request);
        foreach(var original in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while(liveActive?.Invoke()==true)
            {await WriteAsync(()=>store.PutJob(new(jobId,"import","waiting_for_game",processed,total,"Import resumes when SWTOR closes."),requestJson,store.Revision),cancellationToken);await Task.Delay(1000,cancellationToken);}
            var length=new FileInfo(original).Length;
            var sourceDate=request.DateAnchor is null?LogClockState.DateFromFilename(original):DateOnly.Parse(request.DateAnchor,System.Globalization.CultureInfo.InvariantCulture);
            var fileRoot=Path.Combine(stagingRoot,Keys.Stable(Path.GetFullPath(original)));
            Directory.CreateDirectory(fileRoot);
            var snapshot=Path.Combine(fileRoot,"capture.txt");
            // Stable, explicitly sealed snapshot. Later writes remain for the live path or a later replacement.
            using(var input=ByteFramer.OpenRead(original))
            using(var output=new FileStream(snapshot,FileMode.Create,FileAccess.Write,FileShare.None))
            {
                var buffer=new byte[65536];long remaining=length;
                while(remaining>0){var n=await input.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,remaining)),cancellationToken);if(n==0)throw new IOException("Source shrank during snapshot.");await output.WriteAsync(buffer.AsMemory(0,n),cancellationToken);remaining-=n;}
            }
            var digest=ByteFramer.Digest(snapshot);
            var existing=store.SourceIds().Select(store.LoadSource).ToArray();
            var same=existing.FirstOrDefault(s=>s.Digest==digest&&s.DateAnchor==sourceDate?.ToString("yyyy-MM-dd")&&s.SourceZone==request.SourceZone);
            if(same is not null){await WriteAsync(()=>store.AddAlias(same.Id,Path.GetFullPath(original)),cancellationToken);processed+=length;duplicates++;continue;}
            // Whole-range byte proof against known source copies, qualified by date/zone.
            var related=existing.Where(s=>s.DateAnchor==sourceDate?.ToString("yyyy-MM-dd")&&s.SourceZone==request.SourceZone&&File.Exists(s.Path)).FirstOrDefault(s=>PrefixEqual(snapshot,s.Path));
            if(related is not null&&related.Offset>=length){await WriteAsync(()=>store.AddAlias(related.Id,Path.GetFullPath(original)),cancellationToken);processed+=length;duplicates++;continue;}
            var extension=existing.FirstOrDefault(s=>s.DateAnchor==sourceDate?.ToString("yyyy-MM-dd")&&s.SourceZone==request.SourceZone&&s.Digest is not null&&s.Length<length&&File.Exists(s.Path)&&ByteFramer.Digest(s.Path)==s.Digest&&PrefixEqual(s.Path,snapshot));
            if(extension is not null)
            {
                await WriteAsync(()=>
                {
                    new HistoryMaintenance(store,catalog).Rebuild(replacementSource:extension.Id,replacementPath:snapshot,cancellationToken:cancellationToken);
                    store.AddAlias(extension.Id,Path.GetFullPath(original));
                },cancellationToken);
                processed+=length;imported++;continue;
            }
            var possibleOverlap=existing.Any(s=>s.SourceZone==request.SourceZone&&(s.DateAnchor is null||sourceDate is null||s.DateAnchor==sourceDate.Value.ToString("yyyy-MM-dd")));
            if(possibleOverlap&&!request.ConfirmIndependent)
            {
                await WriteAsync(()=>store.PutJob(new(jobId,"import","overlap_review",processed,total,"Capture dates may overlap. Reimport with its original date, or confirm an independent recording in Settings."),requestJson,store.Revision),cancellationToken);
                return new(store.Revision,"Import held for overlap review; no overlapping amounts were added.",jobId);
            }
            // An interrupted stage is disposable. Production is unchanged until activation.
            var db=Path.Combine(fileRoot,"derived");
            if(Directory.Exists(db))Directory.Delete(db,true);
            var stage=new RecapStore(db,store.Settings.ReportingZone);var container=stage.CreateContainer("import");
            var ingest=new IngestionEngine(stage,catalog);var sourceId=ingest.Register(container,snapshot,true,sourceDate,request.SourceZone,digest:digest,owner:request.OwnerId);
            while(true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(liveActive?.Invoke()==true){await Task.Delay(1000,cancellationToken);continue;}
                if(ingest.ReadOnce(sourceId)==0)break;
                await WriteAsync(()=>store.PutJob(new(jobId,"import","parsing",processed+stage.LoadSource(sourceId).Offset,total,null),requestJson,store.Revision),cancellationToken);
                await Task.Yield();
            }
            ingest.FinalizeSource(sourceId);stage.AddAlias(sourceId,Path.GetFullPath(original));stage.Verify();
            var activated=false;
            while(!activated)
            {
                await WriteAsync(()=>
                {
                    if(liveActive?.Invoke()==true)return;
                    cancellationToken.ThrowIfCancellationRequested();
                    store.ActivateStaging(stage.DatabasePath,store.Revision,false);activated=true;
                },cancellationToken);
                if(!activated)await Task.Delay(1000,cancellationToken);
            }
            processed+=length;imported++;
            await WriteAsync(()=>store.PutJob(new(jobId,"import","running",processed,total,$"{imported} imported; {duplicates} already recorded."),requestJson,store.Revision),cancellationToken);
        }
        await WriteAsync(()=>store.PutJob(new(jobId,"import","completed",processed,total,$"{imported} imported; {duplicates} already recorded."),requestJson,store.Revision),cancellationToken);
        return new(store.Revision,$"Imported {imported} captures; {duplicates} were already recorded.",jobId);
    }
    private static bool PrefixEqual(string shorter,string longer)
    {
        using var a=ByteFramer.OpenRead(shorter);using var b=ByteFramer.OpenRead(longer);
        if(a.Length>b.Length)return false;
        var x=new byte[65536];var y=new byte[65536];int n;
        while((n=a.Read(x))>0){b.ReadExactly(y.AsSpan(0,n));if(!x.AsSpan(0,n).SequenceEqual(y.AsSpan(0,n)))return false;}return true;
    }
    public MutationResult Rebuild(string? reportingZone=null,string? ownerSource=null,string? ownerId=null,CancellationToken cancellationToken=default,string? replacementSource=null,string? replacementPath=null)
    {
        var expected=store.Revision;
        if(store.History(new(),catalog).Items.Any(i=>i.State=="active"))throw new InvalidOperationException("Finish the active game session before reprocessing history.");
        var sources=store.SourceIds().Select(store.LoadSource).ToArray();
        if(ownerSource is not null&&!sources.Any(s=>s.Id==ownerSource))throw new ArgumentException("Choose an existing source.");
        var frozenDigests=new Dictionary<string,string>();
        foreach(var source in sources)
        {
            if(!File.Exists(source.Path))throw new IOException("Original source bytes are unavailable; existing versioned facts were preserved.");
            using var stream=ByteFramer.OpenRead(source.Path);
            if(!new IngestionEngine(store,catalog).VerifyAnchors(source,stream)||stream.Length!=source.Length)throw new IOException("Source changed; replacement requires a verified complete snapshot.");
            frozenDigests[source.Id]=ByteFramer.Digest(source.Path);
        }
        var directory=Path.Combine(store.Root,"staging","rebuild-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        store.Backup(Path.Combine(directory,"recap.db"));var stage=new RecapStore(directory);
        if(reportingZone is not null){stage.UpdateSettings(stage.Revision.SettingsRevision,stage.Settings with{ReportingZone=reportingZone});stage.ReprojectGameIntervals(reportingZone);}
        var engine=new IngestionEngine(stage,catalog);
        var continuations=stage.Continuations();
        foreach(var source in sources){cancellationToken.ThrowIfCancellationRequested();stage.ResetDerivedSource(source.Id,source.Id==ownerSource?ownerId:null);}
        string? replacementDigest=null;
        if(replacementSource is not null&&replacementPath is not null)
        {
            using var input=ByteFramer.OpenRead(replacementPath);var count=(int)Math.Min(4096,input.Length);replacementDigest=ByteFramer.Digest(replacementPath);
            stage.ReplaceSourceInput(replacementSource,replacementPath,ByteFramer.DetectEncoding(input),input.Length,count,ByteFramer.HashRange(input,0,count),replacementDigest);
        }
        var remaining=stage.SourceIds().Select(stage.LoadSource).ToList();var completed=new HashSet<string>();
        while(remaining.Count>0)
        {
            var source=remaining.FirstOrDefault(s=>!continuations.TryGetValue(s.Id,out var prior)||completed.Contains(prior))??throw new InvalidDataException("Cyclic source dependencies.");
            if(continuations.TryGetValue(source.Id,out var previous))
            {
                if(!engine.TryContinue(previous,source.Id))throw new InvalidOperationException("Source continuation no longer verifies; complete history was preserved.");
            }
            engine.ReadAll(source.Id,cancellationToken);completed.Add(source.Id);remaining.Remove(source);
        }
        foreach(var group in stage.SourceIds().Select(stage.LoadSource).GroupBy(s=>s.ContextId))
            engine.FinalizeSource(group.First().State.Segment?.SourceId??group.Last().Id);
        stage.Verify();cancellationToken.ThrowIfCancellationRequested();
        foreach(var source in sources)if(ByteFramer.Digest(source.Path)!=frozenDigests[source.Id])throw new IOException("Source changed during staging; existing history was preserved.");
        if(replacementPath is not null&&ByteFramer.Digest(replacementPath)!=replacementDigest)throw new IOException("Replacement bytes changed before activation; existing history was preserved.");
        store.ActivateStaging(stage.DatabasePath,expected,true);
        return new(store.Revision,"Reprocessed history with an atomic replacement. A recovery backup was saved.");
    }
}
