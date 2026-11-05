namespace LocalBrain.ClientHost.AgentV2;

/// <summary>Routes final reviews without changing the local Critic or HIGH approval gates.</summary>
internal static class ExternalReviewCoordinator
{
    internal sealed record Gate(bool Passed, bool SolRequired, bool Unavailable);
    internal sealed record Outcome(ReviewerRoutingDecision Routing,
        SolReviewer.Review Sol, CopilotReviewer.Review Copilot,
        bool Passed, bool SolRequired, bool Unavailable, int SolFinalAttempts,
        string AstraStatus);

    public static Task<Outcome> RunAsync(AgentTaskRequest request, string reviewDiff,
        string[] changedFiles, IReadOnlyList<ToolRouter.TestResult> tests,
        SecondBrainEvidence.Packet knowledge, CancellationToken token)
    {
        return RouteAsync(request.EffectiveRisk,
            () => SolReviewer.ReviewFinalAsync(request,reviewDiff,changedFiles,tests,knowledge,token),
            () => CopilotReviewer.ReviewFinalAsync(ReviewerEvidence.Build(request,reviewDiff,changedFiles,tests,knowledge),
                token,sources:knowledge.ChunkIds,files:changedFiles),token);
    }

    internal static async Task<Outcome> RouteAsync(string risk, Func<Task<SolReviewer.Review>> solReview,
        Func<Task<CopilotReviewer.Review>> opusReview,CancellationToken token)
    {
        if (risk is not ("LOW" or "NORMAL" or "HIGH")) throw new InvalidDataException("Unsupported external review risk");
        var routing = new ReviewerRoutingDecision(risk == "LOW" ? ReviewerRoute.None : ReviewerRoute.Sol,
            "host_risk_rule");
        if (routing.Route == ReviewerRoute.None)
            return new Outcome(routing, new("NOT_REQUIRED", []), new("NOT_REQUIRED", []),
                true, false, false, 0, "NOT_REQUIRED");

        var sol = new SolReviewer.Review("NOT_RUN", []);
        var copilot = new CopilotReviewer.Review("NOT_RUN", []);
        token.ThrowIfCancellationRequested();
        Slack.RestrictedExecution.DenyExternal();
        sol = await solReview();
        // Never shop for PASS after ISSUES/INVALID, or bypass approval/egress denial.
        // HIGH retains its existing mandatory Sol plan/final gates.
        if(risk=="NORMAL" && sol.Verdict=="UNAVAILABLE") {
            copilot=await opusReview();
            routing=new(ReviewerRoute.Copilot,"sol_unavailable_opus_fallback");
        }
        var solAttempts = sol.Attempts;

        var gate = Evaluate(risk, sol, copilot);
        return new Outcome(routing, sol, copilot, gate.Passed, gate.SolRequired, gate.Unavailable,
            solAttempts, "NOT_REQUIRED");
    }

    internal static Gate Evaluate(string risk, SolReviewer.Review sol,
        CopilotReviewer.Review copilot)
    {
        var solValid = sol.Verdict is "PASS" or "ISSUES";
        var copilotValid = copilot.Verdict is "PASS" or "ISSUES";
        if (risk == "HIGH")
            return new(sol.Verdict == "PASS" && (!copilotValid || copilot.Verdict == "PASS"),
                true, !solValid);
        if (risk != "NORMAL") throw new InvalidDataException("Unsupported external review risk");
        return new(sol.Verdict!="INVALID" && copilot.Verdict!="INVALID" && (solValid || copilotValid) &&
                (!solValid || sol.Verdict == "PASS") &&
                (!copilotValid || copilot.Verdict == "PASS"),
            solValid, !solValid && !copilotValid);
    }

}
