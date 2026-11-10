using SwtorRecap.Core;
using SwtorRecap.Storage;

namespace SwtorRecap.Engine;

public sealed class IngestionEngine(RecapStore store, Catalog catalog)
{
    public bool TryContinue(string priorId,string nextId)
    {
        var prior=store.LoadSource(priorId);var next=store.LoadSource(nextId);
        if(prior.ContainerId!=next.ContainerId||next.Offset!=0||prior.State.Owner is null||prior.State.Server is null||prior.State.Area is null||prior.State.Gap||prior.State.Clock.ClockUncertain||!File.Exists(prior.Path))return false;
        using var previous=ByteFramer.OpenRead(prior.Path);if(previous.Length!=prior.Offset||!VerifyAnchors(prior,previous))return false;
        using var stream=ByteFramer.OpenRead(next.Path);var frames=ByteFramer.Read(stream,0,next.Encoding,false,maxRecords:1);if(frames.Records.Length!=1)return false;
        var e=new CombatLogParser().Parse(frames.Records[0].Text.AsSpan()).Event;
        if(e is null||e.Kind!=EventKind.AreaEntered||e.Source.Key!=prior.State.Owner||e.Server!=prior.State.Server||e.AreaId!=prior.State.Area||e.DifficultyId!=prior.State.Difficulty||e.GroupSize!=prior.State.GroupSize)return false;
        var last=prior.State.Clock.Date?.ToDateTime(prior.State.Clock.Previous??TimeOnly.MinValue);
        var nextTime=next.State.Clock.Date?.ToDateTime(e.Time);
        if(last is null||nextTime is null||nextTime<last||nextTime-last>TimeSpan.FromMinutes(2))return false;
        store.LinkContinuation(prior,next);return true;
    }
    public string Register(string container,string path,bool sealedSource,DateOnly? date=null,string? sourceZone=null,string? physical=null,string? digest=null,string? owner=null)
    {
        using var stream=ByteFramer.OpenRead(path);var count=(int)Math.Min(4096,stream.Length);
        return store.CreateSource(container,path,physical,ByteFramer.DetectEncoding(stream),date??LogClockState.DateFromFilename(path),sourceZone??TimeZoneInfo.Local.Id,stream.Length,count,ByteFramer.HashRange(stream,0,count),sealedSource,digest,owner);
    }
    public bool VerifyAnchors(SourceCheckpoint source,FileStream stream)
    {
        return stream.Length>=source.Offset && stream.Length>=source.PrefixLength
            && ByteFramer.HashRange(stream,0,source.PrefixLength)==source.PrefixHash
            && (source.BoundaryHash.Length==0 || ByteFramer.HashRange(stream,source.BoundaryStart,checked((int)(source.Offset-source.BoundaryStart)))==source.BoundaryHash);
    }
    public int ReadOnce(string sourceId,FileStream? retainedHandle=null,int maxRecords=2000,int chunkSize=65536)
    {
        var source=store.LoadSource(sourceId);var stream=retainedHandle??ByteFramer.OpenRead(source.Path);
        try
        {
            if(!VerifyAnchors(source,stream)){store.MarkIntegrity(source.Id,"source_replaced_or_truncated",stream.Length);return 0;}
            if(source.Offset==0)source=source with{Encoding=ByteFramer.DetectEncoding(stream)};
            var exclusion=store.NextExclusion(sourceId,source.Offset,source.State.LastExclusionSource!=sourceId||source.State.LastExclusionEnd!=source.Offset);
            if(exclusion is not null&&exclusion.Start<=source.Offset)
            {SkipToCurrent(sourceId,stream,exclusion.End);return 1;}
            var frames=ByteFramer.Read(stream,source.Offset,source.Encoding,source.Sealed,source.State.DiscardPartial,maxRecords,chunkSize:chunkSize);
            var records=exclusion is null?frames.Records:frames.Records.Where(r=>r.End<=exclusion.Start).ToArray();
            if(records.Length==0)
            {
                if(stream.Length!=source.Length)store.Commit(source,new(){State=source.State,End=source.Offset},stream.Length,source.BoundaryStart,source.BoundaryHash);
                return 0;
            }
            var batch=new CombatReducer(catalog,store.Settings.ReportingZone).Reduce(source.State,source.Id,source.ContainerId,records);
            batch.State.DiscardPartial=records.Length==frames.Records.Length&&frames.DiscardPartial;
            var boundary=Math.Max(0,batch.End-256);var hash=ByteFramer.HashRange(stream,boundary,checked((int)(batch.End-boundary)));
            store.Commit(source,batch,stream.Length,boundary,hash);
            return records.Length;
        }
        finally{if(retainedHandle is null)stream.Dispose();}
    }
    public void ReadAll(string sourceId,CancellationToken cancellationToken=default)
    {while(true){cancellationToken.ThrowIfCancellationRequested();if(ReadOnce(sourceId)==0)break;}}
    public void FinalizeSource(string sourceId)
    {
        var source=store.LoadSource(sourceId);var state=Json.Clone(source.State);
        var batch=new BatchProjection{State=state,End=source.Offset};
        if(source.Offset<source.Length)batch.Gaps.Add(new(sourceId,source.Offset,source.Length,"source","incomplete_live_tail"));
        if(state.Lives.Values.Any(l=>l.CombatStart is not null))batch.Gaps.Add(new(sourceId,source.Offset,source.Offset,"time","combat_end_unobserved"));
        CombatReducer.Interrupt(state,batch,"source_finalized");
        store.Commit(source,batch,source.Length,source.BoundaryStart,source.BoundaryHash);
    }
    public void SkipToCurrent(string sourceId,FileStream? retainedHandle=null,long? until=null)
    {
        var source=store.LoadSource(sourceId);var stream=retainedHandle??ByteFramer.OpenRead(source.Path);
        try
        {
            if(!VerifyAnchors(source,stream)){store.MarkIntegrity(sourceId,"pause_source_integrity",stream.Length);return;}
            // Recover only metadata from skipped bytes. Metrics, combat and terminal state are never credited.
            var state=Json.Clone(source.State);var parser=new CombatLogParser();var offset=source.Offset;
            var length=until??stream.Length;
            if(length==offset&&state.LastExclusionSource==sourceId&&state.LastExclusionEnd==length)return;
            if(length<offset||length>stream.Length)throw new IOException("Excluded source range is unavailable.");
            while(offset<length)
            {
                var framed=ByteFramer.Read(stream,offset,source.Encoding,false,maxRecords:2000);
                var records=framed.Records.Where(r=>r.End<=length).ToArray();
                if(records.Length==0)break;
                foreach(var record in records)
                {
                    offset=record.End;var e=parser.Parse(record.Text.AsSpan()).Event;
                    if(e is null)continue;
                    state.Clock.Resolve(e.Time,store.Settings.ReportingZone);
                    if(e.Kind==EventKind.AreaEntered){state.Owner=e.Source.Key;state.OwnerName=e.Source.Name;state.Server=e.Server;state.Area=e.AreaId;state.Difficulty=e.DifficultyId;state.GroupSize=e.GroupSize;state.Format=e.Format;}
                }
            }
            var batch=new BatchProjection{State=state,End=length};
            CombatReducer.Interrupt(state,batch,"paused");
            if(source.State.Segment is { } segment)batch.Segments.Add(segment with{End=source.Offset});
            state.Segment=null;state.Lives.Clear();state.Gap=true;
            state.DiscardPartial=length>0&&ReadLast(stream,length)!=10;
            batch.Gaps.Add(new(sourceId,source.Offset,length,"recording","paused_bytes",true));
            batch.Exclusions.Add(new(sourceId,source.Offset,length));state.LastExclusionSource=sourceId;state.LastExclusionEnd=length;
            var boundary=Math.Max(0,length-256);store.Commit(source,batch,stream.Length,boundary,ByteFramer.HashRange(stream,boundary,checked((int)(length-boundary))));
        }
        finally{if(retainedHandle is null)stream.Dispose();}
    }
    private static int ReadLast(FileStream stream,long end){stream.Position=end-1;return stream.ReadByte();}
}
