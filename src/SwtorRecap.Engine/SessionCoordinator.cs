using System.Threading.Channels;
using SwtorRecap.Core;
using SwtorRecap.Storage;

namespace SwtorRecap.Engine;

public sealed record ObservedProcess(int Pid,string Scope,DateTimeOffset? StartedUtc,DateTimeOffset FirstObservedUtc)
{
    public string Key => Scope+":"+Pid+":"+(StartedUtc?.ToString("O")??"uncertain-"+FirstObservedUtc.ToString("O"));
}
public interface IGameProcessSource
{
    IReadOnlyList<ObservedProcess> Discover();
    Task WaitForExitAsync(ObservedProcess process,CancellationToken cancellationToken);
}
public interface IAwakeClock { long Milliseconds { get; } }
public sealed class PortableAwakeClock(TimeProvider time) : IAwakeClock
{ public long Milliseconds => checked((long)(time.GetTimestamp()*1000.0/time.TimestampFrequency)); }
public sealed class NoGameProcesses : IGameProcessSource
{
    public IReadOnlyList<ObservedProcess> Discover()=>[];
    public Task WaitForExitAsync(ObservedProcess process,CancellationToken cancellationToken)=>Task.Delay(Timeout.Infinite,cancellationToken);
}

public sealed class SessionCoordinator : IDisposable
{
    private readonly RecapStore _store; private readonly IngestionEngine _ingest; private readonly IGameProcessSource _processes;
    private readonly TimeProvider _time; private readonly IAwakeClock _awake; private readonly Func<FileStream,string?> _identity;
    private readonly Dictionary<string,FileStream> _handles=[];
    private ObservedProcess? _bound; private DateTimeOffset _lastWall; private long _lastAwake;
    private DateTimeOffset _lastInventory=DateTimeOffset.MinValue; private FileSystemWatcher? _watcher;
    private string? _folder; private bool _first=true; private CancellationTokenSource? _exitWatch;
    public Channel<bool> Wake {get;}=Channel.CreateBounded<bool>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropWrite});
    public string? ContainerId {get;private set;}
    public bool Active=>_bound is not null;
    public bool ShouldExit {get;private set;}
    public string State {get;private set;}="Idle";
    public string Message {get;private set;}="Ready for SWTOR";
    public string DefaultLogFolder {get;}
    public SessionCoordinator(RecapStore store,Catalog catalog,IGameProcessSource processes,IAwakeClock awake,string defaultFolder,Func<FileStream,string?>? physicalIdentity=null,TimeProvider? time=null)
    {_store=store;_ingest=new(store,catalog);_processes=processes;_awake=awake;DefaultLogFolder=defaultFolder;_identity=physicalIdentity??(_=>null);_time=time??TimeProvider.System;}

    public void Tick(bool inventoryHint=false)
    {
        var now=_time.GetUtcNow();var candidates=_processes.Discover();
        if(_first){_store.RecoverSessions(candidates.Count==1&&candidates[0].StartedUtc is not null?candidates[0].Key:null);_first=false;}
        if(_bound is null)
        {
            if(candidates.Count!=1)
            {
                if(candidates.Count==0&&_store.Settings.Paused)_store.UpdateSettings(_store.Revision.SettingsRevision,_store.Settings with{Paused=false});
                State="Idle";Message=candidates.Count>1?"Multiple SWTOR instances found. Close the extra instance to begin tracking.":"Ready for SWTOR";return;
            }
            _bound=candidates[0];ContainerId=_store.CreateContainer("session",_bound.StartedUtc is null?null:_bound.Key,now);
            _lastWall=now;_lastAwake=_awake.Milliseconds;State="Attaching";inventoryHint=true;
            _exitWatch=new();var token=_exitWatch.Token;
            _=_processes.WaitForExitAsync(_bound,token).ContinueWith(_=>Wake.Writer.TryWrite(true),CancellationToken.None,TaskContinuationOptions.OnlyOnRanToCompletion,TaskScheduler.Default);
        }
        var present=candidates.Any(p=>p.Pid==_bound.Pid&&(p.StartedUtc is null||p.StartedUtc==_bound.StartedUtc));
        ObserveTime(now);
        EnsureWatcher(_store.Settings.LogFolder??DefaultLogFolder);
        if(!present)
        {
            State="Draining";Reconcile(true);
            if(_store.Settings.Paused)foreach(var (id,handle) in _handles)_ingest.SkipToCurrent(id,handle);
            // Bounded quiet drain includes newly created files and complete trailing records.
            var until=_time.GetUtcNow()+TimeSpan.FromSeconds(5);var quiet=0;
            while(_time.GetUtcNow()<until&&quiet<3)
            {
                var count=ReadHandles();quiet=count==0?quiet+1:0;
                Thread.Sleep(150);Reconcile(true);
            }
            foreach(var source in _store.SourceIds(ContainerId).Select(_store.LoadSource).GroupBy(s=>s.ContextId).Select(g=>g.Last()))_ingest.FinalizeSource(source.Id);
            _store.FinalizeSession(ContainerId!,now,"process_exit");
            if(_store.Settings.Paused)_store.UpdateSettings(_store.Revision.SettingsRevision,_store.Settings with{Paused=false});
            _store.DailyBackup();
            ReleaseHandles();_bound=null;ContainerId=null;_exitWatch?.Cancel();_exitWatch?.Dispose();_exitWatch=null;
            ShouldExit=_store.Settings.SessionOnce;State="Idle";Message="Ready for the next session";return;
        }
        Reconcile(inventoryHint);if(!_store.Settings.Paused)ReadHandles();
        State=_store.Settings.Paused?"Paused":_handles.Count==0?"WaitingForLog":"Tracking";
        Message=_store.Settings.Paused?"Recording paused":_handles.Count==0?FolderMessage():candidates.Count>1?"Tracking the bound game; additional SWTOR instances are unsupported.":"Recording available combat logs";
    }
    private void ObserveTime(DateTimeOffset now)
    {
        var awake=_awake.Milliseconds;
        if(!_store.Settings.Paused&&ContainerId is not null&&awake>=_lastAwake&&awake-_lastAwake>0)
            _store.RecordAwake(ContainerId,_lastWall,now,awake-_lastAwake,_store.Settings.ReportingZone);
        _lastWall=now;_lastAwake=awake;
    }
    public void SetPaused(bool paused)
    {
        if(_store.Settings.Paused==paused)return;
        if(Active)
        {
            ObserveTime(_time.GetUtcNow());Reconcile(true);
            if(paused){ReadHandles();foreach(var (id,handle) in _handles)_ingest.SkipToCurrent(id,handle);}
            else foreach(var (id,handle) in _handles)_ingest.SkipToCurrent(id,handle);
        }
        var revision=_store.Revision;_store.UpdateSettings(revision.SettingsRevision,_store.Settings with{Paused=paused});
        _lastWall=_time.GetUtcNow();_lastAwake=_awake.Milliseconds;
        State=paused?"Paused":Active?"Tracking":"Idle";Message=paused?"Recording paused":"Ready for SWTOR";
    }
    public void Suspend()
    {
        if(Active)ObserveTime(_time.GetUtcNow());
        _lastWall=_time.GetUtcNow();_lastAwake=_awake.Milliseconds;
    }
    private string FolderMessage()=>!Directory.Exists(_folder)?"Game detected. Combat-log folder is unavailable; check Settings.":"Game detected. No eligible combat log yet. Enable combat logging in SWTOR Preferences.";
    private void EnsureWatcher(string folder)
    {
        if(folder==_folder&&_watcher is not null)return;
        _watcher?.Dispose();_watcher=null;_folder=folder;_lastInventory=DateTimeOffset.MinValue;
        if(!Directory.Exists(folder))return;
        try
        {
            _watcher=new FileSystemWatcher(folder,"*.txt"){IncludeSubdirectories=false,NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite|NotifyFilters.Size};
            _watcher.Changed+=(_,_)=>Wake.Writer.TryWrite(true);_watcher.Created+=(_,_)=>Wake.Writer.TryWrite(true);
            _watcher.Renamed+=(_,_)=>Wake.Writer.TryWrite(true);_watcher.Deleted+=(_,_)=>Wake.Writer.TryWrite(true);_watcher.Error+=(_,_)=>Wake.Writer.TryWrite(true);
            _watcher.EnableRaisingEvents=true;
        }
        catch(UnauthorizedAccessException){Message="Combat-log folder is inaccessible. Choose a readable folder in Settings.";}
    }
    public void Reconcile(bool force)
    {
        if(!Active||ContainerId is null||_folder is null||!Directory.Exists(_folder))return;
        if(!force&&_time.GetUtcNow()-_lastInventory<TimeSpan.FromSeconds(10))return;_lastInventory=_time.GetUtcNow();
        var sources=_store.SourceIds(ContainerId).Select(_store.LoadSource).ToArray();
        foreach(var path in Directory.EnumerateFiles(_folder,"combat_*.txt",SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            if(_handles.Count>=32){Message="Too many active sources; close the game and import the remaining recordings.";break;}
            FileStream? stream=null;
            try
            {
                stream=ByteFramer.OpenRead(path);var physical=_identity(stream);
                var known=sources.FirstOrDefault(s=>physical is not null?s.PhysicalIdentity==physical:s.Path==path);
                if(known is not null&&_handles.TryGetValue(known.Id,out var retained))
                {if(_store.Settings.Paused)_ingest.SkipToCurrent(known.Id,retained);continue;}
                if(known is not null&&_ingest.VerifyAnchors(known,stream))
                {_handles[known.Id]=stream;stream=null;_store.AddAlias(known.Id,path);continue;}
                if(known is not null)_store.MarkIntegrity(known.Id,"replaced_or_truncated",stream.Length);
                var date=LogClockState.DateFromFilename(path);var created=new FileInfo(path).CreationTimeUtc;
                var processStart=_bound!.StartedUtc??_bound.FirstObservedUtc;
                var earliestDate=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(processStart,TimeZoneInfo.Local).DateTime).AddDays(-1);
                if(date is null||date<earliestDate||date>DateOnly.FromDateTime(_time.GetLocalNow().DateTime).AddDays(1)||created<processStart.UtcDateTime.AddSeconds(-2))continue;
                var id=_ingest.Register(ContainerId,path,false,date,physical:physical);_handles[id]=stream;stream=null;
                if(_store.Settings.Paused)_ingest.SkipToCurrent(id,_handles[id]);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){Message="A combat log is temporarily unreadable; saved offsets are retained.";}
            finally{stream?.Dispose();}
        }
    }
    private int ReadHandles()
    {
        if(_store.Settings.Paused)return 0;var count=0;
        foreach(var (id,stream) in _handles.ToArray())
        {
            var source=_store.LoadSource(id);
            if(source.Offset==0)
            {
                foreach(var prior in _handles.Keys.Where(other=>other!=id).Select(_store.LoadSource).Where(s=>s.Offset>0).OrderByDescending(s=>s.State.Clock.Date).ThenByDescending(s=>s.State.Clock.Previous))
                    if(_ingest.TryContinue(prior.Id,id))break;
                source=_store.LoadSource(id);
            }
            if(!_ingest.VerifyAnchors(source,stream))
            {_store.MarkIntegrity(id,"source_replaced_or_truncated",stream.Length);stream.Dispose();_handles.Remove(id);_lastInventory=DateTimeOffset.MinValue;continue;}
            count+=_ingest.ReadOnce(id,stream);
        }
        return count;
    }
    public void Stop(string reason="tracker_stopped")
    {
        if(Active){ObserveTime(_time.GetUtcNow());Reconcile(true);if(_store.Settings.Paused){foreach(var (id,handle) in _handles)_ingest.SkipToCurrent(id,handle);}else ReadHandles();}
        // Leave a proved process instance resumable; no time is inferred across the tracker gap.
        ReleaseHandles();State="Stopped";Message=reason;
    }
    private void ReleaseHandles(){foreach(var handle in _handles.Values)handle.Dispose();_handles.Clear();_watcher?.Dispose();_watcher=null;}
    public void Dispose(){_exitWatch?.Cancel();_exitWatch?.Dispose();_exitWatch=null;ReleaseHandles();}
}
