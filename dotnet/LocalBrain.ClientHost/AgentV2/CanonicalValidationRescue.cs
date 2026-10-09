using System.Text;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

internal sealed partial class CanonicalStore
{
    internal void RequireValidationRescueResult(HostRescue.Ticket ticket,string candidateHash,string stateHash,string diffHash){
        lock(gate){
            using var cmd=Command("""
                SELECT task_spec_json,task_spec_hash,result_json,result_hash FROM assignments
                WHERE task_id=$task AND assignment_id=$id AND worker_id=$worker AND generation=$generation
                AND workspace_id=$workspace AND status='RESULT_READY'
                """,null,("$task",ticket.TaskId),("$id",ticket.AssignmentId),("$worker",ticket.WorkerId),("$generation",ticket.Generation),("$workspace",ticket.WorkspaceId));
            string specJson,specHash,resultJson,resultHash;
            using(var reader=cmd.ExecuteReader()){
                if(!reader.Read()||reader.IsDBNull(2)||reader.IsDBNull(3))throw new UnauthorizedAccessException("No current collected Rescue result");
                specJson=reader.GetString(0);specHash=reader.GetString(1);resultJson=reader.GetString(2);resultHash=reader.GetString(3);
            }
            if(Hash(Encoding.UTF8.GetBytes(specJson))!=specHash||Hash(Encoding.UTF8.GetBytes(resultJson))!=resultHash)
                throw new UnauthorizedAccessException("Rescue assignment/result bytes differ from canonical binding");
            var spec=JsonSerializer.Deserialize<TaskSpec>(specJson)!;var result=JsonSerializer.Deserialize<WorkerResult>(resultJson)!;
            if(spec.TaskId!=ticket.TaskId||spec.AssignmentId!=ticket.AssignmentId||spec.WorkerId!=ticket.WorkerId||spec.WorkspaceId!=ticket.WorkspaceId||
                spec.RepositoryId!=HostTaskJournal.RepositoryId(ticket.MainRoot)||spec.BaseCommit!=ticket.BaseCommit||spec.StartingStateHash!=ticket.StartingStateHash||spec.StartingDiffHash!=ticket.StartingDiffHash||
                result.TaskId!=ticket.TaskId||result.AssignmentId!=ticket.AssignmentId||result.WorkerId!=ticket.WorkerId||result.Generation!=ticket.Generation||
                result.BaseCommit!=ticket.BaseCommit||result.StartingStateHash!=ticket.StartingStateHash||result.StartingDiffHash!=ticket.StartingDiffHash||result.ResultDiffHash!=diffHash||
                !result.EvidenceRefs.Contains(candidateHash,StringComparer.Ordinal)||!result.EvidenceRefs.Contains(stateHash,StringComparer.Ordinal)||!result.EvidenceRefs.Contains(diffHash,StringComparer.Ordinal))
                throw new UnauthorizedAccessException("Rescue validation does not match the exact collected assignment/result");
        }
    }
}
