using System.Text.Json;
using SwtorRecap.Core;
using SwtorRecap.Storage;

namespace SwtorRecap.Engine;

public interface IStartupRegistration
{
    bool IsEnabled {get;}
    void SetEnabled(bool enabled);
}
public sealed class PreviewStartup : IStartupRegistration
{
    public bool IsEnabled=>false;
    public void SetEnabled(bool enabled){if(enabled)throw new InvalidOperationException("Startup registration is unavailable in isolated preview.");}
}

/// <summary>Same typed services in preview and production. UI calls execute on workers, never on its render thread.</summary>
public sealed class TrackerService : IAsyncDisposable
{
    public RecapStore Store {get;}
    public Catalog Catalog {get;}
    public SessionCoordinator? Coordinator {get;}
    private readonly IStartupRegistration _startup;
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly Dictionary<string,CancellationTokenSource> _jobs=[];
    private readonly CancellationTokenSource _shutdown=new();
    private readonly List<Task> _jobTasks=[];
    private bool HasJobs {get{lock(_jobs)return _jobs.Count>0;}}
    public CancellationToken Shutdown=>_shutdown.Token;
    public string? LastError {get;private set;}
    public TrackerService(RecapStore store,Catalog catalog,SessionCoordinator? coordinator=null,IStartupRegistration? startup=null)
    {Store=store;Catalog=catalog;Coordinator=coordinator;_startup=startup??new PreviewStartup();Store.InterruptPendingJobs();}
    public async Task MonitorAsync(CancellationToken token)
    {
        if(Coordinator is null)return;
        while(!token.IsCancellationRequested&&!Shutdown.IsCancellationRequested)
        {
            await _gate.WaitAsync(token);
            try{Coordinator.Tick(Coordinator.Wake.Reader.TryRead(out _));LastError=null;if(Coordinator.ShouldExit)_shutdown.Cancel();}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException){LastError="Saving delayed: "+ex.Message;}
            finally{_gate.Release();}
            await Task.Delay(Coordinator.Active?2000:3000,token);
        }
    }
    public async Task<IpcResponse> HandleAsync(IpcRequest request,CancellationToken token=default)
    {
        try
        {
            if(request.ProtocolVersion!=Versions.Protocol||request.RequestId.Length is <1 or >128)throw new InvalidDataException("Incompatible protocol or invalid request identifier.");
            var mutation=request.Command is not ("Snapshot" or "History" or "Detail" or "Recap" or "Coverage" or "Jobs");
            if(mutation&&request.ExpectedEpoch!=Store.Revision.HistoryEpoch)throw new InvalidOperationException("History changed. Reconnect before changing it.");
            if(mutation&&Store.Receipt(request.RequestId) is { } receipt)return Json.Read<IpcResponse>(receipt);
            object payload;
            if(!mutation)
            {
                payload=await Task.Run(()=>request.Command switch
                {
                    "Snapshot"=>(object)Snapshot(),
                    "History"=>Store.History(Read<QueryFilter>(request),Catalog),
                    "Detail"=>request.Parameters.ValueKind==JsonValueKind.String?Store.Detail(Read<string>(request),Catalog):Detail(Read<DetailRequest>(request)),
                    "Recap"=>Store.Recap(Read<QueryFilter>(request),Catalog),
                    "Coverage"=>Catalog.Document,
                    "Jobs"=>Store.Jobs(),
                    _=>throw new InvalidOperationException("Unsupported query.")
                },token);
            }
            else
            {
                await _gate.WaitAsync(token);
                try
                {
                    if(Store.Receipt(request.RequestId) is { } completed)return Json.Read<IpcResponse>(completed);
                    if(request.ExpectedEpoch!=Store.Revision.HistoryEpoch)throw new InvalidOperationException("History changed; reconnect before changing it.");
                    payload=await MutateAsync(request,token);
                    var result=new IpcResponse(Versions.Protocol,request.RequestId,true,null,((MutationResult)payload).Revision,JsonSerializer.SerializeToElement(payload,Json.Options));
                    if(request.Command!="Restore")Store.SaveReceipt(request.RequestId,Json.Write(result));
                    return result;
                }
                finally{_gate.Release();}
            }
            var revision=payload switch{TrackerSnapshot s=>s.Revision,HistoryPage h=>h.Revision,HistoryDetail d=>d.Revision,RecapSnapshot r=>r.Revision,MutationResult m=>m.Revision,_=>Store.Revision};
            var response=new IpcResponse(Versions.Protocol,request.RequestId,true,null,revision,JsonSerializer.SerializeToElement(payload,Json.Options));
            return response;
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {return new(Versions.Protocol,request.RequestId,false,ex.Message,Store.Revision,JsonSerializer.SerializeToElement(new{ },Json.Options));}
    }
    public TrackerSnapshot Snapshot()
    {
        // Snapshot components are retried if a commit lands between the short reads.
        for(var attempt=0;attempt<4;attempt++)
        {
            var revision=Store.Revision;var current=Coordinator is not null&&!Coordinator.Active?null:Store.Current(Catalog);var settings=Store.Settings;var jobs=Store.Jobs();
            var character=current is null?null:Store.CurrentCharacter(current.Id);
            if(Store.Revision==revision)return new(revision,LastError is null?Coordinator?.State??"Preview":"Recovering",LastError??Coordinator?.Message??"Isolated preview • synthetic and licensed fixture history",settings,current,Store.SqliteVersion,Versions.Parser,Versions.Catalog,_startup.IsEnabled==settings.StartWithWindows,jobs,settings.LogFolder??Coordinator?.DefaultLogFolder,Store.Root,Versions.App,character);
        }
        throw new IOException("History is changing; retry the snapshot.");
    }
    private static T Read<T>(IpcRequest r)=>r.Parameters.Deserialize<T>(Json.Options)??throw new ArgumentException("Missing command parameters.");
    private HistoryDetail Detail(DetailRequest request)=>Store.Detail(request.Id,Catalog,request.CharacterId);
    private async Task<object> MutateAsync(IpcRequest r,CancellationToken token)
    {
        switch(r.Command)
        {
            case "Pause":
                if(Coordinator is not null&&!Coordinator.Active)throw new InvalidOperationException("SWTOR is closed. Recording controls are available during a game session.");
                var paused=Read<bool>(r);
                if(Coordinator is not null)Coordinator.SetPaused(paused);else Store.UpdateSettings(Store.Revision.SettingsRevision,Store.Settings with{Paused=paused});
                return new MutationResult(Store.Revision,paused?"Recording paused.":"Recording resumed.");
            case "Settings":
                var update=Read<SettingsUpdate>(r);
                var folder=string.IsNullOrWhiteSpace(update.LogFolder)?null:Path.GetFullPath(update.LogFolder);
                if(folder is not null&&!Directory.Exists(folder))throw new DirectoryNotFoundException("Choose an existing readable combat-log folder.");
                Store.UpdateSettings(update.ExpectedRevision,Store.Settings with{LogFolder=folder,StartWithWindows=update.StartWithWindows,SessionOnce=update.SessionOnce});
                _startup.SetEnabled(update.StartWithWindows);
                if(_startup.IsEnabled!=update.StartWithWindows)throw new IOException("The preference was saved, but Windows startup registration could not be verified. Retry from Settings.");
                Coordinator?.Wake.Writer.TryWrite(true);return new MutationResult(Store.Revision,"Settings saved and startup registration verified.");
            case "Import":
                var import=Read<ImportRequest>(r);var id=r.RequestId;
                if(Store.Jobs().Any(j=>j.Id==id))return new MutationResult(Store.Revision,"Import job already exists.",id);
                if(HasJobs)throw new InvalidOperationException("Finish or cancel the current import before starting another.");
                var cancellation=CancellationTokenSource.CreateLinkedTokenSource(Shutdown);lock(_jobs){_jobs[id]=cancellation;}
                Store.PutJob(new(id,"import","queued",0,0,null),Json.Write(import),Store.Revision);
                var task=Task.Run(async()=>
                {
                    try
                    {
                        // All production writes share the monitor/mutation gate; staging has its own database.
                        await new HistoryMaintenance(Store,Catalog,_gate).ImportAsync(id,import,()=>Coordinator?.Active==true,cancellation.Token);
                    }
                    catch(OperationCanceledException){await SetJob(new(id,"import","cancelled",0,0,"Activated sources were retained. Reimport resumes by verified capture identity."),import);}
                    catch(Exception ex){await SetJob(new(id,"import","failed",0,0,ex.Message),import);}
                    finally{lock(_jobs){_jobs.Remove(id);}cancellation.Dispose();}
                },CancellationToken.None);
                lock(_jobs){_jobTasks.RemoveAll(t=>t.IsCompleted);_jobTasks.Add(task);}
                return new MutationResult(Store.Revision,"Import queued.",id);
            case "CancelImport":
                lock(_jobs){if(_jobs.TryGetValue(Read<string>(r),out var job))job.Cancel();}
                return new MutationResult(Store.Revision,"Cancellation requested.");
            case "Backup":
                var backup=Path.GetFullPath(Read<string>(r));Store.Backup(backup);return new MutationResult(Store.Revision,"Backup verified.",Path:backup);
            case "PrepareUpdate":
                Coordinator?.Tick(true);
                if(Coordinator?.Active==true)throw new InvalidOperationException("Close SWTOR before installing an update.");
                if(HasJobs)throw new InvalidOperationException("Finish or cancel the import before installing an update.");
                var updateBackup=Path.Combine(Store.Root,"backups","before-update-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".db");
                Directory.CreateDirectory(Path.GetDirectoryName(updateBackup)!);Store.Backup(updateBackup);
                return new MutationResult(Store.Revision,"Recovery backup verified.",Path:updateBackup);
            case "Restore":
                if(Coordinator?.Active==true)throw new InvalidOperationException("Close SWTOR before replacing history.");
                if(HasJobs)throw new InvalidOperationException("Cancel or finish imports before restoring history.");
                Store.ActivateStaging(Path.GetFullPath(Read<string>(r)),Store.Revision,true,true);return new MutationResult(Store.Revision,"History restored; recovery backup retained.");
            case "Rebuild":
                if(HasJobs)throw new InvalidOperationException("Finish imports before reprocessing history.");
                var zone=Read<string>(r);return await Task.Run(()=>new HistoryMaintenance(Store,Catalog).Rebuild(zone,cancellationToken:token),token);
            case "AttributeOwner":
                if(HasJobs)throw new InvalidOperationException("Finish imports before changing attribution.");
                var choice=Read<string[]>(r);if(choice.Length!=2)throw new ArgumentException("Choose source and player ID.");
                return await Task.Run(()=>new HistoryMaintenance(Store,Catalog).Rebuild(ownerSource:choice[0],ownerId:choice[1],cancellationToken:token),token);
            case "Quit":
                Coordinator?.Stop();_shutdown.Cancel();return new MutationResult(Store.Revision,"Tracker stopped; history retained.");
            default:throw new InvalidOperationException("Unsupported command.");
        }
    }
    private async Task SetJob(JobSummary job,ImportRequest request)
    {await _gate.WaitAsync();try{Store.PutJob(job,Json.Write(request),Store.Revision);}finally{_gate.Release();}}
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();Task[] tasks;
        lock(_jobs){foreach(var job in _jobs.Values)job.Cancel();tasks=_jobTasks.ToArray();}
        await Task.WhenAll(tasks);await _gate.WaitAsync();
        try{Coordinator?.Stop();Coordinator?.Dispose();}finally{_gate.Release();}
        _gate.Dispose();_shutdown.Dispose();
    }
}

public sealed class LocalTrackerClient(TrackerService service) : ITrackerClient
{
    public async Task<T> RequestAsync<T>(string command,object? parameters=null,CancellationToken cancellationToken=default)
    {
        var request=new IpcRequest(Versions.Protocol,Guid.NewGuid().ToString("N"),command,JsonSerializer.SerializeToElement(parameters??new QueryFilter(),Json.Options),service.Store.Revision.HistoryEpoch);
        var response=await Task.Run(()=>service.HandleAsync(request,cancellationToken),cancellationToken);
        if(!response.Success)throw new InvalidOperationException(response.Error);
        return response.Payload.Deserialize<T>(Json.Options)??throw new InvalidDataException("Empty service response.");
    }
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
