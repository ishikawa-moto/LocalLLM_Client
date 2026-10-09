using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalBrain.ClientHost.AgentV2;

// Local intake only. Ownership confirmation never grants proposal export or knowledge adoption.
internal sealed class HumanLearning(CanonicalStore store, string taskId)
{
    internal sealed record Input(string request_id, string title, string body, string project);
    internal sealed record Manifest(int schema_version, string task_id, string source_event_id,
        string source_content_hash, string[] constituent_source_ids, string authority,
        string authority_scope, Input decision, string adoption_status, bool ask_first_required);

    internal static Input Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > 16_000) throw new InvalidDataException("Human learning input exceeds Host budget");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a decision object");
        var keys = root.EnumerateObject().Select(p => p.Name).ToArray();
        if (keys.Length != 4 || !keys.Order(StringComparer.Ordinal).SequenceEqual(
                new[] { "body", "project", "request_id", "title" }, StringComparer.Ordinal))
            throw new InvalidDataException("Decision requires exactly request_id/title/body/project; no authority flags");
        string Text(string key, int limit)
        {
            var value = root.GetProperty(key);
            if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Decision fields must be text");
            var text = value.GetString()!;
            if (string.IsNullOrWhiteSpace(text) || text.Length > limit || text.Contains('\0'))
                throw new InvalidDataException("Decision field is blank, excessive or invalid");
            return text;
        }
        var input = new Input(Text("request_id", 80), Text("title", 200), Text("body", 8_000), Text("project", 80));
        if (!Regex.IsMatch(input.request_id, "\\A[A-Za-z0-9_-]{8,80}\\z", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(input.project, "\\A[A-Za-z0-9_.-]{1,80}\\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Invalid decision identity or project");
        if (HostEgress.Redact(JsonSerializer.Serialize(input)).Count != 0)
            throw new UnauthorizedAccessException("Remove sensitive material before decision intake");
        return input;
    }

    internal static string ContentHash(Input input) => CanonicalStore.Hash(JsonSerializer.SerializeToUtf8Bytes(input));
    internal static bool MatchesOwnershipConfirmation(Input input, string? confirmation) =>
        confirmation == "I OWN THIS DECISION " + ContentHash(input);

    public string CaptureInteractive(Input input)
    {
        // Neither repository files nor a model may claim that a quotation is a human-owned decision.
        input = Parse(JsonSerializer.Serialize(input));
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new UnauthorizedAccessException("Human ownership requires an interactive Host terminal");
        var task = store.GetTask(taskId) ?? throw new InvalidDataException("Unknown decision task");
        var hash = ContentHash(input);
        Console.WriteLine(JsonSerializer.Serialize(input, ClientConfig.JsonOptions));
        Console.WriteLine("Confirm only a decision you personally own. Quoted model/third-party claims remain candidates.");
        Console.WriteLine("This records ownership only; it does not approve export or knowledge adoption.");
        Console.WriteLine("Type: I OWN THIS DECISION " + hash);
        if (!MatchesOwnershipConfirmation(input, Console.ReadLine()))
            throw new UnauthorizedAccessException("Exact human ownership was not confirmed");
        store.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(input));
        var receipt = store.Append(new(taskId, task.WorkspaceId, "windows_host", "human_" + input.request_id,
            "human_owned_decision_confirmed", "human_statement", "human_explicit", [hash],
            JsonSerializer.SerializeToElement(new { input_hash = hash, human_actor = Environment.UserName,
                confirmation_scope = "OWN_DECISION_ONLY_NO_ADOPTION" })));
        return FromSourceEvent(receipt.EventId);
    }

    // Source classification follows canonical Host provenance, never words supplied in the decision body.
    private Manifest BuildManifest(string sourceEventId)
    {
        _ = store.GetTask(taskId) ?? throw new InvalidDataException("Unknown decision task");
        var source = store.ReadTaskEvent(taskId, sourceEventId);
        if (source.Input.EvidenceRefs.Length != 1) throw new InvalidDataException("Intake source must contain one exact input");
        var inputHash = source.Input.EvidenceRefs[0];
        var input = Parse(new UTF8Encoding(false, true).GetString(store.ReadEvidence(inputHash)));
        if (ContentHash(input) != inputHash) throw new InvalidDataException("Decision input is not canonical");
        var metadata = source.Input.Metadata;
        var owned = source.Input.Producer == "windows_host" && source.Input.WorkerId is null && source.Input.ProducerEventId is null &&
            source.Input.EventType == "human_owned_decision_confirmed" &&
            source.Input.EvidenceClass == "human_statement" && source.Input.VerificationStatus == "human_explicit" &&
            metadata.ValueKind == JsonValueKind.Object &&
            metadata.TryGetProperty("input_hash", out var binding) && binding.ValueKind == JsonValueKind.String && binding.GetString() == inputHash &&
            metadata.TryGetProperty("human_actor", out var actor) && actor.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(actor.GetString()) &&
            metadata.TryGetProperty("confirmation_scope", out var scope) && scope.ValueKind == JsonValueKind.String && scope.GetString() == "OWN_DECISION_ONLY_NO_ADOPTION";
        var authority = owned ? "human_explicit" : source.Input.EvidenceClass == "model_claim" || source.Input.VerificationStatus == "model_claim" ? "model_claim" : "unverified";
        return new Manifest(1, taskId, sourceEventId, inputHash, ["event:" + sourceEventId, "blob:" + inputHash],
            authority, owned ? "human_owned_decision_not_implementation_fact" : "candidate_not_truth", input,
            "LOCAL_ONLY_NOT_ADOPTED", true);
    }

    public string FromSourceEvent(string sourceEventId)
    {
        var task = store.GetTask(taskId) ?? throw new InvalidDataException("Unknown decision task");
        var manifest = BuildManifest(sourceEventId);
        var hash = store.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(manifest));
        store.Append(new(taskId, task.WorkspaceId, "windows_host", "human_learning_" + sourceEventId,
            "human_learning_manifest_saved", "learning_candidate", manifest.authority, [hash, manifest.source_content_hash],
            JsonSerializer.SerializeToElement(new { manifest_hash = hash, source_event_id = sourceEventId,
                authority = manifest.authority, adoption_status = manifest.adoption_status }), CausedByEventId: sourceEventId));
        return hash;
    }

    public Manifest RequireManifest(string hash)
    {
        var receipt = store.EventIds(taskId).Select(id => store.ReadTaskEvent(taskId, id)).SingleOrDefault(e =>
            e.Input.Producer == "windows_host" && e.Input.EventType == "human_learning_manifest_saved" &&
            e.Input.EvidenceRefs.Length == 2 && e.Input.EvidenceRefs[0] == hash)
            ?? throw new UnauthorizedAccessException("Manifest is not registered to this task");
        var manifest = JsonSerializer.Deserialize<Manifest>(store.ReadEvidence(hash)) ?? throw new InvalidDataException("Manifest missing");
        if (manifest.task_id != taskId || receipt.Input.CausedByEventId != manifest.source_event_id ||
            receipt.Input.EvidenceRefs[1] != manifest.source_content_hash ||
            CanonicalStore.Hash(JsonSerializer.SerializeToUtf8Bytes(BuildManifest(manifest.source_event_id))) != hash)
            throw new InvalidDataException("Human learning manifest/source binding changed");
        return manifest;
    }
}
