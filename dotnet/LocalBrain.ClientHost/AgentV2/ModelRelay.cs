using System.Net.Http.Headers;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

/// <summary>
/// One request over stdio from WSL Pi to the existing Windows loopback Bridge.
/// The process opens no listener, and only the model completion route is permitted.
/// Request and response bodies are never written to logs.
/// </summary>
internal static class ModelRelay
{
    private const int MaxRequestBytes = 8 * 1024 * 1024;
    private const int MaxResponseBytes = 32 * 1024 * 1024;
    private sealed record RelayRequest(string Method, string Path, string? ContentType, string BodyBase64);

    public static Task RunAsync(int bridgePort)=>RunCoreAsync(bridgePort,TimeSpan.FromMinutes(9));
    internal static Task RunFixtureAsync(int bridgePort,TimeSpan deadline)=>RunCoreAsync(bridgePort,deadline);
    private static async Task RunCoreAsync(int bridgePort,TimeSpan deadline)
    {
        try
        {
            var line = await Console.In.ReadLineAsync() ?? throw new InvalidDataException("Missing request");
            if (line.Length > MaxRequestBytes * 2) throw new InvalidDataException("Request too large");
            var request = JsonSerializer.Deserialize<RelayRequest>(line, ClientConfig.JsonOptions)
                ?? throw new InvalidDataException("Invalid request");
            if (request.Method != "POST" || request.Path != "/v1/chat/completions")
                throw new UnauthorizedAccessException("Relay route is not allowed");
            var body = Convert.FromBase64String(request.BodyBase64);
            if (body.Length > MaxRequestBytes) throw new InvalidDataException("Request too large");
            using var cancellation=new CancellationTokenSource(deadline);
            using var client = new HttpClient { BaseAddress = new Uri($"http://{System.Net.IPAddress.Loopback}:{bridgePort}/"),
                Timeout = Timeout.InfiniteTimeSpan };
            using var message = new HttpRequestMessage(HttpMethod.Post, request.Path)
            {
                Content = new ByteArrayContent(body)
            };
            if (MediaTypeHeaderValue.TryParse(request.ContentType, out var type))
                message.Content.Headers.ContentType = type;
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,cancellation.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
            await using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk,cancellation.Token)) != 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                    throw new InvalidDataException("Model response too large");
                await buffer.WriteAsync(chunk.AsMemory(0, read),cancellation.Token);
            }
            var result = new
            {
                status = (int)response.StatusCode,
                content_type = response.Content.Headers.ContentType?.ToString() ?? "application/json",
                body_base64 = Convert.ToBase64String(buffer.ToArray())
            };
            Console.Out.WriteLine(JsonSerializer.Serialize(result));
        }
        catch (Exception error)
        {
            // Never include provider payload, URL exception text, or request body.
            Console.Out.WriteLine(JsonSerializer.Serialize(new { error = "model_relay_failed",
                cause = error.GetType().Name }));
            Environment.ExitCode = 1;
        }
    }
}
