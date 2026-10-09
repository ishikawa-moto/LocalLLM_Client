using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalBrain.ClientHost.AgentV2;

internal sealed record LocalValidationV2(
    [property: JsonPropertyName("required_tests_passed")] bool RequiredTestsPassed,
    [property: JsonPropertyName("acceptance_criteria_passed")] bool AcceptanceCriteriaPassed,
    [property: JsonPropertyName("local_review_passed")] bool LocalReviewPassed,
    [property: JsonPropertyName("sol_review_required")] bool SolReviewRequired,
    [property: JsonPropertyName("sol_review_passed")] bool SolReviewPassed,
    [property: JsonPropertyName("unexpected_files")] string[]? UnexpectedFiles,
    [property: JsonPropertyName("false_verified_detected")] bool FalseVerifiedDetected,
    [property: JsonPropertyName("external_review_required")] bool ExternalReviewRequired = false,
    [property: JsonPropertyName("external_review_passed")] bool ExternalReviewPassed = false,
    [property: JsonPropertyName("review_route")] string ReviewRoute = "legacy")
{
    [JsonIgnore]
    public bool IsComplete => RequiredTestsPassed && AcceptanceCriteriaPassed && LocalReviewPassed &&
        (!SolReviewRequired || SolReviewPassed) &&
        (!ExternalReviewRequired || ExternalReviewPassed) &&
        (UnexpectedFiles?.Length ?? 0) == 0 && !FalseVerifiedDetected;

    public static LocalValidationV2 Read(string workspace)
    {
        var path = Path.Combine(workspace, ".localbrain", "local-validation.json");
        if (!File.Exists(path)) throw new FileNotFoundException("Local validation is missing", path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        foreach (var key in new[] { "required_tests_passed", "acceptance_criteria_passed", "local_review_passed",
            "sol_review_required", "sol_review_passed", "false_verified_detected" })
            if (!root.TryGetProperty(key, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException($"Local validation requires boolean {key}");
        if (!root.TryGetProperty("unexpected_files", out var unexpected) || unexpected.ValueKind != JsonValueKind.Array ||
            unexpected.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new InvalidDataException("Local validation requires an unexpected_files string array");
        foreach (var key in new[] { "external_review_required", "external_review_passed" })
            if (root.TryGetProperty(key, out var value) &&
                value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException($"Local validation requires boolean {key}");
        if (root.TryGetProperty("review_route", out var route) &&
            (route.ValueKind != JsonValueKind.String || route.GetString() is not
                ("legacy" or "none" or "sol" or "copilot" or "both")))
            throw new InvalidDataException("Local validation review route is invalid");
        return JsonSerializer.Deserialize<LocalValidationV2>(root.GetRawText())
            ?? throw new InvalidDataException("Local validation is invalid");
    }

    public async Task WriteAsync(string workspace, CancellationToken token = default)
    {
        HostTaskJournal.Current?.SaveValidation(this);
        var directory = Path.Combine(workspace, ".localbrain");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "local-validation.json");
        var temporary = Path.Combine(directory, $"local-validation.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(this, ClientConfig.JsonOptions), token);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static LocalValidationV2? FromCanonical(JsonElement state) =>
        state.TryGetProperty("validation", out var value) ? value.Deserialize<LocalValidationV2>() : null;

    internal static LocalValidationV2 ReadCanonical(string workspace)
    {
        var snapshot = HostTaskJournal.ReadLatest(workspace)
            ?? throw new InvalidDataException("Canonical validation is unavailable; legacy evidence requires revalidation");
        using var state = JsonDocument.Parse(snapshot.StateJson);
        if (!state.RootElement.TryGetProperty("phase", out var phase) || phase.GetString() != "complete")
            throw new InvalidDataException("Canonical task is not complete");
        return FromCanonical(state.RootElement) ?? throw new InvalidDataException("Canonical validation is missing");
    }
}
