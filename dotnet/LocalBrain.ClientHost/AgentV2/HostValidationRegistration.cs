using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

// Administrative Windows Host entry, not a model/MCP operation.
internal static class HostValidationRegistration
{
    internal sealed record Result(string TaskId, string PolicyHash, bool CreatedRegistrationTask);
    internal static async Task<Result> RegisterAsync(CanonicalStore store,string root,HostValidationPolicy.Policy policy,CancellationToken token=default)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Publisher registration belongs to Windows Host");
        root=Path.GetFullPath(root);CanonicalStore.GuardPath(root);
        if(policy.RepositoryId!=HostTaskJournal.RepositoryId(root))throw new UnauthorizedAccessException("Publisher repository does not match registered workspace");
        var stateDirectory=Path.Combine(root,".localbrain");CanonicalStore.GuardPath(stateDirectory);Directory.CreateDirectory(stateDirectory);
        var lockPath=Path.Combine(stateDirectory,"active.lock");CanonicalStore.GuardPath(lockPath);
        using var workspaceLease=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var current=store.LatestTask(HostTaskJournal.WorkspaceId(root));
        var legacy=Path.Combine(stateDirectory,"task-state.json");CanonicalStore.GuardPath(legacy);
        if(current is null&&File.Exists(legacy))
            throw new InvalidOperationException("Import the existing legacy checkpoint through ordinary Agent resume before publisher registration; registration cannot replace its owner");
        var request=current is null?AgentTaskRequest.Parse("""
            {"requirement":"Register Host-owned .NET validation inputs","acceptance_criteria":["Publisher registration is retained in Host canonical state"],"risk":"LOW","approved_high_risk":false,"allowed_files":["global.json"],"required_tests":[{"kind":"git_diff_check"}]}
            """):AgentTaskRequest.Parse(store.TaskRequest(current.TaskId));
        var git=await GitEvidence.CaptureAsync(root,request.AllowedFiles,token);
        var taskId=current?.TaskId??Guid.NewGuid().ToString("N");
        using var journal=new HostTaskJournal(store,root,taskId,request,git.Head);
        try{
            var hash=HostValidationPolicy.Register(journal,policy);
            if(current is null)journal.Checkpoint(new{task_id=taskId,phase="host_registration",git_head=git.Head,request_hash=AgentTaskRunner.RequestHash(request),actor_turns=0});
            return new(taskId,hash,current is null);
        }catch(Exception error){
            journal.Record("validation_publisher_registration_failed",new{reason=error.GetType().Name},"host_verified");
            if(current is null)journal.Checkpoint(new{task_id=taskId,phase="host_registration_failed",git_head=git.Head,request_hash=AgentTaskRunner.RequestHash(request),actor_turns=0});
            throw;
        }
    }
}
