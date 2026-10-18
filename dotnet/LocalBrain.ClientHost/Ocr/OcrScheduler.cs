using System.Diagnostics;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

namespace LocalBrain.ClientHost.Ocr;

// Runs inside the existing Windows Bridge lifecycle; no new service or database.
internal sealed class OcrScheduler(OcrConfig config)
{
    private readonly OcrJobStore store=new(config);
    private readonly OcrResources resources=new();
    private long configChecked;
    private bool configDrift;
    private long quietUntil;
    private long incomingChecked;
    internal static async Task RunConfiguredAsync(CancellationToken token){
        OcrConfig? config;
        try{config=OcrConfig.Read(requireEnabled:false);}catch(Exception e)when(e is IOException or JsonException or OcrException or UnauthorizedAccessException){return;}
        if(config is null)return;
        await new OcrScheduler(config).RunAsync(token);
    }
    private bool Cancelled(string id)=>File.Exists(Path.Combine(store.DirectoryFor(id),"cancel.json"));
    private bool Priority(){
        // Leases are checked on every poll. Large pinned config is audited at most twice a second.
        if(OcrPriority.Active(config)){quietUntil=Environment.TickCount64+5000;return true;}
        if(Environment.TickCount64<quietUntil)return true;
        if(Environment.TickCount64-configChecked>=500){
            var current=OcrConfig.Read(requireEnabled:false);
            configDrift=current is null||OcrJobStore.ConfigHash(current)!=OcrJobStore.ConfigHash(config)||!OcrPriority.Healthy(config)||OcrPriority.LegacyConsumerActive(config);
            configChecked=Environment.TickCount64;
        }
        return configDrift;
    }
    internal async Task RunAsync(CancellationToken token){
        config.Validate();Directory.CreateDirectory(config.Root);Directory.CreateDirectory(store.Jobs);
        FileStream owner;
        try{owner=new FileStream(Path.Combine(config.Root,"scheduler.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException){return;}
        using(owner){
            OcrJobStore.Atomic(Path.Combine(config.Root,"scheduler-owner.json"),new{pid=Environment.ProcessId,birth=Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,config_hash=OcrJobStore.ConfigHash(config)});
            _=resources.Read();
            while(!token.IsCancellationRequested){
                if(Environment.TickCount64-incomingChecked>=60000){
                    try{store.RecoverIncoming();incomingChecked=Environment.TickCount64;}
                    catch(IOException e)when((e.HResult&65535)==32){await Task.Delay(100,token);continue;}
                }
                var jobs=Directory.EnumerateDirectories(store.Jobs).Order(StringComparer.Ordinal).ToArray();
                var didWork=false;
                foreach(var directory in jobs){
                    if(token.IsCancellationRequested)break;
                    var id=Path.GetFileName(directory);if(!Guid.TryParseExact(id,"N",out _))continue;
                    try{
                        var state=store.GetState(id);if(store.Expire(id))continue;
                        if(state.Status is "complete" or "cancelled"){store.TryCleanupInput(store.GetManifest(id));continue;}
                        if(state.Status=="failed")continue;
                        var manifest=store.GetManifest(id);store.VerifyPages(manifest,state);store.RecoverPageCommits(manifest);state=store.GetState(id);
                        if(Cancelled(id)){CancelTerminal(manifest);continue;}
                        var reason=OcrResources.Refusal(config,resources.Read(),Priority());
                        if(reason is not null){if(state.Status!="paused"||state.ErrorCode!=reason)store.Transition(id,"paused",reason);continue;}
                        // Every critical-operation lease is rechecked after admission and throughout the worker lifetime.
                        if(Priority())continue;
                        await ProcessOneAsync(manifest,token);didWork=true;break;
                    }catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
                    catch(IOException error)when((error.HResult&65535)==32){await Task.Delay(50,token);}
                    catch(Exception error)when(error is OcrException or IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException or TimeoutException or System.Runtime.InteropServices.ExternalException){
                        try{
                            OcrJobStore.Atomic(Path.Combine(store.DirectoryFor(id),"host-failure.json"),new{error_type=error.GetType().Name,method=error.TargetSite?.Name,hresult=error.HResult,stack_methods=new StackTrace(error,false).GetFrames()?.Select(f=>f.GetMethod()?.DeclaringType?.Name+"."+f.GetMethod()?.Name).Take(8).ToArray()});
                            if(store.GetState(id).Status is not("complete" or "cancelled"))store.Transition(id,error is OcrException{Code:"PRIORITY_WORK_ACTIVE" or "PRIOR_WORKER_STILL_ALIVE_INSPECT" or "SANDBOX_OWNER_STILL_ALIVE"}?"paused":"failed",error is OcrException o?o.Code:"HOST_OCR_FAILURE");
                        }catch(Exception e)when(e is IOException or UnauthorizedAccessException or OcrException or JsonException or KeyNotFoundException or InvalidOperationException){}
                    }
                }
                try{await Task.Delay(didWork?100:500,token);}catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
            }
        }
    }
    private void CancelTerminal(OcrJobStore.Manifest manifest){
        store.Transition(manifest.JobId,"cancelled","CANCELLED");
        store.TryCleanupInput(manifest);
    }
    private async Task ProcessOneAsync(OcrJobStore.Manifest manifest,CancellationToken token){
        var id=manifest.JobId;var state=store.GetState(id);var input=store.Input(manifest);
        if(!File.Exists(input)||OcrJobStore.HashFile(input)!=manifest.InputSha256)throw new OcrException("INPUT_UNAVAILABLE_OR_CHANGED");
        var inspect=state.PageCount==0;
        var page=Enumerable.Range(0,state.PageCount).FirstOrDefault(p=>!state.PageHashes.ContainsKey(p),-1);
        if(!inspect&&page<0){store.Finish(manifest);return;}
        var attempts=Path.Combine(store.DirectoryFor(id),"attempts");
        // A surviving prior child is never adopted, killed by a guessed PID, or duplicated.
        foreach(var processFile in Directory.EnumerateFiles(attempts,"process.json",SearchOption.AllDirectories)){
            var process=OcrJobStore.Read<JsonElement>(processFile);
            if(TaskAgentCoordinator.IsAlive(process.GetProperty("pid").GetInt32(),process.GetProperty("birth").GetInt64()))throw new OcrException("PRIOR_WORKER_STILL_ALIVE_INSPECT");
        }
        foreach(var attemptDirectory in Directory.EnumerateDirectories(attempts))OcrSandbox.Recover(config,manifest,attemptDirectory);
        // Pure OCR may retry an interrupted uncommitted page. Already committed page hashes are never repeated.
        store.CheckStorage(8*1024*1024+65536);
        var attempt=Path.Combine(attempts,Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attempt);
        OcrJobStore.New(Path.Combine(attempt,"intent.json"),new{job_id=id,page=inspect?-1:page,operation=inspect?"inspect":"page",input_sha256=manifest.InputSha256,config_hash=manifest.ConfigHash,owner_pid=Environment.ProcessId,created_at=DateTimeOffset.UtcNow});
        store.Transition(id,"running");
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(token);
        var task=OcrWorkerProcess.RunAsync(config,id,input,manifest.InputSha256,attempt,inspect?"inspect":"page",page,manifest.Profile,()=>Priority()||Cancelled(id),cancellation.Token);
        OcrWorkerProcess.Outcome result;
        try{result=await task;}catch(OperationCanceledException)when(token.IsCancellationRequested){store.Transition(id,"paused","HOST_STOPPED");return;}
        if(Cancelled(id)){CancelTerminal(manifest);return;}
        if(result.State=="paused"||token.IsCancellationRequested){store.Transition(id,"paused",token.IsCancellationRequested?"HOST_STOPPED":result.ErrorCode);return;}
        if(result.State!="complete"||result.Result is null){store.Transition(id,"failed",result.ErrorCode??"WORKER_FAILED");return;}
        if(inspect){
            var count=result.Result.Value.GetProperty("page_count").GetInt32();
            if(count<1||count>config.MaxPages)throw new OcrException("PAGE_LIMIT");
            store.Transition(id,"queued",count:count);return;
        }
        state=store.CommitPage(manifest,page,result.Result.Value);
        var transientResult=Path.Combine(attempt,"scratch","result.json");if(File.Exists(transientResult))File.Delete(transientResult);
        if(state.CompletedPages==state.PageCount)store.Finish(manifest);
        else store.Transition(id,Priority()?"paused":"queued",Priority()?"PRIORITY_WORK_ACTIVE":null);
    }
}
