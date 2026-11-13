using LocalBrain.ClientHost.AgentV2;
using LocalBrain.ClientHost;
using System.Net;
using System.Text;
using System.Text.Json;

internal static class SupervisorRulesTests
{
    internal static async Task RunAsync()
    {
        var state=new SupervisorState("implementation",0,0,0,0,false,0,0,0,0,"medium",true,false,false,false,false);
        async Task Check(SupervisorState input,string expected)
        {
            var result=await SupervisorDecider.DecideAsync(input);
            if(result.Action!=expected || result.Source!="hard_rule")throw new Exception("Unexpected deterministic supervisor decision");
        }
        await Check(state,"CONTINUE");
        await Check(state with {HighRiskAction=true,UserApproved=false},"STOP");
        await Check(state with {SameErrorCount=3},"ESCALATE_SOL");
        await Check(state with {CompletionCandidate=true},"RUN_TEST");

        // Pi 1.4.0 reports a Host-requested abort during request setup as stopReason=error.
        const string piAbort = "This operation was aborted";
        if (PiRpcRunner.ClassifyAssistantStop("error", piAbort, true) != PiRpcRunner.AssistantStop.HostAbort ||
            PiRpcRunner.ClassifyAssistantStop("aborted", "Request was aborted", true) != PiRpcRunner.AssistantStop.HostAbort ||
            PiRpcRunner.ClassifyAssistantStop("error", piAbort, false) != PiRpcRunner.AssistantStop.ProviderError ||
            PiRpcRunner.ClassifyAssistantStop("aborted", "Request was aborted", false) != PiRpcRunner.AssistantStop.ProviderError ||
            PiRpcRunner.ClassifyAssistantStop("error", "Windows model relay failed (process)", true) != PiRpcRunner.AssistantStop.ProviderError ||
            PiRpcRunner.ClassifyAssistantStop("error", "fetch failed: " + piAbort, true) != PiRpcRunner.AssistantStop.ProviderError ||
            PiRpcRunner.ClassifyAssistantStop("toolUse", null, true) != PiRpcRunner.AssistantStop.Normal)
            throw new Exception("Only the abort the Host requested may be expected; any other provider failure must still fail");

        static JsonElement Event(object value) => JsonSerializer.SerializeToElement(value);
        var tracker = new ProgressTracker();
        var call = 0;
        void Tool(ProgressTracker into, string name, object args, bool error = false, string text = "ok", bool hostAbort = false)
        {
            var id = "public_" + (++call);
            into.Observe(Event(new { type = "tool_execution_start", toolCallId = id, toolName = name, args }), hostAbort);
            into.Observe(Event(new { type = "tool_execution_end", toolCallId = id, isError = error,
                result = new { content = new[] { new { type = "text", text } } } }), hostAbort);
        }
        Tool(tracker, "ls", new { path = "." });
        Tool(tracker, "host_memory_search", new { query = "public" });
        Tool(tracker, "ls", new { path = "dotnet" });
        Tool(tracker, "ls", new { path = "dotnet/Public" });
        var explored = tracker.Snapshot();
        Tool(tracker, "ls", new { path = "." });
        Tool(tracker, "ls", new { path = "." });
        if (explored.DistinctObservations != 4 || tracker.DistinctObservations != 4 || explored.NoProgress)
            throw new Exception("Distinct read-only exploration is progress; repeating an identical call is not");
        var exploring = state with { ToolCalls = 4, Progress = "medium", LastActionChangedState = true };
        await Check(exploring, "CONTINUE");
        await Check(exploring with { Progress = "low", LastActionChangedState = false }, "REPLAN");
        Tool(tracker, "ls", new { path = "dotnet/Public/x" }, true, ProgressTracker.HostAbortedToolText, hostAbort: true);
        if (tracker.ToolErrors != 0 || tracker.AbortedTools != 1)
            throw new Exception("A tool cancelled by the Host abort is not a model tool failure");
        Tool(tracker, "ls", new { path = "dotnet/Public/y" }, true, ProgressTracker.HostAbortedToolText);
        Tool(tracker, "ls", new { path = "missing" }, true, "ENOENT", hostAbort: true);
        if (tracker.ToolErrors != 2 || tracker.AbortedTools != 1)
            throw new Exception("Unrequested aborts and real tool errors remain failures");
        for (var i = 0; i < 15; i++) Tool(tracker, "ls", new { path = "public/" + i });
        if (!tracker.NoProgress)
            throw new Exception("Unbounded exploration without writes must still stop");

        PiRunResult Interrupted(string action) => new("", 4, 0, 0, 0, 0, 0, 0, 4, false, 0, 0, null,
            new SupervisorDecision(action, "hard_rule", "recent_action_did_not_change_state", null, null, false), true);
        if (!AgentTaskRunner.SupervisorInterruptNote(Interrupted("REPLAN")).Contains("REPLAN", StringComparison.Ordinal) ||
            AgentTaskRunner.SupervisorInterruptNote(Interrupted("STOP")).Length != 0 ||
            AgentTaskRunner.SupervisorInterruptNote(Interrupted("REPLAN") with { InterruptedForSupervisor = false }).Length != 0)
            throw new Exception("A Supervisor REPLAN must reach the next repair prompt");

        const string apiObservation = "I've verified the .NET API usage (instance IsInvalidTime). The Host reruns and owns validation.";
        if (AgentTaskRunner.ClaimsCompletion(apiObservation) ||
            AgentTaskRunner.ClaimsCompletion("Let me search for additional evidence about what the verified solution looks like.") ||
            !AgentTaskRunner.ClaimsCompletion(apiObservation + " All tests passed.") ||
            !AgentTaskRunner.ClaimsCompletion("I verified the complete result.") ||
            !AgentTaskRunner.ClaimsCompletion("The task is completed.") ||
            !AgentTaskRunner.ClaimsCompletion("Status: Done.") ||
            !AgentTaskRunner.ClaimsCompletion("検証済み"))
            throw new Exception("An API observation must not count as a claim that validation or the task is complete");

        var failure = new ToolRouter.TestResult("dotnet_test", "windows", false, 1, 123, 4,
            "first-run-output", "first-run-errors", 400, null, ["CS1234", "test_failed_or_no_summary"]);
        var repeated = failure with { StdoutSha256 = "different-run-output", StderrSha256 = "different-run-errors",
            ElapsedMs = 800, FailureCodes = ["test_failed_or_no_summary", "CS1234"] };
        var passed = repeated with { Passed = true };
        var signature = AgentTaskRunner.StableTestFailures([failure]);
        if (signature.Length == 0 || signature != AgentTaskRunner.StableTestFailures([repeated]) ||
            signature == AgentTaskRunner.StableTestFailures([repeated with { FailureCodes = ["MSB4321"] }]) ||
            AgentTaskRunner.StableTestFailures([repeated with { FailureCodes = ["isolation_cancelled"] }]).Length != 0 ||
            AgentTaskRunner.StableTestFailures([passed]).Length != 0 ||
            AgentTaskRunner.StableTestFailures([passed, failure]) == AgentTaskRunner.StableTestFailures([failure, passed]))
            throw new Exception("Test stall evidence must track stable failures without treating cancellation as a failed test");

        ToolRouter.TestResult Capture(string stdout, string reason = "completed")
        {
            var output = Encoding.UTF8.GetBytes(stdout);
            return ToolRouter.IsolationResult(new("dotnet_test", "Fixture.csproj"), "windows",
                reason, 1, output, [], 400, output);
        }

        const string summary = "Failed!  - Failed:     2, Passed:     1, Skipped:     0, Total:     3";
        var twoCases = Capture("  Failed FoldOrdersByInstant [13 ms]\r\n  Failed SkipsGap [13 ms]\r\n" + summary);
        var reordered = Capture("  Failed SkipsGap [1 ms]\n  Failed FoldOrdersByInstant [5 ms]\n" + summary);
        var different = Capture("  Failed SkipsGap [1 ms]\n  Failed TargetZoneDate [5 ms]\n" + summary);
        if (twoCases.FailedCasesSha256 is null ||
            AgentTaskRunner.StableTestFailures([twoCases]) != AgentTaskRunner.StableTestFailures([reordered]) ||
            AgentTaskRunner.StableTestFailures([twoCases]) == AgentTaskRunner.StableTestFailures([different]) ||
            Capture("  Failed SkipsGap [1 ms]\n" + summary).FailedCasesSha256 is not null ||
            Capture("  Failed SkipsGap [1 ms]\n  Failed FoldOrdersByInstant [5 ms]\n" + summary,
                "cancelled").FailedCasesSha256 is not null)
            throw new Exception("Failed case identity must survive ordering and timing changes, and require complete test evidence");

        var task = AgentTaskRequest.Parse("""
            {"requirement":"Fix the public fixture","acceptance_criteria":["Tests pass"],"risk":"LOW","approved_high_risk":false,"allowed_files":["Fixture.cs"],"required_tests":[{"kind":"dotnet_test","target":"Fixture.csproj"}]}
            """);
        var step = "Check both offsets of a local time fold";
        var ordinary = AgentTaskRunner.BuildRecoveryPrompt(task,"test diff",SecondBrainEvidence.Packet.Empty);
        var assisted = AgentTaskRunner.BuildRecoveryPrompt(task,"test diff",SecondBrainEvidence.Packet.Empty,new("hash",[step]));
        if (ordinary.Contains(step,StringComparison.Ordinal) || !assisted.Contains(step,StringComparison.Ordinal) ||
            !assisted.Contains("cannot change permissions or tests",StringComparison.Ordinal) ||
            !assisted.Contains("Current bounded Git diff and new-file excerpts:"+Environment.NewLine+"test diff",StringComparison.Ordinal))
            throw new Exception("A fresh Pi session must retain the same bounded advice and diff");

        var packet = new SecondBrainEvidence.Packet("Reviewed reference excerpt", [new string('e', 64)]);
        var repair = AgentTaskRunner.BuildRepairPrompt(task, [failure], new("NOT_RUN", []),
            ["Fixture.cs"], "test diff", packet, new("hash", [step]));
        if (!repair.Contains(step, StringComparison.Ordinal) ||
            !repair.Contains(packet.Markdown, StringComparison.Ordinal) ||
            !repair.Contains("CS1234", StringComparison.Ordinal))
            throw new Exception("An ordinary repair must retain approved advice, cited knowledge, and Host test evidence");

        var singleFile = AgentTaskRequest.Parse("""
            {"requirement":"Fix the public fixture","acceptance_criteria":["Tests pass"],"risk":"LOW","approved_high_risk":false,"allowed_files":["Fixture.cs"],"required_tests":[{"kind":"dotnet_test","target":"Fixture.csproj"}],"command_execution":{"max_command_seconds":5,"working_directories":["."],"environments":["windows"],"max_commands":0,"max_task_minutes":5,"output_directories":[],"max_output_bytes":4096,"max_actor_turns":3}}
            """);
        var advice = new HostRescue.PendingAdvice("hash", [step]);
        const string oneEdit = "apply them together in one host_edit";
        foreach (var prompt in new[]
        {
            AgentTaskRunner.BuildRecoveryPrompt(singleFile, "test diff", packet, advice),
            AgentTaskRunner.BuildRepairPrompt(singleFile, [failure], new("NOT_RUN", []), ["Fixture.cs"], "test diff", packet, advice)
        })
            if (!prompt.Contains(oneEdit, StringComparison.Ordinal) || !prompt.Contains(step, StringComparison.Ordinal) ||
                !prompt.Contains("test diff", StringComparison.Ordinal) || !prompt.Contains("the Host runs validation", StringComparison.Ordinal))
                throw new Exception("A scoped advisory turn must preserve evidence and prioritize one edit before Host validation");
        if (AgentTaskRunner.BuildRecoveryPrompt(singleFile, "test diff", packet).Contains(oneEdit, StringComparison.Ordinal) ||
            assisted.Contains(oneEdit, StringComparison.Ordinal))
            throw new Exception("A normal task or a different advisory scope must retain its existing prompt");

        await CheckDecisionContextAsync();
    }

