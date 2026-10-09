using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalBrain.ClientHost.AgentV2;

// Implements the verified ServerPC proposal-adoption-v1 contract. Never a model tool.
internal static class DecisionProtocol
{
    internal const string ProposalSchema = "localbrain.decision-proposal.v1";
    internal const string ReviewSchema = "localbrain.reviewed-decision.v1";
    internal const string Owner = "pc73-windows-host";
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    internal static void Keys(JsonElement value, params string[] names) {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Unknown, missing or duplicate decision fields");
    }
    internal static string Text(JsonElement value, string key, int max) {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.String) throw new InvalidDataException("Decision field must be present text");
        var text = field.GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > max || text.Contains('\0')) throw new InvalidDataException("Invalid decision text");
        _ = Utf8.GetBytes(text); return text;
    }
    internal static string Id(string value) {
        if (!Regex.IsMatch(value, "\\A[A-Za-z0-9_-]{8,80}\\z", RegexOptions.CultureInvariant)) throw new InvalidDataException("Invalid decision identity");
        return value;
    }
    internal static string HashText(string value) {
        if (!Regex.IsMatch(value, "\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant)) throw new InvalidDataException("Invalid decision hash");
        return value;
    }
    internal static JsonElement Parse(string json) {
        if (Utf8.GetByteCount(json) > 250_000) throw new InvalidDataException("Decision exceeds protocol budget");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var value = document.RootElement.Clone(); _ = Canonical(value); return value;
    }
    // Python ensure_ascii=False / sort_keys=True / separators=(',', ':'). Do not
    // substitute a .NET encoder: it escapes supplementary scalars differently.
    internal static byte[] Canonical(JsonElement value) {
        var output = new StringBuilder();
        void String(string text) {
            _ = Utf8.GetBytes(text); output.Append('"');
            foreach (var ch in text) {
                output.Append(ch switch { '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f",
                    '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ when ch < 32 => "\\u" + ((int)ch).ToString("x4"), _ => ch.ToString() });
            }
            output.Append('"');
        }
        void Write(JsonElement node) {
            switch (node.ValueKind) {
                case JsonValueKind.Object:
                    var properties = node.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                    if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
                        properties.Any(p => p.Name.Any(ch => ch > 127))) throw new InvalidDataException("Decision keys must be unique ASCII");
                    output.Append('{');
                    for (var i = 0; i < properties.Length; i++) { if (i > 0) output.Append(','); String(properties[i].Name); output.Append(':'); Write(properties[i].Value); }
                    output.Append('}'); break;
                case JsonValueKind.Array:
                    output.Append('['); var first = true;
                    foreach (var item in node.EnumerateArray()) { if (!first) output.Append(','); first = false; Write(item); }
                    output.Append(']'); break;
                case JsonValueKind.String: String(node.GetString()!); break;
                case JsonValueKind.Number:
                    if (!node.TryGetInt64(out var number) || node.GetRawText().IndexOfAny(['.', 'e', 'E']) >= 0) throw new InvalidDataException("Only integer decision fields are supported");
                    output.Append(number.ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
                case JsonValueKind.True: output.Append("true"); break;
                case JsonValueKind.False: output.Append("false"); break;
                case JsonValueKind.Null: output.Append("null"); break;
                default: throw new InvalidDataException("Invalid decision value");
            }
        }
        Write(value); return Utf8.GetBytes(output.ToString());
    }
    internal static string Digest(JsonElement value) => CanonicalStore.Hash(Canonical(value));
    internal static string ReviewHash(string operation, JsonElement payload) => Digest(JsonSerializer.SerializeToElement(new { schema = ReviewSchema, operation, payload }));
    internal static void References(JsonElement refs) {
        if (refs.ValueKind != JsonValueKind.Array || refs.GetArrayLength() is < 1 or > 30) throw new InvalidDataException("Decision needs bounded reference snapshots");
        foreach (var reference in refs.EnumerateArray()) {
            Keys(reference, "path", "expected_hash", "expected_metadata_hash", "relevant_location");
            var path = Text(reference, "path", 239);
            if (path.StartsWith('/') || path.Contains('\\') || path.Contains(':') || path.Split('/').Any(p => p is "" or "." or "..")) throw new InvalidDataException("Invalid reference path");
            HashText(Text(reference, "expected_hash", 64)); HashText(Text(reference, "expected_metadata_hash", 64));
            var location = Text(reference, "relevant_location", 32);
            if (!Regex.IsMatch(location, "\\AL[1-9][0-9]*(?:-L?[1-9][0-9]*)?\\z", RegexOptions.CultureInvariant)) throw new InvalidDataException("Invalid reference location");
        }
    }
    internal static JsonElement Proposal(JsonElement receipt) {
        var allowed = new[] { "proposal_id", "proposal_hash", "schema", "status", "payload", "content", "path", "file_hashes" };
        if (receipt.TryGetProperty("duplicate", out var duplicate)) {
            Keys(receipt, [..allowed, "duplicate"]);
            if (duplicate.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Invalid proposal duplicate flag");
        } else Keys(receipt, allowed);
        var payload = receipt.GetProperty("payload");
        Keys(payload, "schema", "proposal_id", "kind", "project", "title", "body", "references");
        var id = Id(Text(payload, "proposal_id", 80));
        if (Text(payload, "schema", 64) != ProposalSchema || Text(receipt, "schema", 64) != ProposalSchema ||
            Text(payload, "kind", 16) != "decision" || Text(receipt, "proposal_id", 80) != id || Text(receipt, "status", 16) != "draft") throw new InvalidDataException("Proposal is not an adoptable bound draft");
        var project = Text(payload, "project", 80);
        if (!Regex.IsMatch(project, "\\A[A-Za-z0-9_.-]{1,80}\\z", RegexOptions.CultureInvariant)) throw new InvalidDataException("Unsupported proposal project");
        _ = Text(payload, "title", 200); var body = Text(payload, "body", 200_000);
        if (Utf8.GetByteCount(body) > 200_000 || Text(receipt, "content", 200_000) != body) throw new InvalidDataException("Proposal body mapping differs");
        References(payload.GetProperty("references"));
        var digest = Digest(payload);
        if (HashText(Text(receipt, "proposal_hash", 64)) != digest || Text(receipt, "path", 239) != "drafts/pending-review/" + id + ".md") throw new InvalidDataException("Proposal hash/path mismatch");
        var hashes = receipt.GetProperty("file_hashes");
        if (hashes.ValueKind != JsonValueKind.Array || hashes.GetArrayLength() != 3) throw new InvalidDataException("Proposal file hashes missing");
        foreach (var hash in hashes.EnumerateArray()) HashText(hash.GetString()!);
        if (hashes[0].GetString() != CanonicalStore.Hash(Utf8.GetBytes("# " + payload.GetProperty("title").GetString() + "\n\n" + body + "\n")) || hashes[2].GetString() != digest) throw new InvalidDataException("Proposal raw byte mapping mismatch");
        return payload.Clone();
    }
}

internal sealed class DecisionAdoption(CanonicalStore store, string taskId)
{
    internal sealed record Intent(int version, string task_id, string operation, string source_hash, string? source_event_id,
        string decision_hash, JsonElement payload);
    internal sealed record Reservation(string ApprovalId, string IntentHash, string RequestHash, string Route, string Json, string? ResultHash, int? ResultStatus);

    private CanonicalStore.TaskSnapshot Task() => store.GetTask(taskId) ?? throw new InvalidDataException("Unknown adoption task");
    private CanonicalStore.EventInput Event(string key, string type, string status, string[] hashes, object metadata, string? cause = null) =>
        new(taskId, Task().WorkspaceId, "windows_host", key, type, "decision_adoption_audit", status, hashes, JsonSerializer.SerializeToElement(metadata), CausedByEventId: cause);
    private string Save(Intent intent) {
        var hash = store.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(intent));
        store.Append(Event("adoption_prepare_" + hash, "decision_adoption_prepared", "unapproved", [hash, intent.source_hash],
            new { intent_hash = hash, intent.decision_hash, operation = intent.operation }, intent.source_event_id));
        return hash;
    }
    internal string PrepareProposal(string receiptJson, string requestId) {
        _ = Task(); DecisionProtocol.Id(requestId);
        var receipt = DecisionProtocol.Parse(receiptJson); var proposal = DecisionProtocol.Proposal(receipt);
        if (HostEgress.Redact(receiptJson).Count != 0) throw new UnauthorizedAccessException("Sensitive proposal is not eligible for adoption");
        var receiptHash = store.PutEvidence(DecisionProtocol.Utf8.GetBytes(receiptJson));
        var payload = JsonSerializer.SerializeToElement(new { request_id = requestId, project = proposal.GetProperty("project"), title = proposal.GetProperty("title"),
            content = proposal.GetProperty("body"), references = proposal.GetProperty("references"), origin_proposal_id = proposal.GetProperty("proposal_id"), expected_proposal_hash = receipt.GetProperty("proposal_hash") });
        return Save(new(1, taskId, "decision", receiptHash, null, DecisionProtocol.ReviewHash("decision", payload), payload));
    }
    internal string PrepareHuman(string manifestHash, string referencesJson, string requestId) {
        _ = Task(); DecisionProtocol.Id(requestId);
        var manifest = new HumanLearning(store, taskId).RequireManifest(manifestHash);
        if (manifest.authority != "human_explicit") throw new UnauthorizedAccessException("Only verified human ownership permits the explicit-human route");
        var references = DecisionProtocol.Parse(referencesJson); DecisionProtocol.References(references);
        var payload = JsonSerializer.SerializeToElement(new { request_id = requestId, manifest.decision.project, manifest.decision.title, content = manifest.decision.body, references });
        return Save(new(1, taskId, "human-decision", manifestHash, manifest.source_event_id, DecisionProtocol.ReviewHash("human-decision", payload), payload));
    }
    internal Intent RequireIntent(string hash) {
        var rows = store.EventIds(taskId).Select(id => store.ReadTaskEvent(taskId, id));
        var row = rows.SingleOrDefault(e => e.Input.Producer == "windows_host" && e.Input.EventType == "decision_adoption_prepared" && e.Input.EvidenceRefs.FirstOrDefault() == hash)
            ?? throw new UnauthorizedAccessException("Intent is not attached to this task");
        if (row.Input.WorkerId is not null || row.Input.ProducerEventId is not null) throw new UnauthorizedAccessException("Worker cannot prepare Host adoption");
        var intent = JsonSerializer.Deserialize<Intent>(store.ReadEvidence(hash)) ?? throw new InvalidDataException("Intent missing");
        if (intent.version != 1 || intent.task_id != taskId || row.Input.EvidenceRefs.Length != 2 || row.Input.EvidenceRefs[1] != intent.source_hash ||
            DecisionProtocol.ReviewHash(intent.operation, intent.payload) != intent.decision_hash) throw new InvalidDataException("Adoption intent binding changed");
        var requestId = DecisionProtocol.Id(DecisionProtocol.Text(intent.payload, "request_id", 80));
        JsonElement rebuilt;
        if (intent.operation == "decision") {
            var receipt = DecisionProtocol.Parse(DecisionProtocol.Utf8.GetString(store.ReadEvidence(intent.source_hash))); var proposal = DecisionProtocol.Proposal(receipt);
            if (intent.source_event_id is not null || row.Input.CausedByEventId is not null) throw new InvalidDataException("Proposal source binding differs");
            rebuilt = JsonSerializer.SerializeToElement(new { request_id = requestId, project = proposal.GetProperty("project"), title = proposal.GetProperty("title"), content = proposal.GetProperty("body"),
                references = proposal.GetProperty("references"), origin_proposal_id = proposal.GetProperty("proposal_id"), expected_proposal_hash = receipt.GetProperty("proposal_hash") });
        } else if (intent.operation == "human-decision") {
            var manifest = new HumanLearning(store, taskId).RequireManifest(intent.source_hash);
            if (manifest.authority != "human_explicit" || manifest.source_event_id != intent.source_event_id || row.Input.CausedByEventId != intent.source_event_id) throw new UnauthorizedAccessException("Human ownership/source drift");
            DecisionProtocol.References(intent.payload.GetProperty("references"));
            rebuilt = JsonSerializer.SerializeToElement(new { request_id = requestId, manifest.decision.project, manifest.decision.title, content = manifest.decision.body, references = intent.payload.GetProperty("references") });
        } else throw new InvalidDataException("Unknown adoption operation");
        if (!DecisionProtocol.Canonical(rebuilt).SequenceEqual(DecisionProtocol.Canonical(intent.payload))) throw new InvalidDataException("Adoption payload differs from its immutable source");
        return intent;
    }
    internal static bool MatchesConfirmation(string hash, string? text) => text == "ADOPT REVIEWED DECISION " + hash;
    internal string ApproveInteractive(string intentHash) {
        var intent = RequireIntent(intentHash);
        if (Console.IsInputRedirected || Console.IsOutputRedirected) throw new UnauthorizedAccessException("Adoption approval requires an interactive Host human terminal");
        Console.WriteLine(JsonSerializer.Serialize(intent, ClientConfig.JsonOptions));
        Console.WriteLine("Review the complete content, ordered reference snapshots, project and proposal binding. This authorizes one SecondBrain adoption, not implementation truth.");
        Console.WriteLine("Type: ADOPT REVIEWED DECISION " + intentHash);
        if (!MatchesConfirmation(intentHash, Console.ReadLine())) throw new UnauthorizedAccessException("Exact adoption approval not confirmed");
        return RecordConfirmed(intentHash, Environment.UserName, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 900);
    }
    // Trusted Host post-confirmation entry point; tests exercise it with synthetic provenance.
    // No model/MCP/configuration path calls this method.
    internal string RecordConfirmed(string intentHash, string actor, long issuedAt, int duration) {
        var intent = RequireIntent(intentHash);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (string.IsNullOrWhiteSpace(actor) || issuedAt > now || issuedAt < now - 60 || duration is < 1 or > 3600) throw new UnauthorizedAccessException("Invalid human approval interval/actor");
        var id = Guid.NewGuid().ToString("N");
        var approval = JsonSerializer.SerializeToElement(new { version = 1, owner = DecisionProtocol.Owner, approval_id = id, task_id = taskId,
            request_id = intent.payload.GetProperty("request_id"), operation = intent.operation,
            proposal_id = intent.operation == "decision" ? intent.payload.GetProperty("origin_proposal_id").GetString() : null,
            proposal_hash = intent.operation == "decision" ? intent.payload.GetProperty("expected_proposal_hash").GetString() : null,
            decision_hash = intent.decision_hash, issued_at = issuedAt, expires_at = issuedAt + duration,
            provenance = intent.operation == "decision" ? "human-reviewed-model-proposal" : "explicit-human-owned-decision" });
        var fields = intent.payload.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal); fields.Add("approval", approval);
        var bytes = DecisionProtocol.Canonical(JsonSerializer.SerializeToElement(fields));
        var requestHash = store.PutEvidence(bytes);
        store.RecordDecisionApproval(Event("adoption_approval_" + id, "decision_adoption_human_approved", "human_explicit", [intentHash, requestHash],
            new { approval_id = id, intent_hash = intentHash, request_hash = requestHash, human_actor = actor, confirmation_scope = "EXACT_SECOND_BRAIN_DECISION_ADOPTION" }),
            id, intentHash, requestHash, issuedAt + duration);
        return id;
    }
    internal Reservation Reserve(string approvalId) {
        var persisted = store.DecisionApproval(taskId, DecisionProtocol.Id(approvalId));
        var intent = RequireIntent(persisted.IntentHash);
        var json = DecisionProtocol.Utf8.GetString(store.ReadEvidence(persisted.RequestHash)); var request = DecisionProtocol.Parse(json);
        var approval = request.GetProperty("approval");
        var top = request.EnumerateObject().Where(p => p.Name != "approval").ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        if (DecisionProtocol.Digest(JsonSerializer.SerializeToElement(top)) != DecisionProtocol.Digest(intent.payload) || approval.GetProperty("approval_id").GetString() != approvalId ||
            approval.GetProperty("task_id").GetString() != taskId || approval.GetProperty("decision_hash").GetString() != intent.decision_hash ||
            approval.GetProperty("expires_at").GetInt64() != persisted.ExpiresAt || approval.GetProperty("owner").GetString() != DecisionProtocol.Owner ||
            !DecisionProtocol.Canonical(request).SequenceEqual(DecisionProtocol.Utf8.GetBytes(json))) throw new InvalidDataException("Durable request/intent mismatch");
        if (!persisted.Reserved && persisted.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) throw new UnauthorizedAccessException("Adoption approval expired before reservation");
        store.ReserveDecision(Event("adoption_reserve_" + approvalId, "decision_adoption_dispatch_reserved", "host_verified", [persisted.IntentHash, persisted.RequestHash],
            new { approval_id = approvalId, request_hash = persisted.RequestHash }), approvalId, persisted.RequestHash);
        return new(approvalId, persisted.IntentHash, persisted.RequestHash,
            intent.operation == "decision" ? "/v1/brain/apply-decision" : "/v1/brain/apply-human-decision", json, persisted.ResultHash, persisted.ResultStatus);
    }
    // A qualified Host transport must supply the fixed owner marker and certificate.
    // The Host CLI transport uses the existing certificate. No model/MCP dispatch entry point exists.
    internal string DescribeIntent(string intentHash)=>JsonSerializer.Serialize(RequireIntent(intentHash),ClientConfig.JsonOptions);
    internal async Task<string> DispatchAsync(string approvalId, string expectedCertificateSha256,
        Func<Reservation, CancellationToken, Task<(int Status, string Json)>> transport, CancellationToken token = default) {
        DecisionProtocol.HashText(expectedCertificateSha256);
        using var lease = store.AcquireDecisionDispatch(approvalId);
        var reservation = Reserve(approvalId);
        if (reservation.ResultHash is { } saved) {
            var savedJson = DecisionProtocol.Utf8.GetString(store.ReadEvidence(saved));
            _ = ValidateReceipt(reservation, expectedCertificateSha256, reservation.ResultStatus ?? throw new InvalidDataException("Saved receipt HTTP status missing"), savedJson);
            return savedJson;
        }
        var response = await transport(reservation, token);
        var rawHash = store.PutEvidence(DecisionProtocol.Utf8.GetBytes(response.Json));
        store.Append(Event("adoption_response_" + approvalId + "_" + rawHash, "decision_adoption_response_received", "unverified", [reservation.RequestHash, rawHash],
            new { approval_id = approvalId, response.Status, response_hash = rawHash }));
        var status = ValidateReceipt(reservation, expectedCertificateSha256, response.Status, response.Json);
        store.FinishDecision(Event("adoption_result_" + approvalId, "decision_adoption_result_verified", "host_verified", [reservation.RequestHash, rawHash],
            new { approval_id = approvalId, result_hash = rawHash, status, http_status = response.Status }), approvalId, rawHash);
        return response.Json;
    }
    private string ValidateReceipt(Reservation reservation, string expectedCertificateSha256, int httpStatus, string json) {
        var result = DecisionProtocol.Parse(json);
        var request = DecisionProtocol.Parse(reservation.Json); var approval = request.GetProperty("approval");
        var status = DecisionProtocol.Text(result, "status", 32);
        if (status == "applied") {
            var required = new[] { "status", "operation", "request_id", "approval_id", "task_id", "client_certificate_sha256", "git_commit", "audit_id", "destination", "paths" };
            DecisionProtocol.Keys(result, approval.GetProperty("operation").GetString() == "decision" ? [.. required, "origin_proposal_id", "expected_proposal_hash", "consumption_hash"] : required);
        } else if (result.TryGetProperty("audit_id", out _)) {
            DecisionProtocol.Keys(result, "status", "reason_code", "reason", "request_id", "audit_id");
            if (!Guid.TryParseExact(DecisionProtocol.Text(result, "audit_id", 36), "D", out _))
                throw new InvalidDataException("Invalid rejected adoption audit identity");
        } else DecisionProtocol.Keys(result, "status", "reason_code", "reason", "request_id");
        if (DecisionProtocol.Text(result, "request_id", 80) != request.GetProperty("request_id").GetString()) throw new InvalidDataException("Adoption result request mismatch");
        if (status == "applied") {
            if (httpStatus != 200 || DecisionProtocol.Text(result, "operation", 32) != approval.GetProperty("operation").GetString() ||
                DecisionProtocol.Text(result, "approval_id", 80) != reservation.ApprovalId || DecisionProtocol.Text(result, "task_id", 80) != taskId ||
                DecisionProtocol.HashText(DecisionProtocol.Text(result, "client_certificate_sha256", 64)) != expectedCertificateSha256 ||
                !Regex.IsMatch(DecisionProtocol.Text(result, "git_commit", 40), "\\A[a-f0-9]{40}\\z", RegexOptions.CultureInvariant) ||
                DecisionProtocol.Text(result, "destination", 239) != "decisions/pc73-" + request.GetProperty("request_id").GetString() + ".md") throw new InvalidDataException("Applied receipt binding mismatch");
            if (approval.GetProperty("operation").GetString() == "decision" &&
                (DecisionProtocol.Text(result, "origin_proposal_id", 80) != request.GetProperty("origin_proposal_id").GetString() ||
                 DecisionProtocol.HashText(DecisionProtocol.Text(result, "expected_proposal_hash", 64)) != request.GetProperty("expected_proposal_hash").GetString())) throw new InvalidDataException("Applied proposal binding mismatch");
            if (!Guid.TryParseExact(DecisionProtocol.Text(result, "audit_id", 36), "D", out _)) throw new InvalidDataException("Invalid adoption audit identity");
            var paths = result.GetProperty("paths");
            var destination = result.GetProperty("destination").GetString()!;
            if (paths.ValueKind != JsonValueKind.Array || paths.GetArrayLength() != 2 ||
                paths.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String) ||
                !paths.EnumerateArray().Select(p => p.GetString()).Order(StringComparer.Ordinal).SequenceEqual(new[] { destination, destination + ".meta.json" }.Order(StringComparer.Ordinal))) throw new InvalidDataException("Adoption path receipt mismatch");
            if (approval.GetProperty("operation").GetString() == "decision") DecisionProtocol.HashText(DecisionProtocol.Text(result, "consumption_hash", 64));
        } else {
            // Busy/transport/server failures retain the reservation for exact recovery.
            if (status != "rejected" || httpStatus < 400 || httpStatus >= 500 ||
                DecisionProtocol.Text(result, "reason_code", 80) is "busy") throw new InvalidDataException("No terminal adoption receipt; retry the identical durable request");
            _ = DecisionProtocol.Text(result, "reason", 2000);
        }
        return status;
    }
}
