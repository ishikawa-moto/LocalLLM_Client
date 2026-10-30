using LocalBrain.ClientHost.AgentV2;

internal static class ExternalReviewAvailabilityTests
{
    internal static async Task RunAsync()
    {
        var request=AgentTaskRequest.Parse("""{"requirement":"Fixture task","acceptance_criteria":["check"],"risk":"NORMAL","approved_high_risk":false,"allowed_files":["fixture.txt"],"required_tests":[{"kind":"git_diff_check"}]}""");
        var result=await ExternalReviewCoordinator.RunAsync(request,"",[],[],new("",[]),CancellationToken.None);
        if(result.Passed || !result.Unavailable || result.Sol.Verdict!="UNAVAILABLE" || result.SolFinalAttempts!=0)
            throw new Exception("Unverified review route must remain unavailable.");
        try { await SolReviewer.HealthAsync(); throw new Exception("Unverified route started."); }
        catch(InvalidOperationException e) when(e.Message=="Sol subscription route is not verified") { }
    }
}