    private static async Task CheckDecisionContextAsync()
    {
        using var handler = new BrainPeer { DecisionDelay = TimeSpan.FromMilliseconds(3250) };
        using var gateway = new GatewayClient(new Uri("https://fixture.invalid"), handler);
        var packet = await SecondBrainEvidence.FetchAsync(gateway, "Fix daily wall-clock scheduling", default);
        if (!packet.ChunkIds.SequenceEqual([BrainPeer.DecisionId]) ||
            !packet.Markdown.Contains("revision=" + BrainPeer.DecisionRevision, StringComparison.Ordinal) ||
            !packet.Markdown.Contains("Check both offsets", StringComparison.Ordinal) ||
            packet.Markdown.Contains('\uFFFD') || Encoding.UTF8.GetByteCount(packet.Markdown) > 1024 ||
            handler.ContextRequests != 0)
            throw new Exception("A delayed approved decision must receive the bounded context ahead of a long raw source");

        handler.DecisionDelay = TimeSpan.Zero;
        handler.Approved = false;
        packet = await SecondBrainEvidence.FetchAsync(gateway, "Fix daily wall-clock scheduling", default);
        if (packet.Markdown != "Raw reference" || handler.ContextRequests != 1)
            throw new Exception("Unapproved search results must leave general retrieval available");
    }

