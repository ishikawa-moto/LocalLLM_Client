using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalBrain.ClientHost.AgentV2;

// Detached Windows Host owner. MCP only submits/observes; it never holds a model call open.
internal static class TaskAgentCoordinator
{
    internal sealed record Job(string TaskId,string Root,string RequestJson,string RequestHash,string Head,
        string ControlRoot,string ConfigHash,string RuntimeDll,string RuntimeHash,TaskCommandAuthorization.Pending Authorization);
    internal sealed record Report(string TaskId,string State,bool EligibleForCompletion,int ActorTurns,
        string? Reason,DateTimeOffset UpdatedAt,DateTimeOffset? ActiveSince=null);
    internal static string WorkspaceDirectory(string root,string? control=null)=>Path.Combine(control??CanonicalStore.DefaultRoot,"agent-jobs",HostTaskJournal.WorkspaceId(root));
    internal static bool IsAlive(int pid,long birth)
    {
        try{using var p=Process.GetProcessById(pid);return !p.HasExited&&p.StartTime.ToUniversalTime().Ticks==birth;}catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}
    }
    internal static bool OwnerAlive(string dir)
    {
        var path=Path.Combine(dir,"worker-process.json");if(!File.Exists(path))return false;
        using var d=JsonDocument.Parse(File.ReadAllBytes(path));return IsAlive(d.RootElement.GetProperty("pid").GetInt32(),d.RootElement.GetProperty("birth").GetInt64());
    }
    private static T Read<T>(string path){CanonicalStore.GuardPath(path);return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path),ClientConfig.JsonOptions)??throw new InvalidDataException("Invalid managed task record");}
    internal static string? CurrentDirectory(string root,string? control=null)
    {
        var workspace=WorkspaceDirectory(root,control);var pointer=Path.Combine(workspace,"current-job.json");if(!File.Exists(pointer))return null;
        using var p=JsonDocument.Parse(File.ReadAllBytes(pointer));var id=p.RootElement.GetProperty("task_id").GetString();
        if(!Guid.TryParseExact(id,"N",out _))throw new InvalidDataException("Invalid managed task identity");
        var dir=Path.Combine(workspace,id!);CanonicalStore.GuardPath(dir);return dir;
    }
    internal static async Task<object> SubmitAsync(string root,AgentTaskRequest request,ClientConfig config,bool resume=false,string? control=null,bool launch=true,bool runnerSummary=false)
    {
        if(request.CommandExecution is null)throw new InvalidDataException("Managed tasks require an explicit command_execution scope");
        var parent=WorkspaceDirectory(root,control);CanonicalStore.GuardPath(parent);Directory.CreateDirectory(parent);
        using var submission=new FileStream(Path.Combine(parent,"submission.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var current=CurrentDirectory(root,control);var hash=AgentTaskRunner.RequestHash(request);
        if(current is not null){
            var prior=Read<Job>(Path.Combine(current,"job.json"));
            var report=File.Exists(Path.Combine(current,"report.json"))?Read<Report>(Path.Combine(current,"report.json")):null;
            var canonical=CanonicalStore.ReadLatest(control??CanonicalStore.DefaultRoot,HostTaskJournal.WorkspaceId(root));
            var candidateDiscarded=false;
            if(canonical?.TaskId==prior.TaskId){
                using var state=JsonDocument.Parse(canonical.StateJson);
                var priorPhase=state.RootElement.TryGetProperty("phase",out var phaseValue) && phaseValue.ValueKind==JsonValueKind.String?phaseValue.GetString():null;
                candidateDiscarded=priorPhase=="cancelled" && report?.State=="cancelled" &&
                    state.RootElement.TryGetProperty("candidate_disposition",out var disposition) && disposition.ValueKind==JsonValueKind.String && disposition.GetString()=="discarded";
                if((resume || prior.RequestHash!=hash) && (priorPhase is "v3_preparing" or "v3_running" or "v3_unknown"))
                    throw new InvalidOperationException("A prior v3 attempt requires process reconciliation before another task or resume");
            }
            if(prior.RequestHash==hash && (resume || !candidateDiscarded)){
                // An existing launch or terminal result is always observed, never silently replayed.
                if(resume&&!OwnerAlive(current)&&report?.State=="blocked"&&report.ActiveSince is {} since&&
                    DateTimeOffset.UtcNow<since.AddMinutes(request.CommandExecution.MaxTaskMinutes)){
                    TaskCommandAuthorization.Validate(TaskCommandAuthorization.Read(prior.Authorization)??throw new UnauthorizedAccessException("The original task grant is unavailable"));
                    if(File.Exists(Path.Combine(current,"cancel.json")))return await StatusAsync(root,control);
                    if(launch)Launch(current,prior,control,resuming:true);
                }
                return await StatusAsync(root,control,runnerSummary);
            }
            if(OwnerAlive(current)||report is null||report.State is not ("complete" or "cancelled" or "declined" or "blocked" or "time_budget_exhausted"))
                throw new InvalidOperationException("A managed task already owns this workspace; observe/cancel it before submitting another task");
        }
        if(resume)throw new InvalidOperationException("No matching managed task exists; do not fabricate a new resume");
        var snapshot=await GitEvidence.CaptureAsync(root,request.AllowedFiles);
        if(snapshot.ChangedFiles.Length!=0)throw new InvalidOperationException("A new managed task requires a clean Git working tree; preserve existing work");
        var taskId=Guid.NewGuid().ToString("N");var dir=Path.Combine(parent,taskId);
        if (runnerSummary) TaskCommandAuthorization.RequireNativeReceiptPath(dir);
        Directory.CreateDirectory(dir);
        var configBytes=JsonSerializer.SerializeToUtf8Bytes(config,ClientConfig.JsonOptions);File.WriteAllBytes(Path.Combine(dir,"worker-config.json"),configBytes);
        var auth=TaskCommandAuthorization.Prepare(dir,root,taskId,request,snapshot.Head);
        var dll=typeof(TaskAgentCoordinator).Assembly.Location;
        var job=new Job(taskId,root,AgentTaskRunner.RequestJson(request),hash,snapshot.Head,control??CanonicalStore.DefaultRoot,CanonicalStore.Hash(configBytes),dll,CanonicalStore.Hash(File.ReadAllBytes(dll)),auth);
        TaskCommandGate.WriteNew(Path.Combine(dir,"job.json"),job);
        Save(dir,new(taskId,"queued",false,0,null,DateTimeOffset.UtcNow));
        TaskCommandGate.WriteReplace(Path.Combine(parent,"current-job.json"),new{task_id=taskId});
        if(launch)Launch(dir,job,control,detachProtocol:runnerSummary);
        return await StatusAsync(root,control,runnerSummary);
    }
    private static void Launch(string dir,Job job,string? control,bool resuming=false,bool detachProtocol=false)
    {
        // Reserve before CreateProcess. Uncertain launch outcomes must be inspected, never retried.
        TaskCommandGate.WriteNew(Path.Combine(dir,resuming?"worker-resume-launch-intent.json":"worker-launch-intent.json"),new{job.TaskId,job.RuntimeHash,created_at=DateTimeOffset.UtcNow});
        var executable=Environment.ProcessPath??throw new InvalidOperationException("Host executable unavailable");
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,
            RedirectStandardOutput=detachProtocol,RedirectStandardError=detachProtocol};
        if(Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(job.RuntimeDll);
        start.ArgumentList.Add("agent-task-worker");start.ArgumentList.Add(dir);
        start.Environment["LOCALBRAIN_CLIENT_CONFIG"]=Path.Combine(dir,"worker-config.json");
        
        using var p=Process.Start(start)??throw new IOException("Managed worker did not start");p.StandardInput.Close();
        // The Runner worker persists its state in the job directory, independent of MCP stdio.
        if(detachProtocol){p.StandardOutput.Close();p.StandardError.Close();}
        // Worker writes the authoritative identity itself while holding its exclusive owner lock.
    }
    internal static async Task<object> StatusAsync(string root,string? control=null,bool runnerSummary=false)
    {
        var dir=CurrentDirectory(root,control);if(dir is null)return new{phase="managed_not_started",eligible_for_completion=false};
        var job=Read<Job>(Path.Combine(dir,"job.json"));var r=Read<Report>(Path.Combine(dir,"report.json"));var alive=OwnerAlive(dir);
        var phase=r.State;
        if(!alive&&phase is ("running" or "awaiting_authorization" or "waiting_for_writer"))phase="owner_absent_inspect_before_resume";
        var canonical=CanonicalStore.ReadLatest(control??CanonicalStore.DefaultRoot,HostTaskJournal.WorkspaceId(root));
        var canonicalPhase="not_started";var canonicalComplete=false;var candidateDiscarded=false;string? candidateHash=null;string? gatewayRequestId=null;
        if(canonical?.TaskId==job.TaskId){using var d=JsonDocument.Parse(canonical.StateJson);var s=d.RootElement;
            canonicalPhase=s.TryGetProperty("phase",out var p)?p.GetString()??"unknown":"unknown";
            if(canonicalPhase=="cancelled" && s.TryGetProperty("candidate_disposition",out var disposition) &&
                disposition.ValueKind==JsonValueKind.String && disposition.GetString()=="discarded"){phase="cancelled";candidateDiscarded=true;}
            if(canonicalPhase=="candidate_ready" && s.TryGetProperty("candidate_hash",out var candidate) && candidate.ValueKind==JsonValueKind.String)
                candidateHash=candidate.GetString();
            if(alive && canonicalPhase=="v3_running" && s.TryGetProperty("current_gateway_request_id",out var current) && current.ValueKind==JsonValueKind.String)
                gatewayRequestId=current.GetString();
            canonicalComplete=canonicalPhase=="complete"&&LocalValidationV2.FromCanonical(s)?.IsComplete==true;
        }
        var complete=r.EligibleForCompletion&&r.State=="complete"&&canonicalComplete;
        if(complete){var request=AgentTaskRequest.Parse(job.RequestJson);var snap=await GitEvidence.CaptureAsync(root,request.AllowedFiles);
            using var d=JsonDocument.Parse(canonical!.StateJson);var s=d.RootElement;
            complete=snap.Head==job.Head&&snap.UnexpectedFiles.Length==0&&s.GetProperty("review_diff_sha256").GetString()==AgentTaskRunner.HashTextForHost(snap.ReviewDiff)&&
                s.TryGetProperty("command_evidence_inventory",out var inventory)&&inventory.GetString()==TaskCommandGate.InventoryHash(control??CanonicalStore.DefaultRoot,job.TaskId)&&
                s.GetProperty("changed_files").EnumerateArray().Select(f=>f.GetString()).SequenceEqual(snap.ChangedFiles,StringComparer.OrdinalIgnoreCase)&&ValidateOutputs(control??CanonicalStore.DefaultRoot,job.TaskId);
        }
        if (runnerSummary)
        {
            using var state = canonical?.TaskId == job.TaskId ? JsonDocument.Parse(canonical.StateJson) : null;
            var request = AgentTaskRequest.Parse(job.RequestJson);
            var changed = state is not null && state.RootElement.TryGetProperty("changed_files", out var files)
                ? files.EnumerateArray().Select(f => f.GetString()).Where(f => f is not null &&
                    request.AllowedFiles.Contains(f, StringComparer.OrdinalIgnoreCase)).ToArray() : [];
            var diffHash = state is not null && state.RootElement.TryGetProperty("review_diff_sha256", out var hash)
                ? hash.GetString() : null;
            var validation = state is null ? null : LocalValidationV2.FromCanonical(state.RootElement);
            return new { task_id = job.TaskId, phase, canonical_phase = canonicalPhase, eligible_for_completion = complete,
                worker_alive = alive, base_commit = job.Head, changed_files = changed, diff_sha256 = diffHash,
                required_tests_passed = validation?.RequiredTestsPassed, local_review_passed = validation?.LocalReviewPassed,
                cancellation_requested = File.Exists(Path.Combine(dir, "cancel.json")),
                details_retained_locally = r.Reason is not null || !complete };
        }
        var commandRoot=Path.Combine(control??CanonicalStore.DefaultRoot,"command-jobs",job.TaskId);
        var commandDirs=Directory.Exists(commandRoot)?Directory.EnumerateDirectories(commandRoot).Take(1024).ToArray():[];
        var gatewayNotice=gatewayRequestId is null?null:await AgentV3.GatewayTaskNotice.ReadAsync(dir,job,gatewayRequestId);
        return new{task_id=job.TaskId,phase,canonical_phase=canonicalPhase,eligible_for_completion=complete,worker_alive=alive,
            actor_turns=Math.Max(r.ActorTurns,canonical?.TaskId==job.TaskId?(int)canonical.ActorTurns:0),command_count=commandDirs.Length,
            unsettled_command_intents=commandDirs.Count(d=>!File.Exists(Path.Combine(d,"receipt.json"))),cancellation_requested=File.Exists(Path.Combine(dir,"cancel.json")),
            reason=candidateDiscarded?"candidate_discarded_evidence_retained":r.Reason,candidate_hash=candidateHash,gateway_status=gatewayNotice is null?null:new{state=gatewayNotice.State,notice=gatewayNotice.Text},updated_at=r.UpdatedAt,active_since=r.ActiveSince,automatic_continuation=alive,individual_command_confirmations=false};
    }
    private static bool ValidateOutputs(string control,string task)
    {
        var root=Path.Combine(control,"command-jobs",task);if(!Directory.Exists(root))return true;
        foreach(var dir in Directory.EnumerateDirectories(root)){
            var p=Path.Combine(dir,"receipt.json");if(!File.Exists(p))return false;
            var r=Read<TaskCommandGate.Receipt>(p);
            foreach(var output in r.Outputs)if(!File.Exists(output.Path)||new FileInfo(output.Path).Length!=output.Bytes||CanonicalStore.Hash(File.ReadAllBytes(output.Path))!=output.Sha256)return false;
            foreach(var pair in new[]{("stdout.bin",r.StdoutHash),("stderr.bin",r.StderrHash)})if(!File.Exists(Path.Combine(dir,pair.Item1))||CanonicalStore.Hash(File.ReadAllBytes(Path.Combine(dir,pair.Item1)))!=pair.Item2)return false;
        }return true;
    }
    internal static async Task<object> CancelAsync(string root,string taskId,string? control=null,bool runnerSummary=false)
    {
        var parent=WorkspaceDirectory(root,control);CanonicalStore.GuardPath(parent);
        using(var submission=new FileStream(Path.Combine(parent,"submission.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)){
            var dir=CurrentDirectory(root,control)??throw new InvalidOperationException("No managed task");var job=Read<Job>(Path.Combine(dir,"job.json"));
            if(job.TaskId!=taskId || !string.Equals(Path.GetFullPath(job.Root),Path.GetFullPath(root),StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(job.ControlRoot),Path.GetFullPath(control??CanonicalStore.DefaultRoot),StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Cancellation must name the current exact task and workspace");
            var path=Path.Combine(dir,"cancel.json");if(!File.Exists(path))TaskCommandGate.WriteNew(path,new{task_id=taskId,requested_at=DateTimeOffset.UtcNow});
            if(!OwnerAlive(dir))TryDiscardCandidate(dir,job);
            return await StatusAsync(root,control,runnerSummary);
        }
    }

    // Canonical commits first; repeating cancel repairs a failed report write.
    // The submission lock is already held and prevents a concurrent new task.
    private static void TryDiscardCandidate(string dir,Job job)
    {
        var snapshot=CanonicalStore.ReadLatest(job.ControlRoot,HostTaskJournal.WorkspaceId(job.Root));
        if(snapshot?.TaskId!=job.TaskId)return;
        using(var parsed=JsonDocument.Parse(snapshot.StateJson)){
            var state=parsed.RootElement;
            var phase=state.TryGetProperty("phase",out var p)&&p.ValueKind==JsonValueKind.String?p.GetString():null;
            var discarded=phase=="cancelled" && state.TryGetProperty("candidate_disposition",out var disposition) && disposition.GetString()=="discarded";
            if(phase!="candidate_ready" && !discarded)return;
        }
        var workerPath=Path.Combine(dir,"worker.lock");CanonicalStore.GuardPath(workerPath);
        using var worker=new FileStream(workerPath,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        if(OwnerAlive(dir))throw new InvalidOperationException("Task owner is still alive; cancellation remains requested");
        using var store=new CanonicalStore(job.ControlRoot);
        var workspace=HostTaskJournal.WorkspaceId(job.Root);
        var task=store.LatestTask(workspace);
        if(task is null || task.TaskId!=job.TaskId || task.WorkspaceId!=workspace || task.RequestHash!=job.RequestHash)
            throw new UnauthorizedAccessException("Canonical candidate task identity changed");
        var stateNode=JsonNode.Parse(task.StateJson)?.AsObject()??throw new InvalidDataException("Invalid canonical task state");
        var phaseNow=stateNode["phase"]?.GetValue<string>();
        var alreadyDiscarded=phaseNow=="cancelled" && stateNode["candidate_disposition"]?.GetValue<string>()=="discarded";
        if(phaseNow!="candidate_ready" && !alreadyDiscarded)
            throw new InvalidOperationException("Candidate is not available for discard");
        var hash=stateNode["candidate_hash"]?.GetValue<string>();
        if(hash is null || hash.Length!=64 || !hash.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !store.HasCandidateArchive(job.TaskId,hash))
            throw new InvalidDataException("Candidate evidence is not bound to this task");
        _=store.ReadEvidence(hash); // Verifies the retained evidence content hash.
        if(stateNode["applied"]?.GetValue<bool>()!=false || stateNode["eligible_for_completion"]?.GetValue<bool>()!=false)
            throw new InvalidDataException("Candidate state is not an unapplied proposal");
        if(!alreadyDiscarded){
            stateNode["phase"]="cancelled";stateNode["candidate_disposition"]="discarded";
            var metadata=JsonSerializer.SerializeToElement(new{candidate_hash=hash,applied=false,evidence_retained=true});
            store.Checkpoint(new(job.TaskId,workspace,"windows_host","candidate_discard_"+hash,
                "pi_v3_candidate_discarded","host_decision","host_verified",[hash],metadata),stateNode.ToJsonString());
        }
        var report=Read<Report>(Path.Combine(dir,"report.json"));
        if(report.TaskId!=job.TaskId)throw new InvalidDataException("Managed report task identity changed");
        if(report.State!="cancelled" || report.EligibleForCompletion || report.Reason!="candidate_discarded_evidence_retained")
            Save(dir,report with{State="cancelled",EligibleForCompletion=false,Reason="candidate_discarded_evidence_retained",UpdatedAt=DateTimeOffset.UtcNow});
    }
    private static void Save(string dir,Report report)=>TaskCommandGate.WriteReplace(Path.Combine(dir,"report.json"),report);
    internal static async Task RunWorkerAsync(string dir,ClientConfig config)
    {
        CanonicalStore.GuardPath(dir);var job=Read<Job>(Path.Combine(dir,"job.json"));
        if(Path.GetFullPath(dir)!=Path.GetFullPath(Path.Combine(WorkspaceDirectory(job.Root,job.ControlRoot),job.TaskId)))throw new UnauthorizedAccessException("Managed job is outside its Host-owned workspace");
        using var owner=new FileStream(Path.Combine(dir,"worker.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var previousReport=Read<Report>(Path.Combine(dir,"report.json"));
        var identity=Path.Combine(dir,"worker-process.json");if(File.Exists(identity))File.Copy(identity,identity+"."+Guid.NewGuid().ToString("N")+".history");
        using(var self=Process.GetCurrentProcess())TaskCommandGate.WriteReplace(identity,new{pid=self.Id,birth=self.StartTime.ToUniversalTime().Ticks,job.TaskId});
        var request=AgentTaskRequest.Parse(job.RequestJson);var contract=request.CommandExecution!;DateTimeOffset? activeSince=previousReport.ActiveSince;var turns=previousReport.ActorTurns;
        using var stop=new CancellationTokenSource();
        var cancellation=Task.Run(async()=>{while(!stop.IsCancellationRequested){if(File.Exists(Path.Combine(dir,"cancel.json"))){stop.Cancel();break;}try{await Task.Delay(300,stop.Token);}catch(OperationCanceledException){break;}}});
        void Report(string state,string? reason=null,bool complete=false)=>Save(dir,new(job.TaskId,state,complete,turns,reason,DateTimeOffset.UtcNow,activeSince));
        var v3Attempt=false;
        try{
            if(!string.Equals(Path.GetFullPath(typeof(TaskAgentCoordinator).Assembly.Location),Path.GetFullPath(job.RuntimeDll),StringComparison.OrdinalIgnoreCase)||
                CanonicalStore.Hash(JsonSerializer.SerializeToUtf8Bytes(config,ClientConfig.JsonOptions))!=job.ConfigHash||
                CanonicalStore.Hash(File.ReadAllBytes(job.RuntimeDll))!=job.RuntimeHash||CanonicalStore.Hash(File.ReadAllBytes(Path.Combine(dir,"worker-config.json")))!=job.ConfigHash||
                AgentTaskRunner.RequestHash(request)!=job.RequestHash||!string.Equals(new ProjectAllowlist(config).Resolve(job.Root),job.Root,StringComparison.OrdinalIgnoreCase)||ProjectAllowlist.CurrentHead(job.Root)!=job.Head)
                throw new UnauthorizedAccessException("Managed task baseline/runtime/config changed before execution");
            Report("awaiting_authorization");var grant=TaskCommandAuthorization.Read(job.Authorization);
            if (grant is null && File.Exists(job.Authorization.Input+".launch-intent"))
                throw new InvalidOperationException("Existing native confirmation launch has no result; observe it without relaunching");
            using (var dialog = grant is null ? TaskCommandAuthorization.Launch(job.Authorization) : null)
            {
                while (grant is null)
                {
                    await Task.Delay(500, stop.Token);
                    if (File.Exists(Path.Combine(dir, "cancel.json"))) throw new OperationCanceledException(stop.Token);
                    grant = TaskCommandAuthorization.Read(job.Authorization);
                    if (grant is null && dialog?.HasExited == true)
                        grant = TaskCommandAuthorization.Read(job.Authorization) ??
                            throw new InvalidOperationException($"Native confirmation exited {dialog.ExitCode} without a receipt; task did not start");
                }
            }
            activeSince??=DateTimeOffset.UtcNow;var remaining=activeSince.Value.AddMinutes(contract.MaxTaskMinutes)-DateTimeOffset.UtcNow;
            if(remaining<=TimeSpan.Zero){Report("time_budget_exhausted","The original finite task budget expired");return;}stop.CancelAfter(remaining);
            using var authority=TaskCommandAuthorization.Enter(grant);
            // Wait for the existing logical writer before any task event or side effect.
            Directory.CreateDirectory(job.ControlRoot);
            while(true){stop.Token.ThrowIfCancellationRequested();try{using var lease=new FileStream(Path.Combine(job.ControlRoot,"writer.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);break;}
                catch(IOException){Report("waiting_for_writer");await Task.Delay(2000,stop.Token);}}
            var priorTask=CanonicalStore.ReadLatest(job.ControlRoot,HostTaskJournal.WorkspaceId(job.Root));
            if(config.V3Development?.Enabled==true && request.EffectiveRisk=="LOW"){
                if(priorTask?.TaskId==job.TaskId)throw new InvalidOperationException("Prior v3 attempt requires reconciliation; no automatic replay");
                v3Attempt=true;Report("running");
                await AgentV3.PiHostRunner.RunAsync(dir,job.Root,job.TaskId,job.Head,request,config,job.ControlRoot,stop.Token);
                Report("candidate_ready","Isolated proposal captured; review required; not applied");return;
            }
            var wsl=await WslWorkspace.ResolveAsync(new ProjectAllowlist(config),job.Root,stop.Token);
            var resume=priorTask?.TaskId==job.TaskId;var reviewRetries=0;
            while(true){
                Report("running");var result=await AgentTaskRunner.RunAsync(job.Root,wsl,request,config,resume:resume,token:stop.Token,controlPlaneRoot:job.ControlRoot);turns=result.ActorTurns;
                if(result.EligibleForCompletion){Report("complete",complete:true);break;}
                if(result.NoProgress||result.FalseVerified||result.TestMutatedWorktree||turns>=contract.MaxActorTurns){Report("blocked",result.FalseVerified?"false_completion_claim":result.NoProgress?"no_progress":"task_turn_budget_or_worktree_gate");break;}
                if(result.Phase=="awaiting_external_review"){
                    if(++reviewRetries>3){Report("blocked","external_review_unavailable");break;}await Task.Delay(TimeSpan.FromSeconds(30),stop.Token);
                }
                else if(result.Phase is not ("needs_work" or "handoff_ready" or "handoff_pending")){Report("blocked",result.Phase);break;}
                resume=true;
            }
        }catch(OperationCanceledException){if(v3Attempt)Report("blocked","v3 cancellation outcome requires reconciliation; prior evidence retained");
            else Report(File.Exists(Path.Combine(dir,"cancel.json"))?"cancelled":"time_budget_exhausted","Owned model/command work cancelled; prior intents and receipts retained");}
        catch(UnauthorizedAccessException e){Report("declined",e.Message);}
        catch(Exception e){Report("blocked",e.GetType().Name+": "+e.Message);}
        finally{stop.Cancel();await cancellation;}
    }
}
