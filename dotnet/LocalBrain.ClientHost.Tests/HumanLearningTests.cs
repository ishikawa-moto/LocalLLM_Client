using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class HumanLearningTests
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Reject(Action action, string reason) {
        try { action(); } catch (Exception e) when (e is UnauthorizedAccessException or InvalidDataException or IOException or JsonException) { return; }
        throw new InvalidOperationException(reason);
    }
    internal static void Run(string? evidenceRoot = null)
    {
        var root = evidenceRoot is null ? Path.Combine(Path.GetTempPath(), "localbrain-human-learning", Guid.NewGuid().ToString("N")) : Path.GetFullPath(evidenceRoot);
        if (Directory.Exists(root)) throw new IOException("Human learning fixture must be new");
        var host = Path.Combine(root, "host");
        var input = HumanLearning.Parse("{\"request_id\":\"decision_001\",\"title\":\"Test decision\",\"body\":\"Use the current repository as implementation authority\",\"project\":\"localbrain\"}");
        var valid = JsonSerializer.Serialize(input);
        foreach (var json in new[] { valid.Replace("\"project\":", "\"authority\":\"human_explicit\",\"project\":"),
            valid.Replace("\"title\":", "\"title\":\"duplicate\",\"title\":"), valid.Replace("\"body\":\"Use the current repository as implementation authority\"", "\"body\":true"),
            valid.Replace("decision_001", "../../unsafe"), valid.Replace("decision_001", "decision_001\\n"), valid.Replace("localbrain", "localbrain\\n"),
            valid.Replace(",\"project\":\"localbrain\"", ""), valid.Replace("Use the current repository as implementation authority", "SERVICE_API_KEY=sensitivevalue") })
            Reject(() => HumanLearning.Parse(json), "Invalid or sensitive decision accepted");
        Check(HumanLearning.MatchesOwnershipConfirmation(input, "I OWN THIS DECISION " + HumanLearning.ContentHash(input)), "Exact confirmation rejected");
        Check(!HumanLearning.MatchesOwnershipConfirmation(input, "yes") && !HumanLearning.MatchesOwnershipConfirmation(input with { body = "changed" }, "I OWN THIS DECISION " + HumanLearning.ContentHash(input)), "Ambiguous or stale confirmation accepted");
        string humanEvent, manifestHash, inputHash;
        using (var store = new CanonicalStore(host)) {
            store.RegisterWorkspace(new("repo_human", "ws_human", Path.Combine(root, "repo"), Path.Combine(root, "repo"), "main", "baseline"));
            var request = "{}"; store.CreateTask("task_human", "ws_human", CanonicalStore.Hash(Encoding.UTF8.GetBytes(request)), request);
            store.CreateTask("task_other", "ws_human", CanonicalStore.Hash(Encoding.UTF8.GetBytes(request)), request);
            inputHash = store.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(input));
            // Fixture simulates trusted Host provenance; no production human approval or adoption is minted.
            CanonicalStore.EventInput Source(string key, string producer, string type, string status, string evidenceClass) =>
                new("task_human", "ws_human", producer, key, type, evidenceClass, status, [inputHash],
                    JsonSerializer.SerializeToElement(new { input_hash = inputHash, human_actor = "synthetic-human", confirmation_scope = "OWN_DECISION_ONLY_NO_ADOPTION" }));
            humanEvent = store.Append(Source("human", "windows_host", "human_owned_decision_confirmed", "human_explicit", "human_statement")).EventId;
            var learning = new HumanLearning(store, "task_human"); manifestHash = learning.FromSourceEvent(humanEvent);
            var manifest = learning.RequireManifest(manifestHash);
            Check(manifest.authority == "human_explicit" && manifest.ask_first_required && manifest.adoption_status == "LOCAL_ONLY_NOT_ADOPTED", "Ownership became adoption or implementation truth");
            var before = store.EventIds("task_human").Length;
            Check(learning.FromSourceEvent(humanEvent) == manifestHash && store.EventIds("task_human").Length == before, "Replay duplicated intake");
            var model = store.Append(Source("model", "qwen", "human_owned_decision_confirmed", "human_explicit", "model_claim")).EventId;
            Check(learning.RequireManifest(learning.FromSourceEvent(model)).authority == "model_claim", "Model-provided human flags became human authority");
            var ambiguous = store.Append(Source("quoted", "windows_host", "human_message_quote", "unverified", "human_statement")).EventId;
            Check(learning.RequireManifest(learning.FromSourceEvent(ambiguous)).authority == "unverified", "Quoted/ambiguous human text became an owned decision");
            var worker = store.Append(Source("worker", "windows_host", "human_owned_decision_confirmed", "human_explicit", "human_statement") with { WorkerId = "ServerPC" }).EventId;
            Check(learning.RequireManifest(learning.FromSourceEvent(worker)).authority == "unverified", "Worker provenance minted human ownership");
            var missing = store.Append(Source("missing_binding", "windows_host", "human_owned_decision_confirmed", "human_explicit", "human_statement") with { Metadata = JsonSerializer.SerializeToElement(new { human_actor = "synthetic-human" }) }).EventId;
            Check(learning.RequireManifest(learning.FromSourceEvent(missing)).authority == "unverified", "Incomplete ownership binding accepted");
            foreach (var metadata in new[] {
                JsonSerializer.SerializeToElement(new { input_hash = new string('a', 64), human_actor = "synthetic-human", confirmation_scope = "OWN_DECISION_ONLY_NO_ADOPTION" }),
                JsonSerializer.SerializeToElement(new { input_hash = inputHash, confirmation_scope = "OWN_DECISION_ONLY_NO_ADOPTION" }),
                JsonSerializer.SerializeToElement(new { input_hash = inputHash, human_actor = "synthetic-human" }) }) {
                var eventId = store.Append(Source("bad_binding_" + Guid.NewGuid().ToString("N"), "windows_host", "human_owned_decision_confirmed", "human_explicit", "human_statement") with { Metadata = metadata }).EventId;
                Check(learning.RequireManifest(learning.FromSourceEvent(eventId)).authority == "unverified", "Wrong hash/missing actor/scope became human ownership");
            }
            var foreign = new HumanLearning(store, "task_other");
            Reject(() => foreign.FromSourceEvent(humanEvent), "Cross-task source accepted");
            Reject(() => foreign.RequireManifest(manifestHash), "Cross-task manifest accepted");
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
                Reject(() => learning.CaptureInteractive(input), "Redirected input minted human ownership");
        }
        using (var store = new CanonicalStore(host)) {
            var learning = new HumanLearning(store, "task_human");
            Check(learning.RequireManifest(manifestHash).source_event_id == humanEvent, "Restart lost source binding");
            var before = store.EventIds("task_human").Length;
            learning.RequireManifest(manifestHash);
            Check(store.EventIds("task_human").Length == before, "Manifest verification wrote an event");
            var path = Path.Combine(host, "evidence", inputHash + ".blob");
            var original = File.ReadAllBytes(path);
            File.SetAttributes(path, FileAttributes.Normal); File.WriteAllText(path, "tampered");
            Reject(() => learning.RequireManifest(manifestHash), "Altered source became verified human learning");
            File.WriteAllBytes(path, original); File.SetAttributes(path, FileAttributes.ReadOnly);
            learning.RequireManifest(manifestHash);
        }
        if (evidenceRoot is not null) File.WriteAllText(Path.Combine(root, "proof.json"), JsonSerializer.Serialize(new {
            status = "PASS_LOCAL_INTAKE_SYNTHETIC_HUMAN_PROVENANCE", host_root = host, task_id = "task_human",
            source_event_id = humanEvent, manifest_hash = manifestHash, input_hash = inputHash,
            model_and_quoted_authority_isolated = true, worker_cannot_own = true, incomplete_binding_unverified = true,
            cross_task_and_tamper_rejected = true, replay_restart_verified = true, readonly_manifest_verification = true,
            production_approval_minted = false, network_calls = 0, knowledge_adoptions = 0
        }), new UTF8Encoding(false));
        Console.WriteLine("Human learning local intake: strict input, exact ownership confirmation, model/quoted authority isolation, cross-task/tamper rejection, replay and restart PASS; synthetic Host provenance only; no proposal/export/adoption");
    }
}
