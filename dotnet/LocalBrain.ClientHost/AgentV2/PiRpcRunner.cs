using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace LocalBrain.ClientHost.AgentV2;

/// <summary>One Pi RPC run against the existing Windows Bridge model path.</summary>
internal static class PiRpcRunner
{
    public static async Task<PiRunResult> RunAsync(string workspace, string prompt, bool freshCritic,
        bool allowMutations = false, string[]? allowedMutationPaths = null,
        string? sessionId = null,
        Func<PiProgressSnapshot, Task<SupervisorDecision>>? onMilestone = null,
        string? windowsRoot = null, string? taskId = null,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 100_000)
            throw new ArgumentException("Prompt must contain 1 to 100,000 characters");
        if (allowMutations && (allowedMutationPaths is null or { Length: 0 } ||
            allowedMutationPaths.Sum(item => item.Length) > 8_000))
            throw new ArgumentException("Actor mutation allowlist is missing or too large");
        var provider = await WslWorkspace.MapFileAsync(Path.Combine(AppContext.BaseDirectory,
            "scripts", "localbrain-provider.js"), token);
        var relay = await WslWorkspace.MapFileAsync(Path.Combine(AppContext.BaseDirectory,
            "localbrain.exe"), token);
        await using var sideEffects = windowsRoot is not null && HostTaskJournal.Current is { } journal
            ? new SideEffectGate(windowsRoot, allowMutations ? allowedMutationPaths! : [],
                journal.ExpectedHead, !freshCritic, journal) : null;
        var telemetryOptions = windowsRoot is null ? null : ContextTelemetryState.Options.Read(windowsRoot);
        var start = new ProcessStartInfo("wsl.exe");
        var launch = new List<string> { "-d", "Ubuntu", "--cd", workspace, "--exec", "/usr/bin/env",
            $"LOCALBRAIN_RELAY_EXE={relay}",
            $"LOCALBRAIN_ALLOWED_FILES={JsonSerializer.Serialize(allowedMutationPaths ?? [])}",
        };
        if (taskId is not null && Guid.TryParse(taskId, out _))
            launch.Add($"LOCALBRAIN_CONTEXT_TASK_ID={taskId}");
        launch.Add($"LOCALBRAIN_CONTEXT_AGENT_PHASE={(freshCritic ? "critic" : "actor")}");
        if ((sideEffects is not null ? "/home/worker/localbrain-v2/verified-tokenizer.json" : telemetryOptions?.TokenizerJson) is { } tokenizer)
            launch.Add($"LOCALBRAIN_CONTEXT_TOKENIZER_JSON={tokenizer}");
        launch.AddRange([
            "/home/worker/.volta/bin/pi", "--mode", "rpc",
            "--offline", "--approve", "--provider", "localbrain", "--model", "local-qwen38",
            "--extension", provider ]);
        if(windowsRoot is not null && HostTaskJournal.Current is {} rescueJournal && !string.Equals(windowsRoot,rescueJournal.WindowsRoot,StringComparison.OrdinalIgnoreCase)) {
            var registered=rescueJournal.Canonical.FindRescueWorkspace(windowsRoot,HostTaskJournal.RepositoryId(rescueJournal.WindowsRoot));
            var gitEnvironment=await WslWorkspace.RescueGitEnvironmentAsync(rescueJournal.Canonical,registered.WorkspaceId,token);
            launch.InsertRange(launch.IndexOf("/usr/bin/env")+1,gitEnvironment);
        }
        foreach (var arg in launch) start.ArgumentList.Add(arg);
        if (sideEffects is not null)
        {
            start.Environment["LOCALBRAIN_SIDEFX_PIPE"] = sideEffects.PipeName;
            start.Environment["LOCALBRAIN_SIDEFX_NONCE"] = sideEffects.Nonce;
            start.Environment.TryGetValue("WSLENV", out var inherited);
            start.Environment["WSLENV"] = (string.IsNullOrEmpty(inherited) ? "" : inherited + ":")
                + "LOCALBRAIN_SIDEFX_PIPE:LOCALBRAIN_SIDEFX_NONCE";
        }
        if (freshCritic)
        {
            start.ArgumentList.Add("--no-extensions");
            start.ArgumentList.Add("--no-session");
            start.ArgumentList.Add("--no-tools");
        }
        else
        {
            // The orchestrator alone may enable edit/write after its risk checks.
            var policy = await WslWorkspace.MapFileAsync(Path.Combine(AppContext.BaseDirectory,
                "scripts", "localbrain-policy.mjs"), token);
            start.ArgumentList.Add("--extension");
            start.ArgumentList.Add(policy);
            var hostTools = await WslWorkspace.MapFileAsync(Path.Combine(AppContext.BaseDirectory,
                "scripts", "host-file-tools.mjs"), token);
            start.ArgumentList.Add("--extension"); start.ArgumentList.Add(hostTools);
            start.ArgumentList.Add("--session-dir");
            start.ArgumentList.Add("/home/worker/localbrain-v2/agent-v2-sessions");
            if (sessionId is not null)
            {
                if (!Guid.TryParse(sessionId, out _)) throw new ArgumentException("Invalid Pi session ID");
                start.ArgumentList.Add("--session-id");
                start.ArgumentList.Add(sessionId);
            }
            start.ArgumentList.Add("--tools");
            start.ArgumentList.Add(allowMutations
                ? "read,ls,host_edit,host_write,host_recall,obs_recall"
                : "read,ls,host_recall,obs_recall");
        }

        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var noProgressSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        var progress = new ProgressTracker();
        var milestones = Channel.CreateBounded<PiProgressSnapshot>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        var nextMilestoneAt = 4;
        var providerError = false;
        var providerErrorCode="UnclassifiedProviderError";
        await using var worker = JsonlWorker.Start(start, message =>
        {
            if (message.TryGetProperty("type", out var journalType) &&
                journalType.GetString() is "tool_execution_start" or "tool_execution_end")
                HostTaskJournal.Current?.RecordToolEvent(message);
            progress.Observe(message);
            if (!message.TryGetProperty("type", out var type)) return;
            if (!freshCritic && type.GetString() == "tool_execution_end" && progress.NoProgress)
                noProgressSignal.TrySetResult();
            if (onMilestone is not null && type.GetString() == "tool_execution_end" &&
                progress.ToolCalls >= nextMilestoneAt)
            {
                milestones.Writer.TryWrite(progress.Snapshot());
                nextMilestoneAt = progress.ToolCalls + 4;
            }
            switch (type.GetString())
            {
                case "message_end":
                    if (!message.TryGetProperty("message", out var completed) ||
                        !completed.TryGetProperty("role", out var role) || role.GetString() != "assistant") break;
                    if (completed.TryGetProperty("stopReason", out var stop) && stop.GetString() == "error")
                    {
                        providerError = true;
                        var raw=completed.TryGetProperty("errorMessage",out var reason)?reason.GetString()??"":"";
                        providerErrorCode=new[]{"ContextMeasurementUnavailable","Windows Host request failed","Host capability is unavailable","InvalidDataException","UnauthorizedAccessException","context_handoff_required"}
                            .FirstOrDefault(code=>raw.Contains(code,StringComparison.Ordinal))??"UnclassifiedProviderError";
                        HostTaskJournal.Current?.Record("provider_error",new {code=providerErrorCode},"runtime_observed");
                    }
                    if (windowsRoot is not null && taskId is not null &&
                        completed.TryGetProperty("usage", out var usage) &&
                        usage.TryGetProperty("input", out var input) && input.TryGetInt32(out var inputTokens))
                    {
                        var actual = inputTokens;
                        if (usage.TryGetProperty("cacheRead", out var read) &&
                            read.TryGetInt32(out var cachedRead)) actual += cachedRead;
                        if (usage.TryGetProperty("cacheWrite", out var write) &&
                            write.TryGetInt32(out var cachedWrite)) actual += cachedWrite;
                        ContextTelemetryState.TryRecordActual(windowsRoot, taskId, actual);
                    }
                    if (completed.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
                        foreach (var block in blocks.EnumerateArray())
                            if (block.TryGetProperty("type", out var kind) && kind.GetString() == "text" &&
                                block.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
                            {
                                var text = value.GetString() ?? "";
                                if (output.Length + text.Length <= 100_000) output.Append(text);
                            }
                    break;
                case "agent_settled": settled.TrySetResult(); break;
            }
        });
        var state = await worker.SendAsync(new() { ["type"] = "get_state" }, TimeSpan.FromSeconds(30), token);
        if (!state.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Pi startup failed");
        HostTaskJournal.Current?.RecordPrompt(prompt, freshCritic);
        var accepted = await worker.SendAsync(new() { ["type"] = "prompt", ["message"] = prompt },
            TimeSpan.FromSeconds(30), token);
        if (!accepted.TryGetProperty("success", out success) || success.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Pi rejected the prompt");
        using var runTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        runTimeout.CancelAfter(TimeSpan.FromMinutes(10));
        SupervisorDecision? liveDecision = null;
        var interruptedForSupervisor = false;
        while (!settled.Task.IsCompleted)
        {
            var milestoneReady = milestones.Reader.WaitToReadAsync(runTimeout.Token).AsTask();
            var finished = await Task.WhenAny(settled.Task, worker.Completion,
                noProgressSignal.Task, milestoneReady).WaitAsync(runTimeout.Token);
            if (settled.Task.IsCompleted) break;
            if (finished == worker.Completion) {
                await worker.Completion;
                throw new IOException("Pi RPC closed before the run settled");
            }
            if (finished == noProgressSignal.Task)
            {
                await AbortAsync(worker, runTimeout.Token);
                break;
            }
            if (!await milestoneReady) continue;
            while (milestones.Reader.TryRead(out var snapshot) && onMilestone is not null)
            {
                liveDecision = await onMilestone(snapshot);
                if (liveDecision.Action is not ("RUN_TEST" or "REPLAN" or "ESCALATE_SOL" or "STOP"))
                    continue;
                await AbortAsync(worker, runTimeout.Token);
                interruptedForSupervisor = true;
                break;
            }
            if (interruptedForSupervisor) break;
        }
        milestones.Writer.TryComplete();
        if (!settled.Task.IsCompleted)
        {
            var finished = await Task.WhenAny(settled.Task, worker.Completion)
                .WaitAsync(TimeSpan.FromSeconds(30), token);
            if (finished != settled.Task)
                throw new IOException("Pi RPC closed before the run settled");
        }
        if (providerError && sideEffects?.HandoffRequested != true)
            throw new InvalidOperationException("LocalBrain model response failed: "+providerErrorCode);
        HostTaskJournal.Current?.Record(freshCritic ? "critic_response" : "actor_response",
            new { text = VisibleText(output.ToString()), progress.ToolCalls, progress.ToolErrors }, "model_claim");
        return new PiRunResult(VisibleText(output.ToString()), progress.ToolCalls, progress.ToolErrors,
            progress.FilesRead, progress.FilesChanged, progress.SameFileReadCount, progress.SameCommandCount,
            progress.SameErrorCount, progress.ToolCallsSinceProgress, progress.NoProgress,
            progress.ObservationRecalls, worker.StderrLineCount,
            state.GetProperty("data").GetProperty("sessionId").GetString(),
            liveDecision, interruptedForSupervisor, sideEffects?.HandoffRequested == true);
    }

    private static async Task AbortAsync(JsonlWorker worker, CancellationToken token)
    {
        var aborted = await worker.SendAsync(new() { ["type"] = "abort" },
            TimeSpan.FromSeconds(30), token);
        if (!aborted.TryGetProperty("success", out var success) ||
            success.ValueKind != JsonValueKind.True)
            throw new IOException("Pi did not acknowledge Supervisor abort");
    }

    private static string VisibleText(string raw)
    {
        // Some local OpenAI-compatible servers place Qwen reasoning markup in
        // the text block. Never return that material to callers or telemetry.
        var lastClose = raw.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (lastClose >= 0) raw = raw[(lastClose + "</think>".Length)..];
        raw = Regex.Replace(raw, @"<think\b[^>]*>[\s\S]*?</think>", "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var unclosed = raw.IndexOf("<think", StringComparison.OrdinalIgnoreCase);
        if (unclosed >= 0) raw = raw[..unclosed];
        return raw.Trim();
    }
}

internal sealed record PiRunResult(string Text, int ToolCalls, int ToolErrors, int FilesRead, int FilesChanged,
    int SameFileReadCount, int SameCommandCount, int SameErrorCount, int ToolCallsSinceProgress,
    bool NoProgress, int ObservationRecalls, int StderrLines, string? SessionId,
    SupervisorDecision? LiveDecision, bool InterruptedForSupervisor, bool ContextHandoffRequested = false);
