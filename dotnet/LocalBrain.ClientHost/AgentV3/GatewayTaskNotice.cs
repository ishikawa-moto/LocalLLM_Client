using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

namespace LocalBrain.ClientHost.AgentV3;

// A single bounded read on explicit status requests, never a background poll.
internal static class GatewayTaskNotice
{
    internal sealed record Notice(string State, string Text);

    internal static async Task<Notice?> ReadAsync(string directory, TaskAgentCoordinator.Job job, string requestId)
    {
        if (!Guid.TryParseExact(requestId, "D", out _) || !TaskAgentCoordinator.OwnerAlive(directory)) return null;
        try {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var path = Path.Combine(directory, "worker-config.json");
            CanonicalStore.GuardPath(path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is 0 or > 1024 * 1024) return null;
            var bytes = new byte[(int)input.Length];
            await input.ReadExactlyAsync(bytes, deadline.Token);
            if (CanonicalStore.Hash(bytes) != job.ConfigHash) return null;
            var config = JsonSerializer.Deserialize<ClientConfig>(bytes, ClientConfig.JsonOptions);
            if (config?.V3Development?.Enabled != true ||
                !Uri.TryCreate(config.GatewayBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https") return null;
            using var gateway = new GatewayClient(config);
            using var response = await gateway.SendAsync(HttpMethod.Get,
                "/v1/control/job-status?request_id=" + Uri.EscapeDataString(requestId), null, null, null,
                Guid.NewGuid().ToString("D"), deadline.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 16384) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var output = new MemoryStream();
            var buffer = new byte[4096]; int n;
            while ((n = await stream.ReadAsync(buffer, deadline.Token)) != 0) {
                if (output.Length + n > 16384) return null;
                await output.WriteAsync(buffer.AsMemory(0, n), deadline.Token);
            }
            using var json = JsonDocument.Parse(output.ToArray());
            var value = json.RootElement;
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("request_id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != requestId ||
                !value.TryGetProperty("state", out var stateValue) || stateValue.ValueKind != JsonValueKind.String ||
                !value.TryGetProperty("notice", out var textValue) || textValue.ValueKind != JsonValueKind.String) return null;
            var state = stateValue.GetString(); var text = textValue.GetString();
            if (!((state == "queued" && text == "別の処理を実行中のため、しばらくお待ちください") ||
                  (state == "active" && text == "処理を開始しました"))) return null;
            // The task may have completed or started another request during the GET.
            var latest = CanonicalStore.ReadLatest(job.ControlRoot, HostTaskJournal.WorkspaceId(job.Root));
            if (latest?.TaskId != job.TaskId || !TaskAgentCoordinator.OwnerAlive(directory)) return null;
            using var latestState = JsonDocument.Parse(latest.StateJson);
            if (!latestState.RootElement.TryGetProperty("phase", out var phase) ||
                phase.ValueKind != JsonValueKind.String || phase.GetString() != "v3_running" ||
                !latestState.RootElement.TryGetProperty("current_gateway_request_id", out var current) ||
                current.ValueKind != JsonValueKind.String || current.GetString() != requestId) return null;
            return new Notice(state!, text!);
        } catch (Exception error) when (error is not OutOfMemoryException) {
            // An unavailable or unsupported status is not a queue notification.
            return null;
        }
    }
}
