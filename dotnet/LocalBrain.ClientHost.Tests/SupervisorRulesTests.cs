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
    }
}
