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

        await CheckDecisionContextAsync();
    }

    private static async Task CheckDecisionContextAsync()
    {
        using var handler = new BrainPeer();
        using var gateway = new GatewayClient(new Uri("https://fixture.invalid"), handler);
        var packet = await SecondBrainEvidence.FetchAsync(gateway, "Fix daily wall-clock scheduling", default);
        if (!packet.ChunkIds.SequenceEqual([BrainPeer.DecisionId]) ||
            !packet.Markdown.Contains("revision=" + BrainPeer.DecisionRevision, StringComparison.Ordinal) ||
            !packet.Markdown.Contains("Check both offsets", StringComparison.Ordinal) ||
            packet.Markdown.Contains('\uFFFD') || Encoding.UTF8.GetByteCount(packet.Markdown) > 1024 ||
            handler.ContextRequests != 0)
            throw new Exception("The approved decision must receive the bounded context ahead of a long raw source");

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
        public int ContextRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/v1/brain/find-decisions")
            {
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

        private static Task<HttpResponseMessage> Reply(object body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            });
    }
}
