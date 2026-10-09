using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class DecisionAdoptionTests
{
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private static void Reject(Action action, string reason) {
        try { action(); } catch (Exception e) when (e is UnauthorizedAccessException or InvalidDataException or JsonException or IOException or ArgumentException or InvalidOperationException) { return; }
        throw new Exception(reason);
    }
    internal static async Task RunAsync(string? evidenceRoot = null) {
        var goldenBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "decision-protocol-v1.json"));
        Check(CanonicalStore.Hash(goldenBytes) == "45514c43d14e2835bccdfcb79b898450bf8e2da9ff17b3d516cf0782cc570614", "Golden fixture changed");
        using var golden = JsonDocument.Parse(goldenBytes);
        var vectors = golden.RootElement.GetProperty("vectors").EnumerateArray().ToArray();
        foreach (var v in vectors) {
            var canonical = DecisionProtocol.Canonical(v.GetProperty("proposal_payload"));
            Check(Convert.ToHexString(canonical).ToLowerInvariant() == v.GetProperty("canonical_utf8_hex").GetString() &&
                Convert.ToBase64String(canonical) == v.GetProperty("canonical_utf8_base64").GetString() && CanonicalStore.Hash(canonical) == v.GetProperty("proposal_hash").GetString(), "Independent proposal vector mismatch");
            Check(Convert.ToHexString(DecisionProtocol.Canonical(v.GetProperty("reviewed_envelope"))).ToLowerInvariant() == v.GetProperty("reviewed_utf8_hex").GetString() &&
                DecisionProtocol.ReviewHash("decision", v.GetProperty("adoption_without_approval")) == v.GetProperty("decision_hash").GetString(), "Independent adoption vector mismatch");
        }
        foreach (var json in new[] { "{\"x\":1,\"x\":2}", "{\"x\":1.0}", "{\"x\":1e0}", "{\"日本語\":1}" }) Reject(() => DecisionProtocol.Parse(json), "Noncontract serialization accepted");
        Reject(() => DecisionProtocol.Id("proposal_001\n"), "Trailing LF identity accepted");
        Reject(() => DecisionProtocol.Parse("\"\\ud800\""), "Unpaired surrogate accepted");
        var v0 = vectors[1]; var proposal = v0.GetProperty("proposal_payload");
        var receipt = JsonSerializer.Serialize(new { proposal_id = proposal.GetProperty("proposal_id"), proposal_hash = v0.GetProperty("proposal_hash"),
            schema = DecisionProtocol.ProposalSchema, status = "draft", payload = proposal, content = proposal.GetProperty("body"),
            path = "drafts/pending-review/" + proposal.GetProperty("proposal_id").GetString() + ".md",
            file_hashes = new[] { v0.GetProperty("markdown_sha256").GetString(), new string('b', 64), v0.GetProperty("proposal_hash").GetString() } });
        DecisionProtocol.Proposal(DecisionProtocol.Parse(receipt));
        Reject(() => DecisionProtocol.Proposal(DecisionProtocol.Parse(receipt.Replace("\"draft\"", "\"adopted\""))), "Consumed proposal accepted");
        Reject(() => DecisionProtocol.Proposal(DecisionProtocol.Parse(receipt.Replace(v0.GetProperty("proposal_hash").GetString()!, new string('a', 64)))), "Changed proposal hash accepted");
        var root = evidenceRoot is null ? Path.Combine(Path.GetTempPath(), "localbrain-adoption", Guid.NewGuid().ToString("N")) : Path.GetFullPath(evidenceRoot);
        if (Directory.Exists(root)) throw new IOException("Adoption proof must be new");
        var storage = Path.Combine(root, "host"); string intentHash, approvalId, requestBytes; var calls = 0;
        string? faultPoint = null;
        Action<string> fault = point => { if (faultPoint == point) { faultPoint = null; throw new IOException("synthetic crash: " + point); } };
        using (var store = new CanonicalStore(storage, fault)) {
            store.RegisterWorkspace(new("repo_adoption", "ws_adoption", Path.Combine(root, "repo"), Path.Combine(root, "repo"), "main", "fixture"));
            store.CreateTask("task_adoption", "ws_adoption", CanonicalStore.Hash(Encoding.UTF8.GetBytes("{}")), "{}");
            store.CreateTask("task_other", "ws_adoption", CanonicalStore.Hash(Encoding.UTF8.GetBytes("{}")), "{}");
            var adapter = new DecisionAdoption(store, "task_adoption");
            intentHash = adapter.PrepareProposal(receipt, "adoption_unicode_001");
            Check(adapter.RequireIntent(intentHash).operation == "decision", "Proposal became human-owned");
            Reject(() => new DecisionAdoption(store, "task_other").RequireIntent(intentHash), "Cross-task intent accepted");
            Reject(() => adapter.Reserve("not_approved_001"), "Unapproved dispatch accepted");
            if (Console.IsInputRedirected || Console.IsOutputRedirected) Reject(() => adapter.ApproveInteractive(intentHash), "Model pipe minted adoption approval");
            Check(DecisionAdoption.MatchesConfirmation(intentHash, "ADOPT REVIEWED DECISION " + intentHash) && !DecisionAdoption.MatchesConfirmation(intentHash, "yes"), "Ambiguous adoption confirmation accepted");
            // Trusted Host provenance is simulated only in this disposable store.
            approvalId = adapter.RecordConfirmed(intentHash, "synthetic-human", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 900);
            Reject(() => new DecisionAdoption(store, "task_other").Reserve(approvalId), "Cross-task approval accepted");
            faultPoint = "event_before_commit";
            Reject(() => adapter.Reserve(approvalId), "Before-reservation crash did not fire");
            Check(!store.DecisionApproval("task_adoption", approvalId).Reserved, "Failed transaction consumed approval");
            faultPoint = "event_after_commit";
            Reject(() => adapter.Reserve(approvalId), "After-reservation crash did not fire");
            Check(store.DecisionApproval("task_adoption", approvalId).Reserved, "Committed reservation missing after crash");
            requestBytes = adapter.Reserve(approvalId).Json;
            var expired = adapter.RecordConfirmed(intentHash, "synthetic-human", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 59, 1);
            Reject(() => adapter.Reserve(expired), "Expired approval started an adoption");
            // Worker/model flags cannot create human-owned source provenance.
            var input = HumanLearning.Parse("{\"request_id\":\"human_model_001\",\"title\":\"Model statement\",\"body\":\"Synthetic body\",\"project\":\"demo\"}");
            var inputHash = store.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(input));
            var source = store.Append(new("task_adoption", "ws_adoption", "qwen", "model_source", "human_owned_decision_confirmed", "model_claim", "human_explicit", [inputHash],
                JsonSerializer.SerializeToElement(new { input_hash = inputHash, human_actor = "pretended", confirmation_scope = "OWN_DECISION_ONLY_NO_ADOPTION" })));
            var modelManifest = new HumanLearning(store, "task_adoption").FromSourceEvent(source.EventId);
            Reject(() => adapter.PrepareHuman(modelManifest, proposal.GetProperty("references").GetRawText(), "human_apply_001"), "Model human flags chose human route");
            var humanSource = store.Append(new("task_adoption", "ws_adoption", "windows_host", "human_source", "human_owned_decision_confirmed", "human_statement", "human_explicit", [inputHash],
                JsonSerializer.SerializeToElement(new { input_hash = inputHash, human_actor = "synthetic-human", confirmation_scope = "OWN_DECISION_ONLY_NO_ADOPTION" })));
            var humanManifest = new HumanLearning(store, "task_adoption").FromSourceEvent(humanSource.EventId);
            var humanIntent = adapter.PrepareHuman(humanManifest, proposal.GetProperty("references").GetRawText(), "human_apply_002");
            var humanApproval = adapter.RecordConfirmed(humanIntent, "synthetic-human", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 900);
            Check(adapter.Reserve(humanApproval).Route == "/v1/brain/apply-human-decision", "Human ownership did not select its distinct route");
            await adapter.DispatchAsync(humanApproval, new string('c', 64), (r, _) => {
                var p = DecisionProtocol.Parse(r.Json);
                return Task.FromResult((409, JsonSerializer.Serialize(new { status = "rejected", reason_code = "stale_target", reason = "Synthetic reference stale", request_id = p.GetProperty("request_id") })));
            });
            await adapter.DispatchAsync(humanApproval, new string('c', 64), (_, _) => throw new Exception("Terminal rejected result dispatched again"));
            var auditedIntent = adapter.PrepareHuman(humanManifest, proposal.GetProperty("references").GetRawText(), "human_apply_003");
            var auditedApproval = adapter.RecordConfirmed(auditedIntent, "synthetic-human", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 900);
            var auditedRequest = DecisionProtocol.Parse(adapter.Reserve(auditedApproval).Json).GetProperty("request_id").GetString();
            foreach (var invalid in new[] {
                JsonSerializer.Serialize(new { status="rejected",reason_code="stale_target",reason="fixture",request_id=auditedRequest,audit_id="bad" }),
                JsonSerializer.Serialize(new { status="rejected",reason_code="stale_target",reason="fixture",request_id=auditedRequest,audit_id=Guid.NewGuid().ToString(),extra=true }),
                JsonSerializer.Serialize(new { status="rejected",reason_code="busy",reason="fixture",request_id=auditedRequest,audit_id=Guid.NewGuid().ToString() }) }) {
                await ExpectFailure(()=>adapter.DispatchAsync(auditedApproval,new string('c',64),(_,_)=>Task.FromResult((409,invalid))), "Invalid audited rejection finalized");
                Check(store.DecisionApproval("task_adoption",auditedApproval).ResultHash is null,"Invalid audited rejection saved as terminal");
            }
            var auditedResult=JsonSerializer.Serialize(new {status="rejected",reason_code="stale_target",reason="Synthetic reference stale",request_id=auditedRequest,audit_id=Guid.NewGuid().ToString()});
            await adapter.DispatchAsync(auditedApproval,new string('c',64),(_,_)=>Task.FromResult((409,auditedResult)));
            Check(store.DecisionApproval("task_adoption",auditedApproval).ResultHash==CanonicalStore.Hash(Encoding.UTF8.GetBytes(auditedResult)),"Audited terminal rejection not retained");
            Check(await adapter.DispatchAsync(auditedApproval,new string('c',64),(_,_)=>throw new Exception("Audited rejection redispatched"))==auditedResult,"Audited rejected cache changed bytes");
        }
        var certificate = new string('c', 64);
        string Success(DecisionAdoption.Reservation r) {
            using var request = JsonDocument.Parse(r.Json); var p = request.RootElement;
            return JsonSerializer.Serialize(new { status = "applied", operation = "decision", request_id = p.GetProperty("request_id"), approval_id = r.ApprovalId,
                task_id = "task_adoption", client_certificate_sha256 = certificate, git_commit = new string('d', 40), audit_id = Guid.NewGuid().ToString(),
                destination = "decisions/pc73-" + p.GetProperty("request_id").GetString() + ".md", paths = new[] { "decisions/pc73-" + p.GetProperty("request_id").GetString() + ".md", "decisions/pc73-" + p.GetProperty("request_id").GetString() + ".md.meta.json" },
                origin_proposal_id = p.GetProperty("origin_proposal_id"), expected_proposal_hash = p.GetProperty("expected_proposal_hash"), consumption_hash = new string('e', 64) });
        }
        using (var store = new CanonicalStore(storage, fault)) {
            var adapter = new DecisionAdoption(store, "task_adoption");
            Check(adapter.Reserve(approvalId).Json == requestBytes, "Restart changed reserved bytes/expiry/request identity");
            await ExpectFailure(async () => await adapter.DispatchAsync(approvalId, certificate, (r, _) => { calls++; throw new HttpRequestException("uncertain backend commit"); }), "Transport failure became adoption");
            await ExpectFailure(async () => await adapter.DispatchAsync(approvalId, certificate, (r, _) => Task.FromResult((200, Success(r).Replace(certificate, new string('a', 64))))), "Foreign certificate receipt accepted");
            Check(store.DecisionApproval("task_adoption", approvalId).ResultHash is null, "Invalid receipt finalized adoption");
            foreach (var field in new[] { "audit_id", "paths", "consumption_hash" }) {
                await ExpectFailure(async () => await adapter.DispatchAsync(approvalId, certificate, (r, _) => {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(Success(r))!.AsObject(); node.Remove(field); return Task.FromResult((200, node.ToJsonString()));
                }), "Incomplete success receipt accepted: " + field);
            }
            await ExpectFailure(async () => await adapter.DispatchAsync(approvalId, certificate, (r, _) => Task.FromResult((200, Success(r).Replace(new string('e', 64), "bad_hash")))), "Invalid consumption hash accepted");
            var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
            var first = adapter.DispatchAsync(approvalId, certificate, async (r, _) => { calls++; Check(r.Json == requestBytes, "Exact retry drifted"); entered.SetResult(); await release.Task; return (200, Success(r)); });
            await entered.Task;
            await ExpectFailure(async () => await adapter.DispatchAsync(approvalId, certificate, (_, _) => throw new Exception("Duplicate transport executed")), "Concurrent dispatch allowed");
            release.SetResult(); var success = await first;
            Check(store.DecisionApproval("task_adoption", approvalId).ResultHash == CanonicalStore.Hash(DecisionProtocol.Utf8.GetBytes(success)), "Final receipt not immutable");
        }
        using (var store = new CanonicalStore(storage)) {
            var adapter = new DecisionAdoption(store, "task_adoption");
            await ExpectFailure(async () => await adapter.DispatchAsync(approvalId, new string('a', 64), (_, _) => throw new Exception("Foreign cached caller dispatched")), "Cached result accepted another certificate");
            var recovered = await adapter.DispatchAsync(approvalId, certificate, (_, _) => throw new Exception("Completed receipt called backend again"));
            Check(DecisionProtocol.Parse(recovered).GetProperty("status").GetString() == "applied", "Saved applied receipt lost");
            var sourceHash = adapter.RequireIntent(intentHash).source_hash; var path = Path.Combine(storage, "evidence", sourceHash + ".blob"); var original = File.ReadAllBytes(path);
            File.SetAttributes(path, FileAttributes.Normal); File.WriteAllText(path, "altered source"); Reject(() => adapter.Reserve(approvalId), "Changed immutable proposal source accepted");
            File.WriteAllBytes(path, original); File.SetAttributes(path, FileAttributes.ReadOnly);
        }
        if (evidenceRoot is not null) File.WriteAllText(Path.Combine(root, "proof.json"), JsonSerializer.Serialize(new { status = "PASS_ISOLATED_HOST_ADOPTION_CORE_ONLY", golden_vectors = vectors.Length,
            host_root = storage, intent_hash = intentHash, approval_id = approvalId, request_sha256 = CanonicalStore.Hash(DecisionProtocol.Utf8.GetBytes(requestBytes)),
            synthetic_transport_calls = calls, production_human_approval = false, production_adoptions = 0, real_mtls_qualified = false, exclusive_host_ownership_qualified = false }));
        Console.WriteLine("Decision adoption core: exact Python Unicode vectors, immutable proposal/source, strict schema, human/model separation, atomic reservation crash/restart, exact retry, concurrent denial and receipt validation PASS; synthetic fixture only; Host exclusivity unresolved");
    }
    private static async Task ExpectFailure(Func<Task> action, string reason) {
        try { await action(); } catch (Exception e) when (e is UnauthorizedAccessException or InvalidDataException or IOException or JsonException or HttpRequestException) { return; }
        throw new Exception(reason);
    }
}
