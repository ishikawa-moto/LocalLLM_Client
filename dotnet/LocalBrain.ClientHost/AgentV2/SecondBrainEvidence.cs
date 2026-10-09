using System.Text;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

/// <summary>Reads one small cited packet through the existing mTLS Gateway.</summary>
internal static class SecondBrainEvidence
{
    internal sealed record Packet(string Markdown, string[] ChunkIds)
    {
        public static Packet Empty { get; } = new("", []);
        public bool HasEvidence => ChunkIds.Length > 0 && !string.IsNullOrWhiteSpace(Markdown);
    }

    public static async Task<Packet> FetchAsync(ClientConfig config, string requirement,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(requirement)) return Packet.Empty;
        var query = requirement.Length <= 240 ? requirement : requirement[..240];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var gateway = new GatewayClient(config);
            var request = JsonSerializer.Serialize(new { query, budget = 1024 });
            var response = await gateway.JsonAsync(HttpMethod.Post, "/v1/brain/context", request,
                timeout.Token);
            if (Encoding.UTF8.GetByteCount(response) > 16_384) return Packet.Empty;
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (!root.TryGetProperty("markdown", out var markdownValue) ||
                markdownValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("chunk_ids", out var chunkValues) ||
                chunkValues.ValueKind != JsonValueKind.Array ||
                chunkValues.GetArrayLength() is < 1 or > 8 ||
                !root.TryGetProperty("stale_sources", out var stale) ||
                stale.ValueKind != JsonValueKind.Array || stale.GetArrayLength() != 0)
                return Packet.Empty;
            var markdown = markdownValue.GetString() ?? "";
            if (Encoding.UTF8.GetByteCount(markdown) > 1536) return Packet.Empty;
            var ids = new List<string>();
            foreach (var item in chunkValues.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String ||
                    item.GetString() is not { Length: 64 } id ||
                    !id.All(Uri.IsHexDigit)) return Packet.Empty;
                ids.Add(id);
            }
            return new(markdown, ids.ToArray());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            // Reference retrieval is optional; errors and raw response text stay out of task logs.
            return Packet.Empty;
        }
    }
}
