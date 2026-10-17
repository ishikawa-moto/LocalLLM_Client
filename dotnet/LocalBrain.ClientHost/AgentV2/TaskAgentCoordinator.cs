using System.Diagnostics;
using System.Text.Json;

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
    internal static async Task<object> SubmitAsync(string root,AgentTaskRequest request,ClientConfig config,bool resume=false,string? control=null,bool launch=true)
    {
        if(request.CommandExecution is null)throw new InvalidDataException("Managed tasks require an explicit command_execution scope");
        var parent=WorkspaceDirectory(root,control);CanonicalStore.GuardPath(parent);Directory.CreateDirectory(parent);
        using var submission=new FileStream(Path.Combine(parent,"submission.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var current=CurrentDirectory(root,control);var hash=AgentTaskRunner.RequestHash(request);
        if(current is not null){
            var prior=Read<Job>(Path.Combine(current,"job.json"));
            var report=File.Exists(Path.Combine(current,"report.json"))?Read<Report>(Path.Combine(current,"report.json")):null;
            if(prior.RequestHash==hash){
                // An existing launch or terminal result is always observed, never silently replayed.
                if(resume&&!OwnerAlive(current)&&report?.State=="blocked"&&report.ActiveSince is {} since&&
                    DateTimeOffset.UtcNow<since.AddMinutes(request.CommandExecution.MaxTaskMinutes)){
                    TaskCommandAuthorization.Validate(TaskCommandAuthorization.Read(prior.Authorization)??throw new UnauthorizedAccessException("The original task grant is unavailable"));
                    if(File.Exists(Path.Combine(current,"cancel.json")))return await StatusAsync(root,control);
                    if(launch)Launch(current,prior,control,resuming:true);
                }
                return await StatusAsync(root,control);
            }
            if(OwnerAlive(current)||report is null||report.State is not ("complete" or "cancelled" or "declined" or "blocked" or "time_budget_exhausted"))
                throw new InvalidOperationException("A managed task already owns this workspace; observe/cancel it before submitting another task");
        }
        if(resume)throw new InvalidOperationException("No matching managed task exists; do not fabricate a new resume");
        var snapshot=await GitEvidence.CaptureAsync(root,request.AllowedFiles);
        if(snapshot.ChangedFiles.Length!=0)throw new InvalidOperationException("A new managed task requires a clean Git working tree; preserve existing work");
        var taskId=Guid.NewGuid().ToString("N");var dir=Path.Combine(parent,taskId);Directory.CreateDirectory(dir);
        var configBytes=JsonSerializer.SerializeToUtf8Bytes(config,ClientConfig.JsonOptions);File.WriteAllBytes(Path.Combine(dir,"worker-config.json"),configBytes);
        var auth=TaskCommandAuthorization.Prepare(dir,root,taskId,request,snapshot.Head);
        var dll=typeof(TaskAgentCoordinator).Assembly.Location;
        var job=new Job(taskId,root,AgentTaskRunner.RequestJson(request),hash,snapshot.Head,control??CanonicalStore.DefaultRoot,CanonicalStore.Hash(configBytes),dll,CanonicalStore.Hash(File.ReadAllBytes(dll)),auth);
        TaskCommandGate.WriteNew(Path.Combine(dir,"job.json"),job);
        Save(dir,new(taskId,"queued",false,0,null,DateTimeOffset.UtcNow));
        TaskCommandGate.WriteReplace(Path.Combine(parent,"current-job.json"),new{task_id=taskId});
        if(launch)Launch(dir,job,control);
        return await StatusAsync(root,control);
    }
    private static void Launch(string dir,Job job,string? control,bool resuming=false)
    {
        // Reserve before CreateProcess. Uncertain launch outcomes must be inspected, never retried.
        TaskCommandGate.WriteNew(Path.Combine(dir,resuming?"worker-resume-launch-intent.json":"worker-launch-intent.json"),new{job.TaskId,job.RuntimeHash,created_at=DateTimeOffset.UtcNow});
        var executable=Environment.ProcessPath??throw new InvalidOperationException("Host executable unavailable");
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true};
        if(Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(job.RuntimeDll);
        start.ArgumentList.Add("agent-task-worker");start.ArgumentList.Add(dir);
        start.Environment["LOCALBRAIN_CLIENT_CONFIG"]=Path.Combine(dir,"worker-config.json");
        
        using var p=Process.Start(start)??throw new IOException("Managed worker did not start");p.StandardInput.Close();
        // Worker writes the authoritative identity itself while holding its exclusive owner lock.
    }
    internal static async Task<object> StatusAsync(string root,string? control=null)
    {
        var dir=CurrentDirectory(root,control);if(dir is null)return new{phase="managed_not_started",eligible_for_completion=false};
        var job=Read<Job>(Path.Combine(dir,"job.json"));var r=Read<Report>(Path.Combine(dir,"report.json"));var alive=OwnerAlive(dir);
        var phase=r.State;
        if(!alive&&phase is ("running" or "awaiting_authorization" or "waiting_for_writer"))phase="owner_absent_inspect_before_resume";
        var canonical=CanonicalStore.ReadLatest(control??CanonicalStore.DefaultRoot,HostTaskJournal.WorkspaceId(root));
        var canonicalPhase="not_started";var canonicalComplete=false;
        if(canonical?.TaskId==job.TaskId){using var d=JsonDocument.Parse(canonical.StateJson);var s=d.RootElement;
            canonicalPhase=s.TryGetProperty("phase",out var p)?p.GetString()??"unknown":"unknown";
            canonicalComplete=canonicalPhase=="complete"&&LocalValidationV2.FromCanonical(s)?.IsComplete==true;
        }
        var complete=r.EligibleForCompletion&&r.State=="complete"&&canonicalComplete;
        if(complete){var request=AgentTaskRequest.Parse(job.RequestJson);var snap=await GitEvidence.CaptureAsync(root,request.AllowedFiles);
            using var d=JsonDocument.Parse(canonical!.StateJson);var s=d.RootElement;
            complete=snap.Head==job.Head&&snap.UnexpectedFiles.Length==0&&s.GetProperty("review_diff_sha256").GetString()==AgentTaskRunner.HashTextForHost(snap.ReviewDiff)&&
                s.TryGetProperty("command_evidence_inventory",out var inventory)&&inventory.GetString()==TaskCommandGate.InventoryHash(control??CanonicalStore.DefaultRoot,job.TaskId)&&
                s.GetProperty("changed_files").EnumerateArray().Select(f=>f.GetString()).SequenceEqual(snap.ChangedFiles,StringComparer.OrdinalIgnoreCase)&&ValidateOutputs(control??CanonicalStore.DefaultRoot,job.TaskId);
        }
        var commandRoot=Path.Combine(control??CanonicalStore.DefaultRoot,"command-jobs",job.TaskId);
        var commandDirs=Directory.Exists(commandRoot)?Directory.EnumerateDirectories(commandRoot).Take(1024).ToArray():[];
        return new{task_id=job.TaskId,phase,canonical_phase=canonicalPhase,eligible_for_completion=complete,worker_alive=alive,
            actor_turns=Math.Max(r.ActorTurns,canonical?.TaskId==job.TaskId?(int)canonical.ActorTurns:0),command_count=commandDirs.Length,
            unsettled_command_intents=commandDirs.Count(d=>!File.Exists(Path.Combine(d,"receipt.json"))),cancellation_requested=File.Exists(Path.Combine(dir,"cancel.json")),
            reason=r.Reason,updated_at=r.UpdatedAt,active_since=r.ActiveSince,automatic_continuation=alive,individual_command_confirmations=false};
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
    internal static async Task<object> CancelAsync(string root,string taskId,string? control=null)
    {
        var dir=CurrentDirectory(root,control)??throw new InvalidOperationException("No managed task");var job=Read<Job>(Path.Combine(dir,"job.json"));
        if(job.TaskId!=taskId)throw new UnauthorizedAccessException("Cancellation must name the current exact task");
        var path=Path.Combine(dir,"cancel.json");if(!File.Exists(path))TaskCommandGate.WriteNew(path,new{task_id=taskId,requested_at=DateTimeOffset.UtcNow});
        return await StatusAsync(root,control);
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
        try{
            if(!string.Equals(Path.GetFullPath(typeof(TaskAgentCoordinator).Assembly.Location),Path.GetFullPath(job.RuntimeDll),StringComparison.OrdinalIgnoreCase)||
                CanonicalStore.Hash(JsonSerializer.SerializeToUtf8Bytes(config,ClientConfig.JsonOptions))!=job.ConfigHash||
                CanonicalStore.Hash(File.ReadAllBytes(job.RuntimeDll))!=job.RuntimeHash||CanonicalStore.Hash(File.ReadAllBytes(Path.Combine(dir,"worker-config.json")))!=job.ConfigHash||
                AgentTaskRunner.RequestHash(request)!=job.RequestHash||!string.Equals(new ProjectAllowlist(config).Resolve(job.Root),job.Root,StringComparison.OrdinalIgnoreCase)||ProjectAllowlist.CurrentHead(job.Root)!=job.Head)
                throw new UnauthorizedAccessException("Managed task baseline/runtime/config changed before execution");
            Report("awaiting_authorization");var grant=TaskCommandAuthorization.Read(job.Authorization);
            if(grant is null){if(File.Exists(job.Authorization.Input+".launch-intent"))throw new InvalidOperationException("Existing native confirmation launch has no result; observe it without relaunching");TaskCommandAuthorization.Launch(job.Authorization);}
            while(grant is null){await Task.Delay(500,stop.Token);grant=TaskCommandAuthorization.Read(job.Authorization);}
            activeSince??=DateTimeOffset.UtcNow;var remaining=activeSince.Value.AddMinutes(contract.MaxTaskMinutes)-DateTimeOffset.UtcNow;
            if(remaining<=TimeSpan.Zero){Report("time_budget_exhausted","The original finite task budget expired");return;}stop.CancelAfter(remaining);
            using var authority=TaskCommandAuthorization.Enter(grant);
            var wsl=await WslWorkspace.ResolveAsync(new ProjectAllowlist(config),job.Root,stop.Token);
            // Wait for the existing logical writer before any task event or side effect.
            Directory.CreateDirectory(job.ControlRoot);
            while(true){stop.Token.ThrowIfCancellationRequested();try{using var lease=new FileStream(Path.Combine(job.ControlRoot,"writer.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);break;}
                catch(IOException){Report("waiting_for_writer");await Task.Delay(2000,stop.Token);}}
            var priorTask=CanonicalStore.ReadLatest(job.ControlRoot,HostTaskJournal.WorkspaceId(job.Root));
            var resume=priorTask?.TaskId==job.TaskId;var reviewRetries=0;
            while(true){
                Report("running");var result=await AgentTaskRunner.RunAsync(job.Root,wsl,request,config,resume:resume,token:stop.Token,controlPlaneRoot:job.ControlRoot);turns=result.ActorTurns;
                if(result.EligibleForCompletion){Report("complete",complete:true);break;}
                if(result.NoProgress||result.FalseVerified||result.TestMutatedWorktree||turns>=contract.MaxActorTurns){Report("blocked",result.FalseVerified?"false_completion_claim":result.NoProgress?"laya_no_progress":"task_turn_budget_or_worktree_gate");break;}
                if(result.Phase=="awaiting_external_review"){
                    if(++reviewRetries>3){Report("blocked","external_review_unavailable");break;}await Task.Delay(TimeSpan.FromSeconds(30),stop.Token);
                }
                else if(result.Phase is not ("needs_work" or "handoff_ready" or "handoff_pending")){Report("blocked",result.Phase);break;}
                resume=true;
            }
        }catch(OperationCanceledException){Report(File.Exists(Path.Combine(dir,"cancel.json"))?"cancelled":"time_budget_exhausted","Owned model/command work cancelled; prior intents and receipts retained");}
        catch(UnauthorizedAccessException e){Report("declined",e.Message);}
        catch(Exception e){Report("blocked",e.GetType().Name+": "+e.Message);}
        finally{stop.Cancel();await cancellation;}
    }
}
