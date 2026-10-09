using System.Text;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

/// <summary>Appends bounded operational metrics without prompts, file paths, tool output, or secrets.</summary>
internal static class TelemetryRecorder
{
    public static Task WriteAsync(string root, AgentTaskRunner.Result result, long wallTimeMs)
    {
        var context = ContextTelemetryState.Options.Read(root).Enabled
            ? ContextTelemetryState.Read(root, result.TaskId) : null;
        var entry = new
        {
            timestamp = DateTimeOffset.UtcNow,
            task_id = result.TaskId,
            outcome = result.Phase,
            risk = result.Risk,
            tool_calls = result.ToolCalls,
            tool_errors = result.ToolErrors,
            files_changed = result.ChangedFiles.Length,
            tests_run = result.Tests.Length,
            tests_passed = result.Tests.Count(test => test.Passed),
            actor_turns = result.ActorTurns,
            replans = result.Replans,
            tests_without_improvement = result.TestsWithoutImprovement,
            no_progress = result.NoProgress,
            test_mutated_worktree = result.TestMutatedWorktree,
            knowledge_chunks = result.KnowledgeChunkCount,
            knowledge_chunk_ids = result.KnowledgeChunkIds,
            observation_recall_calls = result.ObservationRecalls,
            critic_verdict = result.CriticVerdict,
            sol_review_count = result.SolReviewCount,
            sol_verdict = result.SolVerdict,
            review_route = result.ReviewRoute,
            copilot_verdict = result.CopilotVerdict,
            copilot_issue_count = result.CopilotIssues?.Length ?? 0,
            sol_issue_count = result.SolIssues.Length,
            external_review_passed = result.ExternalReviewPassed,
            astra_status = result.AstraStatus,
            sol_rescue_count = result.SolRescueCount,
            sol_rescue_verdict = result.SolRescueVerdict,
            sol_escalation_reason = result.Supervisor.Action == "ESCALATE_SOL"
                ? result.Supervisor.Reason : (string?)null,
            false_verified = result.FalseVerified,
            final_success = result.EligibleForCompletion,
            wall_time_ms = wallTimeMs,
            context_metrics_available = context is not null,
            context = context is null ? null : new {
                context_limit = context.ContextLimit,
                estimated_prompt_tokens = context.PromptTokens,
                context_usage = context.ContextUsage,
                tool_schema_tokens = context.ToolSchemaTokens,
                tool_result_tokens = context.ToolResultTokens,
                conversation_tokens = context.ConversationTokens,
                retrieval_tokens = context.RetrievalTokens,
                system_prompt_tokens = context.SystemPromptTokens,
                tool_schema_share = context.ToolSchemaShare,
                tool_result_share = context.ToolResultShare,
                conversation_share = context.ConversationShare,
                retrieval_share = context.RetrievalShare,
                tool_count_exposed = context.ToolCountExposed,
                context_growth_since_last_check = context.ContextGrowthSinceLastCheck
            },
            context_action_taken = "none",
            laya_decisions = result.DecisionHistory.Select(item => new
            {
                action = item.Action, source = item.Source, reason = item.Reason,
                confidence = item.Confidence, latency_ms = item.LatencyMs,
                fallback = item.Fallback
            }).ToArray()
        };
        return AppendAsync(root, entry);
    }

    public static Task WriteFailureAsync(string root, string taskId, string risk,
        string errorType, long wallTimeMs) => AppendAsync(root, new
        {
            timestamp = DateTimeOffset.UtcNow,
            task_id = taskId,
            outcome = "failed",
            risk,
            error_type = errorType,
            final_success = false,
            wall_time_ms = wallTimeMs
        });

    private static Task AppendAsync(string root, object entry)
    {
        var path = Path.Combine(root, ".localbrain", "telemetry.jsonl");
        var line = JsonSerializer.Serialize(entry) + "\n";
        if (Encoding.UTF8.GetByteCount(line) > 8_192)
            throw new InvalidDataException("Telemetry entry exceeds limit");
        return File.AppendAllTextAsync(path, line, Encoding.UTF8);
    }
}
