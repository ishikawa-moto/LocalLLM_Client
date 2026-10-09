using System.Text.RegularExpressions;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

/// <summary>Routes final reviews without changing the local Critic or HIGH approval gates.</summary>
internal static class ExternalReviewCoordinator
{
    internal sealed record Gate(bool Passed, bool SolRequired, bool Unavailable);
    internal sealed record Outcome(ReviewerRoutingDecision Routing,
        SolReviewer.Review Sol, CopilotReviewer.Review Copilot,
        bool Passed, bool SolRequired, bool Unavailable, int SolFinalAttempts,
        string AstraStatus);

    public static async Task<Outcome> RunAsync(AgentTaskRequest request, string reviewDiff,
        string[] changedFiles, IReadOnlyList<ToolRouter.TestResult> tests,
        SecondBrainEvidence.Packet knowledge, LayaSupervisor supervisor,
        CancellationToken token, string? forcedRoute = null)
    {
        var settings = ReviewerRoutingSettings.Read();
        ReviewerCalibration calibration;
        try { calibration = ReviewerCalibration.Read(); }
        catch (Exception error) when (error is InvalidDataException or JsonException or IOException)
        { calibration = new(0, 0, 0.5, 0.5); }
        string? layaRoute = forcedRoute;
        double? confidence = forcedRoute is null ? null : 1;
        if (forcedRoute is null && request.EffectiveRisk != "LOW")
        {
            try
            {
                var language = string.Join(',', changedFiles.Select(Path.GetExtension)
                    .Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(6));
                var state = new {
                    risk = request.EffectiveRisk.ToLowerInvariant(),
                    files_changed = changedFiles.Length,
                    diff_lines = Math.Min(10_000, reviewDiff.Count(character => character == '\n')),
                    tests_passed = tests.All(test => test.Passed),
                    languages = language.Length > 80 ? language[..80] : language,
                    task_kind = TaskKind(request.Requirement),
                    public_api = request.Requirement.Contains("public api", StringComparison.OrdinalIgnoreCase),
                    security = request.EffectiveRisk == "HIGH",
                    concurrency = request.Requirement.Contains("concurrency", StringComparison.OrdinalIgnoreCase)
                };
                var prediction = await supervisor.PredictReviewRouteAsync(state, token);
                layaRoute = prediction.Route;
                confidence = prediction.Confidence;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or InvalidDataException or
                InvalidOperationException or OperationCanceledException or KeyNotFoundException)
            { /* Missing or uncertain Laya routing conservatively selects both. */ }
        }
        var routing = ReviewerRouting.Decide(request.EffectiveRisk, layaRoute,
            confidence, settings, calibration);
        if (routing.Route == ReviewerRoute.None)
            return new(routing, new("NOT_REQUIRED", []), new("NOT_REQUIRED", []),
                true, false, false, 0, "NOT_REQUIRED");

        // The packet is built once so parallel reviewers never see each other's answer.
        var packet = ReviewerEvidence.Build(request, reviewDiff, changedFiles, tests, knowledge);
        var sol = new SolReviewer.Review("NOT_RUN", []);
        var copilot = new CopilotReviewer.Review("NOT_RUN", []);
        var solAttempts = 0;
        if (routing.Route is ReviewerRoute.Sol or ReviewerRoute.Both)
        {
            sol = await RunSolAsync(request, reviewDiff, changedFiles, tests, knowledge, token);
            solAttempts++;
        }
        if (routing.Route is ReviewerRoute.Copilot or ReviewerRoute.Both)
            copilot = await RunCopilotAsync(packet, token);
        if (routing.Route == ReviewerRoute.Copilot && copilot.Verdict == "UNAVAILABLE" ||
            routing.Route == ReviewerRoute.Both && sol.Verdict == "UNAVAILABLE" &&
                copilot.Verdict == "UNAVAILABLE")
        {
            sol = await RunSolAsync(request, reviewDiff, changedFiles, tests, knowledge, token);
            solAttempts++;
        }
        if (routing.Route == ReviewerRoute.Sol && sol.Verdict == "UNAVAILABLE")
            copilot = await RunCopilotAsync(packet, token);

        var solValid = sol.Verdict is "PASS" or "ISSUES";
        var copilotValid = copilot.Verdict is "PASS" or "ISSUES";
        var gate = Evaluate(request.EffectiveRisk, sol, copilot);

        var astraStatus = "NOT_RUN";
        if (routing.Route == ReviewerRoute.Both && solValid && copilotValid &&
            calibration.Attempts < settings.CalibrationSamples)
        {
            AstraMetaEvaluator.Comparison? comparison = null;
            try
            {
                comparison = await AstraMetaEvaluator.CompareAsync(packet, sol, copilot, token);
                astraStatus = comparison is null ? "UNAVAILABLE" : "RECORDED";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or InvalidDataException or
                InvalidOperationException or OperationCanceledException or
                System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            { astraStatus = "UNAVAILABLE"; }
            try { await ReviewerCalibration.RecordAsync(comparison, settings, token); }
            catch (Exception error) when (error is IOException or InvalidDataException or
                UnauthorizedAccessException)
            { astraStatus = "UNAVAILABLE"; }
        }
        return new(routing, sol, copilot, gate.Passed, gate.SolRequired, gate.Unavailable,
            solAttempts, astraStatus);
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

    private static string TaskKind(string requirement) =>
        Regex.IsMatch(requirement, @"\b(?:fix|repair|bug)\b|修正|不具合", RegexOptions.IgnoreCase)
            ? "bug_fix" : Regex.IsMatch(requirement, @"\brefactor\b|リファクタ", RegexOptions.IgnoreCase)
                ? "refactor" : "implementation";

    private static async Task<SolReviewer.Review> RunSolAsync(AgentTaskRequest request,
        string diff, string[] files, IReadOnlyList<ToolRouter.TestResult> tests,
        SecondBrainEvidence.Packet knowledge, CancellationToken token)
    {
        try { return await SolReviewer.ReviewFinalAsync(request, diff, files, tests, knowledge, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or
            InvalidOperationException or OperationCanceledException or
            System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { return new("UNAVAILABLE", []); }
    }

    private static async Task<CopilotReviewer.Review> RunCopilotAsync(string packet,
        CancellationToken token)
    {
        try { return await CopilotReviewer.ReviewFinalAsync(packet, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or
            InvalidOperationException or OperationCanceledException or
            System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { return new("UNAVAILABLE", []); }
    }
}
