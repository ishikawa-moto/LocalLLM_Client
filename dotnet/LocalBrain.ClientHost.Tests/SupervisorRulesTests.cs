using LocalBrain.ClientHost.AgentV2;

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
    }
}
