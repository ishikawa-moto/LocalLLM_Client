using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalBrain.ClientHost.AgentV2;

internal sealed record SupervisorState(
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("tool_calls")] int ToolCalls,
    [property: JsonPropertyName("files_read")] int FilesRead,
    [property: JsonPropertyName("files_changed")] int FilesChanged,
    [property: JsonPropertyName("tests_run")] int TestsRun,
    [property: JsonPropertyName("tests_passed")] bool TestsPassed,
    [property: JsonPropertyName("same_error_count")] int SameErrorCount,
    [property: JsonPropertyName("same_command_count")] int SameCommandCount,
    [property: JsonPropertyName("replans")] int Replans,
    [property: JsonPropertyName("context_usage")] double ContextUsage,
    [property: JsonPropertyName("progress")] string Progress,
    [property: JsonPropertyName("last_action_changed_state")] bool LastActionChangedState,
    [property: JsonPropertyName("completion_candidate")] bool CompletionCandidate,
    [property: JsonPropertyName("high_risk_action")] bool HighRiskAction,
    [property: JsonPropertyName("user_approved")] bool UserApproved,
    [property: JsonPropertyName("required_tests_passed")] bool RequiredTestsPassed)
{
    public ContextTelemetryState? Context { get; init; }
    public bool ContextTelemetryEnabled { get; init; }
    public bool ContextLayaEnabled { get; init; }
    public bool ContextAutoActionsEnabled { get; init; }
    public double EffectiveContextUsage => Context?.ContextUsage ?? ContextUsage;

    public SupervisorState WithContext(string root, string taskId)
    {
        var options = ContextTelemetryState.Options.Read(root);
        return this with { ContextTelemetryEnabled = options.Enabled,
            Context = options.Enabled ? ContextTelemetryState.Read(root, taskId) : null,
            ContextLayaEnabled = options.Enabled && options.LayaEnabled,
            ContextAutoActionsEnabled = options.Enabled && options.AutoActionsEnabled };
    }

    public object Compact => !ContextTelemetryEnabled
        ? new { phase = Phase, tool_calls = ToolCalls, files_read = FilesRead,
            files_changed = FilesChanged, tests_run = TestsRun, tests_passed = TestsPassed,
            same_error_count = SameErrorCount, same_command_count = SameCommandCount,
            replans = Replans, context_usage = ContextUsage, progress = Progress,
            last_action_changed_state = LastActionChangedState }
        : ContextLayaEnabled && Context is { } metrics
        ? new { phase = Phase, tool_calls = ToolCalls, files_read = FilesRead,
            files_changed = FilesChanged, tests_run = TestsRun, tests_passed = TestsPassed,
            same_error_count = SameErrorCount, same_command_count = SameCommandCount,
            replans = Replans, context_usage = EffectiveContextUsage,
            tool_schema_share = metrics.ToolSchemaShare,
            tool_result_share = metrics.ToolResultShare,
            conversation_share = metrics.ConversationShare,
            retrieval_share = metrics.RetrievalShare,
            context_metrics_available = true,
            progress = Progress, last_action_changed_state = LastActionChangedState }
        : new { phase = Phase, tool_calls = ToolCalls, files_read = FilesRead,
            files_changed = FilesChanged, tests_run = TestsRun, tests_passed = TestsPassed,
            same_error_count = SameErrorCount, same_command_count = SameCommandCount,
            replans = Replans, context_usage = ContextUsage,
            context_metrics_available = false,
            progress = Progress, last_action_changed_state = LastActionChangedState };
}

internal sealed record SupervisorDecision(string Action, string Source, string Reason, double? Confidence,
    int? LatencyMs, bool Fallback);

internal static class SupervisorDecider
{
    public static Task<SupervisorDecision> DecideAsync(SupervisorState state, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!double.IsFinite(state.EffectiveContextUsage) || state.EffectiveContextUsage < 0 ||
            state.ToolCalls < 0 || state.FilesRead < 0 || state.FilesChanged < 0 ||
            state.TestsRun < 0 || state.SameErrorCount < 0 || state.SameCommandCount < 0 ||
            state.Replans < 0 || (state.TestsPassed && state.TestsRun == 0) ||
            (state.RequiredTestsPassed && !state.TestsPassed) ||
            state.Progress is not ("none" or "low" or "medium" or "high"))
            throw new InvalidDataException("Supervisor state is invalid");
        if (state.HighRiskAction && !state.UserApproved)
            return Task.FromResult(Rule("STOP", "high_risk_requires_user_approval"));
        if (state.CompletionCandidate && (!state.RequiredTestsPassed || state.TestsRun == 0 ||
            !state.TestsPassed))
            return Task.FromResult(Rule("RUN_TEST", "completion_requires_test_evidence"));
        if (state.CompletionCandidate)
            return Task.FromResult(Rule("FRESH_REVIEW", "completion_requires_independent_review"));
        if (state.SameErrorCount >= 3 || state.SameCommandCount >= 4 || state.Replans >= 3)
            return Task.FromResult(Rule("ESCALATE_SOL", "no_progress_threshold"));
        // Context composition is observation-only until a validated action
        // policy is implemented. The prior caller supplied 0 usage here.
        if (state.ToolCalls >= 5 && state.FilesChanged > 0 && state.TestsRun == 0)
            return Task.FromResult(Rule("RUN_TEST", "changed_files_need_test"));
        if (state.ToolCalls >= 3 && !state.LastActionChangedState &&
            state.Progress is "none" or "low")
            return Task.FromResult(Rule("REPLAN", "recent_action_did_not_change_state"));
        return Task.FromResult(Rule("CONTINUE", "no_rule_triggered"));
    }

    private static SupervisorDecision Rule(string action, string reason) =>
        new(action, "hard_rule", reason, null, null, false);
}
