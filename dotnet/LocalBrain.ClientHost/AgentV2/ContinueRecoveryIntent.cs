using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LocalBrain.ClientHost.AgentV2;

internal sealed record ContinueRecoveryIntent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("knowledge")] ContinueRecoveryKnowledgeScope? Knowledge)
{
    internal string DecisionId => "continue-recovery-" + Id;

    internal static AgentTaskRequest Read(JsonElement arguments)
    {
        var hasKnowledge = arguments.TryGetProperty("knowledge", out var knowledge);
        DecisionProtocol.Keys(arguments, hasKnowledge
            ? ["workspace", "recovery_id", "task", "knowledge"] : ["workspace", "recovery_id", "task"]);
        var task = arguments.GetProperty("task");
        DecisionProtocol.Keys(task, "requirement", "acceptance_criteria", "risk", "approved_high_risk",
            "allowed_files", "required_tests", "command_execution");
        ContinueRecoveryKnowledgeScope? scope = null;
        if (hasKnowledge)
        {
            DecisionProtocol.Keys(knowledge, "project", "references");
            scope = new(DecisionProtocol.Text(knowledge, "project", 80), knowledge.GetProperty("references").Clone());
        }
        var intent = new ContinueRecoveryIntent(DecisionProtocol.Text(arguments, "recovery_id", 32), scope);
        // Reject duplicate fields throughout the user-supplied contract before normal deserialization.
        _ = DecisionProtocol.Parse(task.GetRawText());
        var request = AgentTaskRequest.Parse(task.GetRawText()) with { ContinueRecovery = intent };
        intent.Validate(request);
        return request;
    }

    internal void Validate(AgentTaskRequest request)
    {
        if (Id is null || !Guid.TryParseExact(Id, "N", out var guid) || Id != guid.ToString("N") || guid == Guid.Empty ||
            request.EffectiveRisk != "NORMAL" || request.ApprovedHighRisk || request.ReadOnly || request.ExternalAiDisabled ||
            request.CommandExecution is not { MaxCommands: 0, MaxTaskMinutes: <= 20, MaxActorTurns: <= 12,
                OutputDirectories.Length: 0, FusedValidation: null } ||
            request.AllowedFiles.Any(path => !ExactFile(path)) ||
            !request.RequiredTests.Any(test => test.Kind is "dotnet_test" or "npm_test") ||
            request.RequiredTests.Any(test => test.Kind is not ("dotnet_test" or "npm_test" or "git_diff_check") ||
                test.Target is not null && !ExactFile(test.Target)))
            throw new InvalidDataException("Continue recovery requires an exact NORMAL file scope, finite budget, and executable tests");

        if (Knowledge is not null)
        {
            if (Knowledge.Project is null || !Regex.IsMatch(Knowledge.Project, @"\A[A-Za-z0-9_.-]{1,80}\z"))
                throw new InvalidDataException("Invalid recovery knowledge project");
            DecisionProtocol.References(Knowledge.References);
        }
    }

    private static bool ExactFile(string value) => value.IndexOfAny(['*', '?', ':', '\0', '\r', '\n']) < 0 &&
        value.Split('/', '\\').All(part => part.Length > 0 && part[^1] is not (' ' or '.') &&
            !part.Equals(".git", StringComparison.OrdinalIgnoreCase) && !part.Equals(".localbrain", StringComparison.OrdinalIgnoreCase));
}

internal sealed record ContinueRecoveryKnowledgeScope(
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("references")] JsonElement References);
