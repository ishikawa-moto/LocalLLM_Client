using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

namespace LocalBrain.ClientHost.AgentV3;

// Experimental and unintegrated. The caller authenticates and authorizes the task,
// and supplies a fresh task-bound process and exclusive streams for each attempt.
// Separate wrappers around shared streams are not detected. Cancellation does not
// prove the process stopped; cancellation responsiveness depends on the streams.
// Prompts and raw observations can contain secrets; access, retention, deletion and
// safe rendering are external obligations. Hashes do not provide confidentiality.
// Results are untrusted observations, not task success or authority. Unknown event
// types are recorded but do not establish completion. Evidence and events are
// separate writes; failures after reservation can orphan evidence or a reservation.
// A reservation commit acknowledgement failure is ambiguous and fails closed.
// Parser or response-envelope failures before the observer may leave only unknown.
// Synchronous store calls
// cannot guarantee immediate cancellation cutoff. A lost result acknowledgement can
// leave both result and unknown markers. Unknown or orphaned starts do not authorize
// automatic retries. Attempt identifiers are scoped to each task and producer;
// distinct attempts on the same task require external coordination. Prepared prompt
// records do not prove transmission or acceptance. This adapter does not change task
// state or approve work.
internal sealed class PiCanonicalTurn
{
    private const int MaxObservations = 256;
    private const int MaxObservationBytes = 1024 * 1024;
    private readonly CanonicalStore store;

    internal PiCanonicalTurn(CanonicalStore store) => this.store = store ?? throw new ArgumentNullException(nameof(store));

    internal sealed record Result(bool CompletedWithoutModelError, bool ToolErrorObserved);

    // Use the existing Host task grant; this does not launch Pi, establish
    // process ownership, approve changes or authorize a production update.
    internal static Task<Result> RunAuthorizedAsync(HostTaskJournal journal, AgentTaskRequest request,
        PiRpcTurn turn, string prompt, Guid attemptId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(request);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(HostTaskJournal.Current, journal))
            throw new UnauthorizedAccessException("Pi execution requires the current Host task.");
        TaskCommandAuthorization.Require(journal, request);
        if (request.EffectiveRisk == "HIGH" && !journal.HasApproval)
            throw new UnauthorizedAccessException("High-risk task approval is unavailable or expired.");
        if (!StringComparer.OrdinalIgnoreCase.Equals(ProjectAllowlist.CurrentHead(journal.WindowsRoot), journal.ExpectedHead))
            throw new UnauthorizedAccessException("The authorized Git baseline changed.");
        return new PiCanonicalTurn(journal.Canonical).RunAsync(journal.TaskId,
            HostTaskJournal.WorkspaceId(journal.WindowsRoot), turn, prompt, attemptId, token);
    }

    internal async Task<Result> RunAsync(string taskId, string workspaceId, PiRpcTurn turn, string prompt, Guid attemptId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (string.IsNullOrEmpty(prompt) || Encoding.UTF8.GetByteCount(prompt) > 100000)
            throw new ArgumentException("Invalid prompt length.", nameof(prompt));
        if (attemptId == Guid.Empty) throw new ArgumentException("Attempt UUID must not be empty.", nameof(attemptId));
        token.ThrowIfCancellationRequested();
        var task = store.GetTask(taskId);
        if (task is null || !StringComparer.Ordinal.Equals(task.WorkspaceId, workspaceId))
            throw new UnauthorizedAccessException("Task or workspace is invalid.");

        string run = attemptId.ToString("N");
        var reservation = store.Append(Input("reservation", "pi_v3_attempt_reserved", []));
        if (reservation.Duplicate) throw new InvalidOperationException("This attempt has already been reserved.");
        string promptHash;
        CanonicalStore.EventReceipt start;
        var outcome = new PiRunOutcome();
        int count = 0, bytes = 0;
        try
        {
            token.ThrowIfCancellationRequested();
            promptHash = store.PutEvidence(Encoding.UTF8.GetBytes(prompt));
            start = store.RecordPrompt(Input("start", "pi_v3_prompt_prepared", [promptHash], reservation.EventId), promptHash);
            await turn.RunPromptAsync(run, prompt, (record, ct) => {
                ct.ThrowIfCancellationRequested();
                byte[] data = Encoding.UTF8.GetBytes(record.GetRawText());
                if (++count > MaxObservations || data.Length > MaxObservationBytes - bytes)
                    throw new InvalidDataException("Pi observation limit exceeded.");
                bytes += data.Length;
                ct.ThrowIfCancellationRequested();
                string hash = store.PutEvidence(data);
                ct.ThrowIfCancellationRequested();
                store.Append(Input("observation_" + count, "pi_v3_observation", [hash], start.EventId));
                outcome.Observe(record);
                return Task.CompletedTask;
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var result = new Result(outcome.CompletedWithoutModelError, outcome.ToolErrorObserved);
            store.Append(Input("result", "pi_v3_turn_observed", [], start.EventId,
                JsonSerializer.SerializeToElement(new { result.CompletedWithoutModelError, result.ToolErrorObserved })));
            return result;
        }
        catch
        {
            // A process crash can leave only the start event. Failure to persist this
            // best-effort marker must never turn an interruption into success.
            try { store.Append(Input("unknown", "pi_v3_turn_unknown", [], reservation.EventId)); }
            catch { }
            throw;
        }

        CanonicalStore.EventInput Input(string suffix, string type, string[] refs, string? cause = null, JsonElement? metadata = null) =>
            new(taskId, workspaceId, "pi_v3_adapter", run + "_" + suffix, type, "model_claim", "unverified",
                refs, metadata ?? JsonSerializer.SerializeToElement(new { }), CorrelationId: run, CausedByEventId: cause);
    }
}
