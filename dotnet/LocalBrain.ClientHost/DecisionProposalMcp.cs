using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalBrain.ClientHost;

// Draft preparation only. No approval creation or adoption dispatch belongs in MCP.
internal static class DecisionProposalMcp
{
    internal static string LegacyRejected(string json)
    {
        using var input = JsonDocument.Parse(json);
        var id = input.RootElement.TryGetProperty("request_id", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return JsonSerializer.Serialize(new { status = "rejected", reason_code = "proposal_binding_required",
            reason = "Direct decision adoption is disabled. Use brain_prepare_decision with the identical request_id/project/title/content/references. It creates a bound draft only; Windows Host approval and dispatch are required before adoption.", request_id = id });
    }

    internal static string Request(string json, bool usesContent)
    {
        using var input = JsonDocument.Parse(json);
        var root = input.RootElement;
        var bodyKey = usesContent ? "content" : "body";
        var fields = new[] { "request_id", "project", "title", bodyKey, "references" };
        if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Bound decision requires exactly request_id/project/title/content (or body)/references; no approval flags.");
        string Text(string key, int max) {
            var item = root.GetProperty(key);
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()) || item.GetString()!.Length > max || item.GetString()!.Contains('\0'))
                throw new InvalidDataException("Invalid bound decision field: " + key);
            return item.GetString()!;
        }
        var id = Text("request_id", 80); var project = Text("project", 80);
        if (!Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{8,80}\z") || !Regex.IsMatch(project, @"\A[A-Za-z0-9_.-]{1,80}\z"))
            throw new InvalidDataException("Invalid bound decision identity/project.");
        var title = Text("title", 200); var body = Text(bodyKey, 200_000);
        if (System.Text.Encoding.UTF8.GetByteCount(body) > 200_000) throw new InvalidDataException("Decision exceeds UTF-8 budget.");
        var references = root.GetProperty("references");
        if (references.ValueKind != JsonValueKind.Array || references.GetArrayLength() is < 1 or > 30) throw new InvalidDataException("Ordered reference snapshots are required.");
        foreach (var reference in references.EnumerateArray()) {
            var names = new[] { "path", "expected_hash", "expected_metadata_hash", "relevant_location" };
            if (reference.ValueKind != JsonValueKind.Object || !reference.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal))) throw new InvalidDataException("Reference requires path/content hash/metadata hash/location.");
            foreach (var name in names) if (reference.GetProperty(name).ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(reference.GetProperty(name).GetString())) throw new InvalidDataException("Invalid reference field.");
            foreach (var name in new[] { "expected_hash", "expected_metadata_hash" }) if (!Regex.IsMatch(reference.GetProperty(name).GetString()!, @"\A[a-f0-9]{64}\z")) throw new InvalidDataException("Invalid reference SHA-256.");
        }
        return JsonSerializer.Serialize(new { request_id = id, project, title, body, references });
    }

    internal static string Prepared(string requestJson, string receiptJson)
    {
        using var requestDoc = JsonDocument.Parse(requestJson); using var receiptDoc = JsonDocument.Parse(receiptJson);
        var request = requestDoc.RootElement; var receipt = receiptDoc.RootElement;
        // Forward business rejections verbatim; never convert them into pending/success.
        if (receipt.TryGetProperty("status", out var state) && state.GetString() is "rejected" or "error") return receiptJson;
        var payload = receipt.GetProperty("payload");
        var id = request.GetProperty("request_id").GetString();
        if (receipt.GetProperty("schema").GetString() != "localbrain.decision-proposal.v1" || receipt.GetProperty("status").GetString() != "draft" ||
            receipt.GetProperty("proposal_id").GetString() != id || payload.GetProperty("proposal_id").GetString() != id ||
            receipt.GetProperty("path").GetString() != "drafts/pending-review/" + id + ".md" ||
            payload.GetProperty("schema").GetString() != "localbrain.decision-proposal.v1" || payload.GetProperty("kind").GetString() != "decision" ||
            !Regex.IsMatch(receipt.GetProperty("proposal_hash").GetString()!, @"\A[a-f0-9]{64}\z")) throw new InvalidDataException("Unexpected bound draft receipt; do not retry with changed content.");
        foreach (var field in new[] { "project", "title", "body", "references" }) if (!JsonElement.DeepEquals(request.GetProperty(field), payload.GetProperty(field))) throw new InvalidDataException("Bound draft receipt differs from the requested content/reference snapshots.");
        if (!JsonElement.DeepEquals(receipt.GetProperty("content"), request.GetProperty("body"))) throw new InvalidDataException("Bound draft content differs.");
        return JsonSerializer.Serialize(new { status = "pending_host_approval", request_id = id, applied = false, proposal_receipt = receipt,
            next_step = "Save proposal_receipt as UTF-8 JSON. In an interactive Windows terminal, use the Host decision approval helper with your registered workspace. It validates the full receipt/hash, prepares an intent, asks you to approve its exact content, then dispatches through the durable Host contract. Do not call brain_apply_decision again." });
    }
}
