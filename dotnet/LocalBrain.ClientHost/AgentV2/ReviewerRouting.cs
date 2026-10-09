using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalBrain.ClientHost.AgentV2;

internal enum ReviewerRoute { None, Sol, Copilot, Both }

internal sealed record ReviewerRoutingSettings(
    [property: JsonPropertyName("low_confidence_threshold")] double LowConfidenceThreshold,
    [property: JsonPropertyName("calibration_samples")] int CalibrationSamples)
{
    public static ReviewerRoutingSettings Read()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "reviewer-routing.json");
        if (!File.Exists(path)) throw new FileNotFoundException("Reviewer routing policy is missing", path);
        var info = new FileInfo(path);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > 4_096)
            throw new InvalidDataException("Reviewer routing policy is linked or oversized");
        var settings = JsonSerializer.Deserialize<ReviewerRoutingSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Reviewer routing policy is invalid");
        if (!double.IsFinite(settings.LowConfidenceThreshold) ||
            settings.LowConfidenceThreshold is < 0.5 or > 0.95 ||
            settings.CalibrationSamples is < 0 or > 20)
            throw new InvalidDataException("Reviewer routing policy is outside safe bounds");
        return settings;
    }
}

internal sealed record ReviewerRoutingDecision(ReviewerRoute Route, string Source,
    double? LayaConfidence, double CopilotWeight, double SolWeight)
{
    public string RouteName => Route.ToString().ToLowerInvariant();
}

internal static class ReviewerRouting
{
    public static ReviewerRoutingDecision Decide(string risk, string? layaRoute,
        double? confidence, ReviewerRoutingSettings settings, ReviewerCalibration calibration)
    {
        if (risk == "LOW") return new(ReviewerRoute.None, "hard_rule_low", confidence,
            calibration.CopilotWeight, calibration.SolWeight);
        if (risk is not ("NORMAL" or "HIGH"))
            throw new InvalidDataException("Unsupported reviewer risk");
        if (confidence is null || !double.IsFinite(confidence.Value) ||
            confidence.Value < settings.LowConfidenceThreshold ||
            layaRoute is not ("copilot" or "sol" or "both"))
            return new(ReviewerRoute.Both, "low_confidence_or_unavailable", confidence,
                calibration.CopilotWeight, calibration.SolWeight);
        if (risk == "HIGH")
            return new(layaRoute == "both" ? ReviewerRoute.Both : ReviewerRoute.Sol,
                "high_risk_sol_required", confidence, calibration.CopilotWeight,
                calibration.SolWeight);
        if (layaRoute == "both") return new(ReviewerRoute.Both, "laya", confidence,
            calibration.CopilotWeight, calibration.SolWeight);

        // Astra can only influence borderline NORMAL choices. Strong Laya choices
        // are never overruled by a small calibration sample.
        var proposed = layaRoute == "copilot" ? ReviewerRoute.Copilot : ReviewerRoute.Sol;
        var alternative = proposed == ReviewerRoute.Copilot ? ReviewerRoute.Sol : ReviewerRoute.Copilot;
        var proposedWeight = proposed == ReviewerRoute.Copilot ? calibration.CopilotWeight : calibration.SolWeight;
        var alternativeWeight = proposed == ReviewerRoute.Copilot ? calibration.SolWeight : calibration.CopilotWeight;
        if (confidence.Value < 0.8 && calibration.Samples >= 3 &&
            alternativeWeight - proposedWeight >= 0.15)
            return new(alternative, "astra_calibration", confidence,
                calibration.CopilotWeight, calibration.SolWeight);
        return new(proposed, "laya", confidence,
            calibration.CopilotWeight, calibration.SolWeight);
    }
}
