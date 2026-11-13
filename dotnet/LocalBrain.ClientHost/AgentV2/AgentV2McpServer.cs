using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

/// <summary>A separate Continue-facing entry point for the scoped Agent v2 pilot.</summary>
internal sealed class AgentV2McpServer(ProjectAllowlist allowlist, ClientConfig config,string? isolatedControlRoot=null,bool launchManaged=true,RunnerMcpScope? runner=null)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,(string Root,string Task)> investigations=new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,bool> cancelledRequests=new();
    private static readonly object[] Tools =
    [
        Tool("localbrain_agent_v2_run", "Submit coding and command work to the managed Agent v2 Host. It runs in the background until tests/review pass or a real stop condition. One native task grant covers its recorded commands; queued is not completion.", false),
        Tool("localbrain_agent_v2_resume", "Resume an incomplete LocalBrain Agent v2 task with the identical request and unchanged checkpoint evidence.", false),
        new { name = "localbrain_agent_v2_status",
            description = "Read only the bounded task phase and validation gate from a registered Git root.",
            inputSchema = new { type = "object", properties = new {
                workspace = new { type = "string", description = "Registered absolute Git root" } },
                required = new[] { "workspace" }, additionalProperties = false },
            annotations = new { readOnlyHint = true, destructiveHint = false,
                idempotentHint = true, openWorldHint = false } },
        new{name="localbrain_agent_v2_cancel",description="Cancel the exact managed task and its owned commands; retain all evidence. Never reports a running task as completed.",
            inputSchema=new{type="object",properties=new{workspace=new{type="string"},task_id=new{type="string"}},required=new[]{"workspace","task_id"},additionalProperties=false},
            annotations=new{readOnlyHint=false,destructiveHint=false,idempotentHint=true,openWorldHint=false}},
        Tool("localbrain_agent_v2_investigate", "Run a LOW read-only repository investigation under the detached Host owner and return the final report. Set read_only=true, no command_execution, required_tests=[{kind:git_diff_check}]. Preserve the exact original requirement and completion criteria. Host retains evidence and continues settled incomplete turns up to six times; no shell/writes or native command grant. Wait for this single call; do not submit duplicates. Infrastructure/unknown outcomes stop without external rescue.",true)
    ];

    private static object Tool(string name, string description, bool readOnly, bool runner = false) => new
    {
        name, description,
        inputSchema = new { type = "object", properties = new
        {
            workspace = new { type = "string", description = "Exact registered absolute Git root, not the parent Continue workspace" },
            task = new { type = "object", properties = new
            {
                requirement = new { type = "string" },
                acceptance_criteria = new { type = "array", items = new { type = "string" } },
                risk = new { type = "string", @enum = new[] { "LOW", "NORMAL", "HIGH" } },
                approved_high_risk = new { type = "boolean" },
                external_ai_disabled = new { type = "boolean", description = "Set true to keep this task on local AI only." },
                read_only = new {type="boolean",description="True only for LOW investigations: no shell, writes or command_execution; required_tests must contain only git_diff_check."},
                allowed_files = new { type = "array", description = "Git-root-relative files. With command_execution, ** grants the whole trusted project or folder/** a subtree; protected metadata/secrets remain excluded from file tools.", items = new { type = "string" } },
                required_tests = new { type = "array", items = new { type = "object",
                    properties = new { kind = new { type = "string", @enum = new[] {
                        "git_diff_check", "dotnet_build", "dotnet_test", "npm_test", "command_exit" } },
                        target = new { type = "string", description = "Omit for npm_test and git_diff_check; use a stable command operationId for command_exit; use a Git-root-relative .csproj only for dotnet_build or dotnet_test" } }, required = new[] { "kind" },
                    additionalProperties = false } },
                command_execution = new { type="object", description="Required for managed autonomous work. Native human approval binds this exact scope; cwd is not a hostile-code sandbox.", properties=new {
                    environments=new{type="array",items=new{type="string", @enum=new[]{"windows","wsl"}}},
                    working_directories=new{type="array",items=new{type="string"}},
                    output_directories=new{type="array",items=new{type="string"}},
                    max_command_seconds=new{type="integer",minimum=1,maximum=86400},
                    max_task_minutes=new{type="integer",minimum=1,maximum=1440},
                    max_actor_turns=new{type="integer",minimum=3,maximum=300},
                    max_commands=new{type="integer",minimum=runner?0:1,maximum=runner?0:1024},
                    max_output_bytes=new{type="integer",minimum=4096,maximum=8388608}
                }, required=new[]{"environments","working_directories","output_directories"},additionalProperties=false }
            }, required = new[] { "requirement", "acceptance_criteria", "risk",
                "approved_high_risk", "allowed_files", "required_tests" },
                additionalProperties = false }
        }, required = new[] { "workspace", "task" }, additionalProperties = false },
        annotations = new { readOnlyHint = readOnly, destructiveHint = false,
            idempotentHint = false, openWorldHint = false }
    };

    public async Task RunAsync()
    {
        var pending=new List<Task>();
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using(var peek=TryParse(line)){
                if(peek?.RootElement is {ValueKind:JsonValueKind.Object} message && message.TryGetProperty("method",out var method) && method.ValueKind==JsonValueKind.String){
                    if(method.GetString()=="notifications/cancelled" && message.TryGetProperty("params",out var cancel) && cancel.ValueKind==JsonValueKind.Object && cancel.TryGetProperty("requestId",out var cancelled)){
                        var key=cancelled.GetRawText();if(cancelledRequests.Count<32)cancelledRequests[key]=true;
                        if(investigations.TryGetValue(key,out var active))await TaskAgentCoordinator.CancelAsync(active.Root,active.Task,isolatedControlRoot);
                        continue;
                    }
                    if(method.GetString()=="tools/call" && message.TryGetProperty("params",out var parameters) && parameters.ValueKind==JsonValueKind.Object && parameters.TryGetProperty("arguments",out var arguments) && arguments.ValueKind==JsonValueKind.Object &&
                        arguments.TryGetProperty("task",out var task) && task.ValueKind==JsonValueKind.Object && task.TryGetProperty("read_only",out var readOnly) && readOnly.ValueKind==JsonValueKind.True){
                        pending.RemoveAll(t=>t.IsCompletedSuccessfully);
                        if(pending.Count<4){pending.Add(RespondAsync(line));continue;}
                    }
                }
            }
            await RespondAsync(line);
        }
        await Task.WhenAll(pending);
    }
    private static JsonDocument? TryParse(string line){if(line.Length>64000)return null;try{return JsonDocument.Parse(line);}catch(JsonException){return null;}}
    private async Task RespondAsync(string line)
    {
            if (line.Length > 64_000)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = (object?)null,
                    error = new { code = -32600, message = "Request exceeds size limit" } }));
                return;
            }
            object? id = null;
            object response;
            try
            {
                using var document = JsonDocument.Parse(line);
                var request = document.RootElement;
                if (request.TryGetProperty("id", out var requestId)) id = requestId.Clone();
                var method = request.GetProperty("method").GetString();
                if (method == "notifications/initialized") return;
                object result = method switch
                {
                    "initialize" => new { protocolVersion = request.TryGetProperty("params", out var p) &&
                        p.TryGetProperty("protocolVersion", out var v) ? v.GetString() : "2025-06-18",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "LocalBrain Agent v2", version = "0.2.0" } },
                    "tools/list" => new { tools = runner is null ? Tools :
                        [Tool("localbrain_agent_v2_run", "Submit a bounded LOW file task in the dedicated Runner checkout. Set max_commands=0, output_directories=[], required_tests=[git_diff_check]. Native approval is required.", false, true), Tools[2], Tools[3], Tools[4]] },
                    "tools/call" => await CallAsync(request.GetProperty("params"),id is JsonElement requestKey?requestKey.GetRawText():null),
                    _ => throw new InvalidDataException("Unknown MCP method")
                };
                response = new { jsonrpc = "2.0", id, result };
            }
            catch (Exception error)
            {
                // Do not return process output, source text, paths, or credentials to the UI.
                response = new { jsonrpc = "2.0", id, error = new {
                    code = -32603, message = error is InvalidDataException
                        ? $"InvalidDataException: {error.Message}" : error.GetType().Name } };
            }
            lock(investigations)Console.WriteLine(JsonSerializer.Serialize(response));
    }

    private async Task<object> CallAsync(JsonElement parameters,string? requestKey=null)
    {
        var name = parameters.GetProperty("name").GetString();
        var arguments = parameters.GetProperty("arguments");
        var selected = arguments.GetProperty("workspace").GetString()
            ?? throw new InvalidDataException("Workspace is required");
        runner?.RequireWorkspace(selected);
        var root = allowlist.Resolve(selected);
        runner?.RequireWorkspace(root);
        if (name == "localbrain_agent_v2_status")
            return Content(isolatedControlRoot is null && TaskAgentCoordinator.CurrentDirectory(root) is null
                ? ReadStatus(root) : await TaskAgentCoordinator.StatusAsync(root,isolatedControlRoot,runner is not null));
        if(name=="localbrain_agent_v2_cancel")return Content(await TaskAgentCoordinator.CancelAsync(root,arguments.GetProperty("task_id").GetString()??"",isolatedControlRoot,runner is not null));
        if (name is not ("localbrain_agent_v2_run" or "localbrain_agent_v2_resume" or "localbrain_agent_v2_investigate"))
            throw new InvalidDataException("Unknown Agent v2 tool");
        if (runner is not null && name == "localbrain_agent_v2_resume")
            throw new UnauthorizedAccessException("Runner resume is not exposed");
        var task = AgentTaskRequest.Parse(arguments.GetProperty("task").GetRawText());
        if(name=="localbrain_agent_v2_investigate" && !task.ReadOnly)
            throw new UnauthorizedAccessException("Investigation requires the read-only Host path");
        if(task.ReadOnly && launchManaged && requestKey is null)throw new InvalidDataException("Investigation delivery requires an MCP request ID");
        runner?.RequireTask(task);
        var submitted=await TaskAgentCoordinator.SubmitAsync(root,task,config,resume:name=="localbrain_agent_v2_resume",control:isolatedControlRoot,launch:launchManaged,runnerSummary:runner is not null);
        if(!task.ReadOnly || !launchManaged)return Content(submitted);
        requestKey=requestKey!;
        var taskId=JsonSerializer.SerializeToElement(submitted).GetProperty("task_id").GetString()!;
        investigations[requestKey]=(root,taskId);
        try{
            if(cancelledRequests.ContainsKey(requestKey))await TaskAgentCoordinator.CancelAsync(root,taskId,isolatedControlRoot);
            return Content(await TaskAgentCoordinator.WaitForInvestigationAsync(root,isolatedControlRoot));
        }finally{investigations.TryRemove(requestKey,out _);cancelledRequests.TryRemove(requestKey,out _);}
    }

    private static object ReadStatus(string root)
    {
        var canonical = HostTaskJournal.ReadLatest(root);
        if (canonical is null) return new { phase = "canonical_not_started", eligible_for_completion = false };
        using var document = JsonDocument.Parse(canonical.StateJson);
        var state = document.RootElement;
        var phase = state.TryGetProperty("phase", out var recordedPhase) ? recordedPhase.GetString() : "created";
        var taskId = canonical.TaskId;
        var turns = state.TryGetProperty("actor_turns", out var value) &&
            value.TryGetInt32(out var number) ? number : 0;
        var validation = LocalValidationV2.FromCanonical(state) ?? new(false,false,false,true,false,[],false,true,false);
        var contextOptions = ContextTelemetryState.Options.Read(root);
        var context = contextOptions.Enabled ? ContextTelemetryState.Read(root, taskId) : null;
        return new { task_id = taskId, phase, actor_turns = turns,
            eligible_for_completion = phase == "complete" && validation.IsComplete,
            required_tests_passed = validation.RequiredTestsPassed,
            local_review_passed = validation.LocalReviewPassed,
            sol_review_required = validation.SolReviewRequired,
            sol_review_passed = validation.SolReviewPassed,
            external_review_required = validation.ExternalReviewRequired,
            external_review_passed = validation.ExternalReviewPassed,
            review_route = validation.ReviewRoute,
            false_verified = validation.FalseVerifiedDetected,
            context_metrics_available = context is not null,
            context = context is null ? null : new {
                context_limit = context.ContextLimit,
                estimated_prompt_tokens = context.PromptTokens,
                context_usage = context.ContextUsage,
                tool_schema_share = context.ToolSchemaShare,
                tool_result_share = context.ToolResultShare,
                conversation_share = context.ConversationShare,
                retrieval_share = context.RetrievalShare,
                other_share = context.OtherShare,
                largest_current_pressure = context.LargestPressure,
                tool_count_exposed = context.ToolCountExposed,
                measurement_ms = context.MeasurementMs,
                context_growth_since_last_check = context.ContextGrowthSinceLastCheck,
                suggested_action = "none" // Observe and collect a baseline before action thresholds.
            } };
    }

    private static object Content(object value) => new { content = new[] {
        new { type = "text", text = JsonSerializer.Serialize(value) } } };
}
