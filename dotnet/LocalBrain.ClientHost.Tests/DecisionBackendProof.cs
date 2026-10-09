using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class DecisionBackendProof
{
    internal static async Task RunAsync(string root, bool finish) {
        root = Path.GetFullPath(root);
        var host = Path.Combine(root, "host");
        using var store = new CanonicalStore(host);
        var adapter = new DecisionAdoption(store, "task_cross_language");
        if (!finish) {
            if (store.GetTask("task_cross_language") is not null) throw new InvalidOperationException("Proof must be new");
            store.RegisterWorkspace(new("repo_cross_language", "ws_cross_language", Path.Combine(root, "repo"), Path.Combine(root, "repo"), "main", "synthetic"));
            store.CreateTask("task_cross_language", "ws_cross_language", CanonicalStore.Hash(Encoding.UTF8.GetBytes("{}")), "{}");
            var intent = adapter.PrepareProposal(await File.ReadAllTextAsync(Path.Combine(root, "proposal-receipt.json")), "cross-adoption-0001");
            // Disposable fixture alone simulates a human post-confirmation callback.
            var id = adapter.RecordConfirmed(intent, "synthetic-protocol-fixture", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1, 900);
            var reserved = adapter.Reserve(id);
            await File.WriteAllBytesAsync(Path.Combine(root, "host-request.json"), DecisionProtocol.Utf8.GetBytes(reserved.Json));
            await File.WriteAllTextAsync(Path.Combine(root, "host-proof.json"), JsonSerializer.Serialize(new { approval_id = id, intent_hash = intent, request_hash = reserved.RequestHash, production_human_approval = false, real_mtls = false }));
            Console.WriteLine(".NET durable exact-byte reservation prepared for actual Python fixture");
        } else {
            using var proof = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "host-proof.json")));
            var id = proof.RootElement.GetProperty("approval_id").GetString()!;
            var json = await File.ReadAllTextAsync(Path.Combine(root, "backend-result.json"));
            var result = DecisionProtocol.Parse(json);
            using var backendProof = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"backend-proof.json")));
            var httpStatus = backendProof.RootElement.GetProperty("http_status").GetInt32();
            var validated = await adapter.DispatchAsync(id, new string('c', 64), (_, _) => Task.FromResult((httpStatus, json)));
            if (validated != json) throw new Exception("Result bytes changed");
            await adapter.DispatchAsync(id, new string('c', 64), (_, _) => throw new Exception("Completed receipt re-dispatched"));
            Console.WriteLine("Actual Python candidate "+result.GetProperty("status").GetString()+" result accepted by .NET, canonical result/evidence retained and cached exact result verified; synthetic transport, no production mTLS/adoption");
        }
    }
}
