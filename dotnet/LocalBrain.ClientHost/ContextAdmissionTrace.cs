using System.Diagnostics;

namespace LocalBrain.ClientHost;

// Metadata only. A new instance belongs to exactly one request, including native callbacks.
internal sealed class ContextAdmissionTrace(Action<string,object>? write=null)
{
    private readonly Stopwatch elapsed=Stopwatch.StartNew();
    internal string Stage { get; private set; }="validation";
    internal string? FailureReason { get; set; }
    internal int? OriginalTokens { get; set; }
    internal int? FinalTokens { get; set; }
    internal bool SelectionRequired { get; set; }
    internal bool FallbackUsed { get; set; }
    internal double LockWaitMs { get; set; }
    internal void Mark(string stage,object? detail=null){Stage=stage;write?.Invoke("continue_context_stage",Snapshot(detail));}
    internal object Snapshot(object? detail=null)=>new {stage=Stage,elapsed_ms=elapsed.Elapsed.TotalMilliseconds,
        original_prompt_tokens=OriginalTokens,final_prompt_tokens=FinalTokens,selection_required=SelectionRequired,
        fallback_used=FallbackUsed,lock_wait_ms=LockWaitMs,failure_reason=FailureReason,detail};
    internal string Reason(Exception error)=>FailureReason??(error is OperationCanceledException or TimeoutException?"context_measurement_timeout":
        Stage is "initial_measurement" or "final_measurement"?"context_measurement_failed":
        Stage is "reconstruction"?"history_reconstruction_failed":Stage is "archive"?"history_archive_failed":"invalid_context_request");
}
