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
        var risk = request.EffectiveRisk;
        if (risk is not ("LOW" or "NORMAL" or "HIGH")) throw new InvalidDataException("Unsupported external review risk");
        var routing = new ReviewerRoutingDecision(risk == "LOW" ? ReviewerRoute.None : ReviewerRoute.Sol,
            "host_risk_rule");
        if (routing.Route == ReviewerRoute.None)
            return Task.FromResult(new Outcome(routing, new("NOT_REQUIRED", []), new("NOT_REQUIRED", []),
                true, false, false, 0, "NOT_REQUIRED"));

        var sol = new SolReviewer.Review("NOT_RUN", []);
        var copilot = new CopilotReviewer.Review("NOT_RUN", []);
        // The legacy OpenCode route does not establish subscription-only
        // authentication. Do not send content or trigger an unverified paid API.
        // A verified, authorized subscription route must be integrated first.
        token.ThrowIfCancellationRequested();
        sol = new("UNAVAILABLE", []);
        var solAttempts = 0;

        var gate = Evaluate(request.EffectiveRisk, sol, copilot);
        return Task.FromResult(new Outcome(routing, sol, copilot, gate.Passed, gate.SolRequired, gate.Unavailable,
            solAttempts, "NOT_RUN"));
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
        return new((solValid || copilotValid) &&
                (!solValid || sol.Verdict == "PASS") &&
                (!copilotValid || copilot.Verdict == "PASS"),
            solValid, !solValid && !copilotValid);
    }

}
