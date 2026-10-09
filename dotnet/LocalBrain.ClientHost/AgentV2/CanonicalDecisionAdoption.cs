using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

internal sealed partial class CanonicalStore
{
    internal sealed record DecisionApprovalRow(string IntentHash, string RequestHash, long ExpiresAt, bool Reserved, string? ResultHash, int? ResultStatus = null);
    private readonly HashSet<string> activeDecisionDispatches = new(StringComparer.Ordinal);
    internal TaskSnapshot ResolveAdoptionOwner(string workspaceId,string identity,bool approval)
    {
        lock(gate)
        {
            using var command=approval
                ?Command("SELECT task_id FROM decision_adoption_approvals WHERE approval_id=$id;",null,("$id",DecisionProtocol.Id(identity)))
                :Command("SELECT DISTINCT e.task_id FROM events e JOIN event_evidence x ON e.event_id=x.event_id WHERE x.hash=$hash AND e.event_type='decision_adoption_prepared' AND e.producer='windows_host';",null,("$hash",DecisionProtocol.HashText(identity)));
            using var rows=command.ExecuteReader();
            if(!rows.Read())throw new UnauthorizedAccessException("No canonical adoption owner");var owner=rows.GetString(0);
            if(rows.Read())throw new InvalidDataException("Ambiguous adoption owner");rows.Close();
            var task=GetTask(owner)??throw new InvalidDataException("Adoption owner task is missing");
            if(task.WorkspaceId!=workspaceId)throw new UnauthorizedAccessException("Adoption belongs to another workspace");
            if(approval)_=DecisionApproval(owner,identity);else _=new DecisionAdoption(this,owner).DescribeIntent(identity);
            return task;
        }
    }
    internal void RecordDecisionApproval(EventInput input, string id, string intentHash, string requestHash, long expiresAt) {
        CommitEvent(input, null, 0, 0, 0, 0, tx => Execute("INSERT INTO decision_adoption_approvals VALUES($id,$task,$intent,$request,$expires,0,NULL);", tx,
            ("$id", id), ("$task", input.TaskId), ("$intent", intentHash), ("$request", requestHash), ("$expires", expiresAt)));
    }
    internal DecisionApprovalRow DecisionApproval(string taskId, string approvalId) {
        lock (gate) {
            using var cmd = Command("SELECT intent_hash,request_hash,expires_at,reserved,result_hash FROM decision_adoption_approvals WHERE approval_id=$id AND task_id=$task;", null, ("$id", approvalId), ("$task", taskId));
            using var rows = cmd.ExecuteReader();
            if (!rows.Read()) throw new UnauthorizedAccessException("Approval is not attached to this task");
            var value = new DecisionApprovalRow(rows.GetString(0), rows.GetString(1), rows.GetInt64(2), rows.GetInt32(3) == 1, rows.IsDBNull(4) ? null : rows.GetString(4));
            rows.Close();
            var registered = EventIds(taskId).Select(id => ReadTaskEvent(taskId, id)).SingleOrDefault(e => e.Input.Producer == "windows_host" &&
                e.Input.EventType == "decision_adoption_human_approved" && e.Input.Metadata.GetProperty("approval_id").GetString() == approvalId);
            if (registered is null || registered.Input.WorkerId is not null || registered.Input.ProducerEventId is not null ||
                registered.Input.VerificationStatus != "human_explicit" || !registered.Input.EvidenceRefs.SequenceEqual(new[] { value.IntentHash, value.RequestHash }, StringComparer.Ordinal) ||
                registered.Input.Metadata.GetProperty("confirmation_scope").GetString() != "EXACT_SECOND_BRAIN_DECISION_ADOPTION" ||
                string.IsNullOrWhiteSpace(registered.Input.Metadata.GetProperty("human_actor").GetString())) throw new UnauthorizedAccessException("Approval provenance missing or changed");
            var events = EventIds(taskId).Select(id => ReadTaskEvent(taskId, id)).ToArray();
            var reservation = events.SingleOrDefault(e => e.Input.Producer == "windows_host" && e.Input.EventType == "decision_adoption_dispatch_reserved" && e.Input.Metadata.GetProperty("approval_id").GetString() == approvalId);
            if (value.Reserved != (reservation is not null) || (reservation is not null && !reservation.Input.EvidenceRefs.SequenceEqual(new[] { value.IntentHash, value.RequestHash }, StringComparer.Ordinal))) throw new InvalidDataException("Reservation ledger/event drift");
            var result = events.SingleOrDefault(e => e.Input.Producer == "windows_host" && e.Input.EventType == "decision_adoption_result_verified" && e.Input.Metadata.GetProperty("approval_id").GetString() == approvalId);
            if ((value.ResultHash is null) != (result is null) || (result is not null &&
                (!result.Input.EvidenceRefs.SequenceEqual(new[] { value.RequestHash, value.ResultHash! }, StringComparer.Ordinal) || result.Input.Metadata.GetProperty("result_hash").GetString() != value.ResultHash))) throw new InvalidDataException("Result ledger/event drift");
            return value with { ResultStatus = result?.Input.Metadata.GetProperty("http_status").GetInt32() };
        }
    }
    internal void ReserveDecision(EventInput input, string approvalId, string requestHash) {
        lock (gate) {
            var row = DecisionApproval(input.TaskId, approvalId);
            if (row.RequestHash != requestHash || (!row.Reserved && row.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())) throw new UnauthorizedAccessException("Expired or changed decision reservation");
            CommitEvent(input, null, 0, 0, 0, 0, tx => Execute("UPDATE decision_adoption_approvals SET reserved=1 WHERE approval_id=$id AND task_id=$task AND request_hash=$hash;", tx,
                ("$id", approvalId), ("$task", input.TaskId), ("$hash", requestHash)));
        }
    }
    internal void FinishDecision(EventInput input, string approvalId, string resultHash) {
        lock (gate) {
            var row = DecisionApproval(input.TaskId, approvalId);
            if (!row.Reserved || (row.ResultHash is not null && row.ResultHash != resultHash)) throw new UnauthorizedAccessException("Unreserved or conflicting adoption result");
            CommitEvent(input, null, 0, 0, 0, 0, tx => Execute("UPDATE decision_adoption_approvals SET result_hash=$result WHERE approval_id=$id;", tx, ("$result", resultHash), ("$id", approvalId)));
        }
    }
    private sealed class DecisionDispatchLease(CanonicalStore owner, string id) : IDisposable {
        public void Dispose() { lock (owner.gate) owner.activeDecisionDispatches.Remove(id); }
    }
    internal IDisposable AcquireDecisionDispatch(string id) {
        lock (gate) {
            if (!activeDecisionDispatches.Add(id)) throw new UnauthorizedAccessException("Decision already dispatching");
            return new DecisionDispatchLease(this, id);
        }
    }
    private const string DecisionAdoptionSchema = """
        CREATE TABLE IF NOT EXISTS decision_adoption_approvals(approval_id TEXT PRIMARY KEY,task_id TEXT NOT NULL REFERENCES tasks,intent_hash TEXT NOT NULL,request_hash TEXT NOT NULL UNIQUE,expires_at INTEGER NOT NULL,reserved INTEGER NOT NULL CHECK(reserved IN(0,1)),result_hash TEXT);
        CREATE TRIGGER IF NOT EXISTS decision_approval_identity BEFORE UPDATE ON decision_adoption_approvals WHEN NEW.approval_id!=OLD.approval_id OR NEW.task_id!=OLD.task_id OR NEW.intent_hash!=OLD.intent_hash OR NEW.request_hash!=OLD.request_hash OR NEW.expires_at!=OLD.expires_at OR NEW.reserved<OLD.reserved OR (OLD.result_hash IS NOT NULL AND (NEW.result_hash IS NULL OR NEW.result_hash!=OLD.result_hash)) BEGIN SELECT RAISE(ABORT,'immutable decision approval'); END;
        CREATE TRIGGER IF NOT EXISTS decision_approval_retention BEFORE DELETE ON decision_adoption_approvals BEGIN SELECT RAISE(ABORT,'retained decision approval'); END;
        """;
}
