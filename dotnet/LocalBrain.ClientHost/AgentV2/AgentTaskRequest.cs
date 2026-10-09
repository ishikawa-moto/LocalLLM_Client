using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LocalBrain.ClientHost.AgentV2;

internal sealed record AgentTaskRequest(
    [property: JsonPropertyName("requirement")] string Requirement,
    [property: JsonPropertyName("acceptance_criteria")] string[] AcceptanceCriteria,
    [property: JsonPropertyName("risk")] string Risk,
    [property: JsonPropertyName("approved_high_risk")] bool ApprovedHighRisk,
    [property: JsonPropertyName("allowed_files")] string[] AllowedFiles,
    [property: JsonPropertyName("required_tests")] RequiredTest[] RequiredTests)
{
    private static readonly Regex HighRisk = new(
        @"production\s+deploy|force[ -]?push|drop\s+(table|database)|delete\s+(database|account)|credential|private\s+key|billing|本番|デプロイ|強制プッシュ|削除|認証情報|秘密鍵|課金|DB移行",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public string EffectiveRisk => HighRisk.IsMatch(Requirement) ||
        AcceptanceCriteria.Any(HighRisk.IsMatch) ? "HIGH" : Risk;

    public static AgentTaskRequest Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("approved_high_risk", out var approved) ||
            approved.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("approved_high_risk must be an explicit boolean");
        var request = root.Deserialize<AgentTaskRequest>(ClientConfig.JsonOptions)
            ?? throw new InvalidDataException("Agent task request is invalid");
        if (request.RequiredTests is not null && request.RequiredTests.Any(test =>
            test.Kind is "npm_test" or "git_diff_check" &&
            !string.IsNullOrWhiteSpace(test.Target)))
            throw new InvalidDataException("npm_test and git_diff_check require target to be omitted");
        if (string.IsNullOrWhiteSpace(request.Requirement) || request.Requirement.Length > 8_000 ||
            request.AcceptanceCriteria is null or { Length: 0 or > 20 } ||
            request.AcceptanceCriteria.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 1_000) ||
            request.Risk is not ("LOW" or "NORMAL" or "HIGH") ||
            request.AllowedFiles is null or { Length: 0 or > 100 } ||
            request.AllowedFiles.Sum(file => file.Length) > 8_000 ||
            request.AllowedFiles.Any(file => string.IsNullOrWhiteSpace(file) || file.Length > 260 ||
                Path.IsPathRooted(file) || file.Split('/', '\\').Any(part => part is "." or ".." or ".git" or ".localbrain")) ||
            request.AllowedFiles.Any(file => Regex.IsMatch(Path.GetFileName(file),
                @"^(?:\.env(?:\..*)?|id_(?:rsa|ed25519)|.*\.(?:pfx|p12|key|pem))$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) ||
            request.RequiredTests is null or { Length: 0 or > 20 } ||
            request.RequiredTests.Any(test => test.Kind is not ("git_diff_check" or "dotnet_build" or
                "dotnet_test" or "npm_test") || (test.Target?.Length ?? 0) > 260 ||
                (test.Kind is "dotnet_build" or "dotnet_test"
                    ? string.IsNullOrWhiteSpace(test.Target) || Path.IsPathRooted(test.Target) ||
                        Path.GetExtension(test.Target) != ".csproj" ||
                        test.Target.Split('/', '\\').Any(part => part is "." or ".." or ".git" or ".localbrain")
                    : !string.IsNullOrWhiteSpace(test.Target))))
            throw new InvalidDataException("Agent task request is incomplete or exceeds limits");
        if (request.EffectiveRisk == "HIGH" && !request.ApprovedHighRisk)
            throw new UnauthorizedAccessException("High-risk task requires explicit user approval");
        return request;
    }
}

internal sealed record RequiredTest(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("target")] string? Target);
