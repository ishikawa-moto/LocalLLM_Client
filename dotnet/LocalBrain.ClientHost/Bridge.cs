using System.Net;
using System.Text.Json;

namespace LocalBrain.ClientHost;

internal sealed class Bridge(ClientConfig config, GatewayClient gateway, Audit audit)
{
    private readonly HttpListener listener = new();
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        listener.Prefixes.Add($"http://{IPAddress.Loopback}:{config.BridgePort}/"); listener.Start();
        audit.Write("bridge_started", new { address = $"{IPAddress.Loopback}:{config.BridgePort}" });
        var heartbeat = HeartbeatAsync(cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
                _ = Task.Run(() => ForwardAsync(context, cancellationToken), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); await TryShutdownAsync(); await heartbeat.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
    }

    private async Task ForwardAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var requestId = context.Request.Headers["X-LocalBrain-Request-Id"];
        if (!Guid.TryParse(requestId, out _)) requestId = Guid.NewGuid().ToString();
        try
        {
            using var response = await gateway.SendAsync(new HttpMethod(context.Request.HttpMethod), context.Request.RawUrl ?? "/",
                context.Request.HasEntityBody ? context.Request.InputStream : null, context.Request.ContentType,
                context.Request.HasEntityBody ? context.Request.ContentLength64 : null, requestId!, cancellationToken);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            context.Response.Headers["X-LocalBrain-Request-Id"] = requestId;
            await response.Content.CopyToAsync(context.Response.OutputStream, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            audit.Write("bridge_host_owned_route_rejected",new{requestId});
            context.Response.StatusCode=403;context.Response.ContentType="application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream,new{error="This operation requires Windows Host approval dispatch",request_id=requestId},cancellationToken:cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            audit.Write("bridge_request_failed", new { requestId, error = error.GetType().Name });
            context.Response.StatusCode = 502; context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream, new { error = "LocalBrain gateway unavailable", request_id = requestId,
                timestamp = DateTimeOffset.UtcNow, retry = true, log = config.AuditLog }, cancellationToken: cancellationToken);
        }
        finally { context.Response.Close(); }
    }

    private async Task HeartbeatAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.HeartbeatSeconds));
        while (await timer.WaitForNextTickAsync(token))
            try { await gateway.JsonAsync(HttpMethod.Post, "/v1/control/heartbeat", "{}", token); }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            { audit.Write("heartbeat_failed", new { error = error.GetType().Name }); }
    }
    private async Task TryShutdownAsync()
    {
        try { await gateway.JsonAsync(HttpMethod.Post, "/v1/control/client-shutdown", "{}"); } catch { }
    }
}
