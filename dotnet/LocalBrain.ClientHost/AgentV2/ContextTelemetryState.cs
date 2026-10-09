using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

/// <summary>Reads only bounded numeric state emitted at the Pi provider boundary.</summary>
internal sealed record ContextTelemetryState(int ContextLimit, int PromptTokens,
    double ContextUsage, int ToolSchemaTokens, int ToolResultTokens, int ConversationTokens,
    int RetrievalTokens, int SystemPromptTokens, int FramingTokens, double ToolSchemaShare,
    double ToolResultShare, double ConversationShare, double RetrievalShare,
    int ToolCountExposed, double MeasurementMs, double? ContextGrowthSinceLastCheck)
{
    public double OtherShare => Math.Max(0, 1 - ToolSchemaShare - ToolResultShare -
        ConversationShare - RetrievalShare);
    public string LargestPressure => new[] {
        (Name: "conversation", Share: ConversationShare),
        (Name: "tool_result", Share: ToolResultShare),
        (Name: "tool_schema", Share: ToolSchemaShare),
        (Name: "retrieval", Share: RetrievalShare)
    }.MaxBy(item => item.Share).Name;

    internal sealed record Options(bool Enabled, bool Persist, bool LayaEnabled,
        bool AutoActionsEnabled, string? TokenizerJson)
    {
        public static Options Read(string root)
        {
            var defaults = new Options(true, true, true, false,
                "/home/worker/localbrain-v2/verified-tokenizer.json");
            var path = Path.Combine(root, ".localbrain", "contextTelemetry.json");
            try
            {
                if (!File.Exists(path)) return defaults;
                var file = new FileInfo(path);
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length > 4096)
                    return defaults with { Enabled = false };
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var source = document.RootElement;
                if (source.TryGetProperty("contextTelemetry", out var nested)) source = nested;
                static bool Flag(JsonElement value, string name, bool fallback) =>
                    value.TryGetProperty(name, out var field) ? field.ValueKind == JsonValueKind.True : fallback;
                string? tokenizer = source.TryGetProperty("tokenizerJson", out var tokenPath) &&
                    tokenPath.ValueKind == JsonValueKind.String ? tokenPath.GetString() : null;
                if (tokenizer is not null && (tokenizer.Length > 512 ||
                    !tokenizer.StartsWith("/home/worker/localbrain-v2/", StringComparison.Ordinal) ||
                    tokenizer.Split('/').Contains("..") || !tokenizer.EndsWith(".json", StringComparison.Ordinal)))
                    tokenizer = null;
                return new(Flag(source, "enabled", true), Flag(source, "persist", true),
                    Flag(source, "layaEnabled", true), Flag(source, "autoActionsEnabled", false),
                    tokenizer);
            }
            catch { return defaults with { Enabled = false }; }
        }
    }

    public static ContextTelemetryState? Read(string root, string? taskId = null)
    {
        try
        {
            var path = Path.Combine(root, ".localbrain", "context-current.json");
            var file = new FileInfo(path);
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                file.Length is < 2 or > 4096) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var value = document.RootElement;
            if (value.GetProperty("context_metrics_available").ValueKind != JsonValueKind.True ||
                taskId is not null && value.GetProperty("task_id").GetString() != taskId) return null;
            var tokens = value.GetProperty("tokens");
            var shares = value.GetProperty("shares");
            var state = new ContextTelemetryState(value.GetProperty("context_limit").GetInt32(),
                value.GetProperty("estimated_prompt_tokens").GetInt32(),
                value.GetProperty("context_usage").GetDouble(),
                tokens.GetProperty("tool_schema").GetInt32(),
                tokens.GetProperty("tool_result").GetInt32(),
                tokens.GetProperty("conversation").GetInt32(),
                tokens.GetProperty("retrieval").GetInt32(),
                tokens.GetProperty("system_prompt").GetInt32(),
                tokens.GetProperty("framing").GetInt32(),
                shares.GetProperty("tool_schema").GetDouble(),
                shares.GetProperty("tool_result").GetDouble(),
                shares.GetProperty("conversation").GetDouble(),
                shares.GetProperty("retrieval").GetDouble(),
                value.GetProperty("tool_count_exposed").GetInt32(),
                value.GetProperty("measurement_ms").GetDouble(),
                value.TryGetProperty("context_growth_since_last_check", out var growth) &&
                    growth.ValueKind == JsonValueKind.Number ? growth.GetDouble() : null);
            if (state.ContextLimit != 32768 || state.PromptTokens < 1 ||
                state.ToolSchemaTokens < 0 || state.ToolResultTokens < 0 ||
                state.ConversationTokens < 0 || state.RetrievalTokens < 0 ||
                state.SystemPromptTokens < 0 || state.FramingTokens < 0 ||
                state.ToolCountExposed < 0 ||
                state.ToolSchemaTokens + state.ToolResultTokens + state.ConversationTokens +
                    state.RetrievalTokens + state.SystemPromptTokens + state.FramingTokens != state.PromptTokens ||
                !double.IsFinite(state.ContextUsage) ||
                Math.Abs(state.ContextUsage - (double)state.PromptTokens / state.ContextLimit) > 0.000001 ||
                !double.IsFinite(state.MeasurementMs) || state.MeasurementMs < 0 ||
                state.ContextGrowthSinceLastCheck is { } growthValue &&
                    !double.IsFinite(growthValue) ||
                new[] { state.ToolSchemaShare, state.ToolResultShare, state.ConversationShare,
                    state.RetrievalShare }.Any(share => !double.IsFinite(share) || share is < 0 or > 1))
                return null;
            return state;
        }
        catch { return null; }
    }

    public static void TryRecordActual(string root, string taskId, int actualPromptTokens)
    {
        var options = Options.Read(root);
        if (actualPromptTokens < 1 || actualPromptTokens > 32768 * 4 ||
            !options.Enabled || !options.Persist) return;
        try
        {
            var preflight = Read(root, taskId);
            var entry = new { timestamp = DateTimeOffset.UtcNow, task_id = taskId,
                phase = "actual", context_limit = 32768, actual_prompt_tokens = actualPromptTokens,
                estimated_prompt_tokens = preflight?.PromptTokens,
                estimation_error = preflight?.PromptTokens - actualPromptTokens,
                framing_tokens = preflight is null ? (int?)null : Math.Max(0,
                    actualPromptTokens - preflight.PromptTokens) };
            AppendNumericLine(Path.Combine(root, ".localbrain", "context-actual.jsonl"), entry);
        }
        catch { /* Actual accounting never changes an Agent outcome. */ }
    }

    public static void TryRecordDecision(string root, string taskId, string phase,
        SupervisorDecision decision)
    {
        var options = Options.Read(root);
        if (!options.Enabled || !options.Persist) return;
        try
        {
            var preflight = Read(root, taskId);
            var entry = new { timestamp = DateTimeOffset.UtcNow, task_id = taskId,
                phase, context_metrics_available = preflight is not null,
                context_limit = preflight?.ContextLimit,
                prompt_tokens = preflight?.PromptTokens,
                context_usage = preflight?.ContextUsage,
                tool_schema_tokens = preflight?.ToolSchemaTokens,
                tool_result_tokens = preflight?.ToolResultTokens,
                conversation_tokens = preflight?.ConversationTokens,
                retrieval_tokens = preflight?.RetrievalTokens,
                system_prompt_tokens = preflight?.SystemPromptTokens,
                framing_tokens = preflight?.FramingTokens,
                tool_schema_share = preflight?.ToolSchemaShare,
                tool_result_share = preflight?.ToolResultShare,
                conversation_share = preflight?.ConversationShare,
                retrieval_share = preflight?.RetrievalShare,
                context_growth_since_last_check = preflight?.ContextGrowthSinceLastCheck,
                supervisor_decision = decision.Action,
                decision_source = decision.Source,
                laya_decision = decision.Source == "laya" ? decision.Action : null,
                action_taken = "none" };
            AppendNumericLine(Path.Combine(root, ".localbrain", "context-decisions.jsonl"), entry);
        }
        catch { /* Decision logging never changes an Agent outcome. */ }
    }

    private static void AppendNumericLine(string path, object entry)
    {
        if (File.Exists(path) && new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
            return;
        var line = JsonSerializer.Serialize(entry) + "\n";
        if (line.Length > 4096) return;
        File.AppendAllText(path, line);
        if (new FileInfo(path).Length <= 2_000_000) return;
        var recent = File.ReadAllText(path);
        recent = recent[^Math.Min(1_000_000, recent.Length)..];
        var boundary = recent.IndexOf('\n');
        File.WriteAllText(path, boundary >= 0 ? recent[(boundary + 1)..] : "");
    }
}
