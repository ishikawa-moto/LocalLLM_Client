using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

internal static class ContinueRecoveryKnowledge
{
    internal static async Task<string?> PrepareAsync(TaskAgentCoordinator.Job job, AgentTaskRequest request,
        ClientConfig config, CancellationToken token)
    {
        if (request.ContinueRecovery is not { Knowledge: { } scope } intent) return null;
        if (!string.Equals(job.ControlRoot, CanonicalStore.DefaultRoot, StringComparison.OrdinalIgnoreCase)) return "fixture_not_dispatched";

        string? candidate = null;
        string phase = "knowledge_preparation_failed";
        try
        {
            using var store = new CanonicalStore(job.ControlRoot);
            using var journal = new HostTaskJournal(store, job.Root, job.TaskId, request, job.Head);
            string Record(string state)
            {
                phase = state;
                journal.Record("continue_recovery_knowledge", new { request_id = intent.DecisionId, phase, candidate_hash = candidate }, "host_verified");
                return phase;
            }
            if (AutomaticDecisionPolicy.Load(config, store.Root) is null)
                return Record("review_policy_unavailable");

            var promotion = store.EventIds(journal.TaskId).Select(id => store.ReadTaskEvent(journal.TaskId, id))
                .LastOrDefault(row => row.Input is { EventType: "codex_rescue_promoted", Producer: "windows_host", VerificationStatus: "host_verified" });
            if (promotion is null) return Record("no_external_repair_promoted");
            var ticket = DecisionProtocol.HashText(DecisionProtocol.Text(promotion.Input.Metadata, "ticket_hash", 64));
            var validation = DecisionProtocol.HashText(DecisionProtocol.Text(promotion.Input.Metadata, "validation_hash", 64));
            var learning = new DifferentialLearning(journal);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            candidate = await learning.ExtractAsync(ticket, validation, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            var body = learning.DraftAdoptionBody(candidate, compact: true);
            var proposal = DecisionProposalMcp.Request(JsonSerializer.Serialize(new {
                request_id = intent.DecisionId, project = scope.Project, title = "Verified LocalBrain recovery", body,
                references = scope.References
            }), usesContent: false);
            // The long-lived MCP decision worker drains the durable outbox once the task releases Canonical ownership.
            await using var automation = new DecisionAutomation(config);
            await automation.EnqueueAsync(proposal, "proposal", startWorker: false);
            return Record("queued_for_review");
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or
            InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or OperationCanceledException or HttpRequestException or Microsoft.Data.Sqlite.SqliteException)
        {
            // The managed repair has already passed its gates. Report knowledge failure separately.
            return phase == "knowledge_preparation_failed" ? "knowledge_" + error.GetType().Name : phase;
        }
    }
}
