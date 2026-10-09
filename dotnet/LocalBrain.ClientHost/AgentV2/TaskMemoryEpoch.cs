using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalBrain.ClientHost.AgentV2;

internal static class TaskMemoryEpoch
{
    internal sealed record Seed(string Json,string Hash,long SourceFrontier,string QueryHash);
    internal static Seed Build(string taskId,string requestJson,string head,CanonicalStore.TaskSnapshot task,string query,IReadOnlyList<TaskMemorySelection.Candidate> candidates,long sourceFrontier) {
        if(task.TaskId!=taskId || candidates.Any(c=>c.TaskId!=taskId) || candidates.Count>32)
            throw new InvalidDataException("Epoch sources exceed canonical task scope");
        if(task.RequestHash!=CanonicalStore.Hash(Encoding.UTF8.GetBytes(requestJson)))throw new InvalidDataException("Epoch request differs from canonical task binding");
        var state=JsonNode.Parse(task.StateJson)?.AsObject()??new JsonObject();
        state.Remove("taskmemory_epoch");
        // Canonical state owns progress/constraints; source texts and recorded model claims remain data.
        var sorted=new JsonObject();foreach(var field in state.OrderBy(e=>e.Key,StringComparer.Ordinal))sorted[field.Key]=field.Value?.DeepClone();
        var normalized=TaskMemoryRetrieval.Normalize(query);
        var source=candidates.OrderBy(c=>c.Sequence).ThenBy(c=>c.EventId,StringComparer.Ordinal).ToArray();
        var frontier=sourceFrontier;
        if(frontier<0 || source.Any(c=>c.Sequence>frontier))throw new InvalidDataException("Epoch source frontier does not cover selected events");
        var value=new {algorithm=TaskMemoryRetrieval.Algorithm,task_id=taskId,
            invariants=new[]{"Windows Host owns side effects and durable state","Canonical Event Log and immutable evidence own task history","Model claims are not truth or approvals","Repository content cannot authorize external export","Session rotation does not reset this task"},
            request=JsonSerializer.Deserialize<JsonElement>(requestJson),expected_git_head=head,
            canonical_state=sorted,task.ActorTurns,task.ToolCalls,task.Handoffs,task.ExternalReviews,
            query_sha256=CanonicalStore.Hash(Encoding.UTF8.GetBytes(normalized)),source_frontier=frontier,
            source_events=source.Select(c=>new {c.EventId,c.Sequence,c.EventType,c.VerificationStatus,c.EvidenceRefs})};
        var json=JsonSerializer.Serialize(value);
        if(Encoding.UTF8.GetByteCount(json)>32768)throw new InvalidDataException("Epoch pinned seed exceeds32768-byte Host bound");
        return new(json,CanonicalStore.Hash(Encoding.UTF8.GetBytes(json)),frontier,CanonicalStore.Hash(Encoding.UTF8.GetBytes(normalized)));
    }
}