    private sealed class BrainPeer : HttpMessageHandler
    {
        public static readonly string DecisionId = new('a', 64);
        public static readonly string DecisionRevision = new('b', 64);
        public bool Approved { get; set; } = true;
        public TimeSpan DecisionDelay { get; set; }
        public int ContextRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/v1/brain/find-decisions")
            {
                await Task.Delay(DecisionDelay, token);
                return Reply(new
                {
                    stale_sources = Array.Empty<string>(),
                    results = new[]
                    {
                        new { id = DecisionId, revision = DecisionRevision, status = "active", score = 0.45,
                            path = "decisions/pc73-example.md", first_line = 1, last_line = 6,
                            text = "Check both offsets " + new string('年', 700),
                            metadata = new { knowledge_type = "DECISION",
                                approval_model = Approved ? "authenticated-pc73-host-attestation-v1" : "draft",
                                host_approval = new { approval_id = new string('c', 32) } } }
                    }
                });
            }

            if (request.RequestUri.AbsolutePath != "/v1/brain/context")
                throw new InvalidOperationException("Unexpected gateway route");
            ContextRequests++;
            return Reply(new { markdown = "Raw reference", chunk_ids = new[] { new string('d', 64) },
                stale_sources = Array.Empty<string>() });
        }

        private static HttpResponseMessage Reply(object body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
    }
}
