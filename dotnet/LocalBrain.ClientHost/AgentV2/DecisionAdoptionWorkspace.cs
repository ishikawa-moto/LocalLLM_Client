using System.Text;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

// Administrative owner only. The caller must resolve the registered root and
// require an interactive human terminal before entering this path.
internal static class DecisionAdoptionWorkspace
{
    internal sealed record Owner(HostTaskJournal Journal,string TaskId,bool Administrative);
    internal static Owner Open(CanonicalStore store,string root,string requestId,string proposalHash,string currentHead)
    {
        DecisionProtocol.Id(requestId);DecisionProtocol.HashText(proposalHash);
        CanonicalStore.GuardPath(root);CanonicalStore.GuardPath(Path.Combine(root,".localbrain","task-state.json"));
        var ws=HostTaskJournal.WorkspaceId(root);var current=store.LatestTask(ws);
        if(current is null && File.Exists(Path.Combine(root,".localbrain","task-state.json")))
            throw new InvalidOperationException("Existing legacy task must be reconciled through its ordinary Host resume; adoption cannot replace it");
        var administrative=current is null;
        if(current is not null){
            using var oldState=JsonDocument.Parse(current.StateJson);
            var s=oldState.RootElement;
            if(s.TryGetProperty("administrative_purpose",out var purpose)&&purpose.GetString()=="decision_adoption"){
                administrative=true;
                if(s.GetProperty("adoption_request_id").GetString()==requestId){
                    if(s.GetProperty("bound_proposal_hash").GetString()!=proposalHash)throw new InvalidDataException("Existing adoption task is bound to different proposal bytes");
                }else {current=null;administrative=true;}
            }
        }
        var taskId=current?.TaskId??"adopt_"+CanonicalStore.Hash(Encoding.UTF8.GetBytes(ws+"\n"+requestId));
        // Existing administrative registration uses this request schema too.
        // Compatibility file/test fields are never sent to an Actor or executor.
        var request=current is null?AgentTaskRequest.Parse(JsonSerializer.Serialize(new{
            requirement="Record explicit SecondBrain adoption approval only: "+requestId,
            acceptance_criteria=new[]{"Bound proposal "+proposalHash,"No Actor, repository mutation or completion qualification"},
            risk="LOW",approved_high_risk=false,allowed_files=new[]{"global.json"},required_tests=new[]{new{kind="git_diff_check"}}
        })):AgentTaskRequest.Parse(store.TaskRequest(current.TaskId));
        var head=currentHead;
        if(current is not null){using var state=JsonDocument.Parse(current.StateJson);head=state.RootElement.GetProperty("git_head").GetString()??throw new InvalidDataException("Canonical task HEAD missing");}
        var journal=new HostTaskJournal(store,root,taskId,request,head);
        try {
            if(current is null)journal.Checkpoint(new{task_id=taskId,phase="host_adoption",administrative_purpose="decision_adoption",adoption_request_id=requestId,
                bound_proposal_hash=proposalHash,adoption_status="PENDING_EXPLICIT_KNOWLEDGE_ADOPTION_CONFIRMATION",git_head=head,request_hash=AgentTaskRunner.RequestHash(request),actor_turns=0,eligible_for_completion=false});
        }catch {journal.Dispose();throw;}
        return new(journal,taskId,administrative);
    }
}
