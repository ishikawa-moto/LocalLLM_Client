using System.Diagnostics;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

/// <summary>Keeps the CPU model loaded across event-driven decisions in a long-running host.</summary>
internal sealed class LayaSupervisor : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private JsonlWorker? worker;
    private bool disposed;
    public int Restarts { get; private set; }
    public int Errors { get; private set; }

    public async Task<(string Action, double Confidence, int LatencyMs)> PredictAsync(
        object compactState, CancellationToken token = default)
    {
        var reply = await SendAsync("action", compactState, token);
        return (reply.GetProperty("action").GetString() ?? "",
            reply.GetProperty("confidence").GetDouble(),
            reply.GetProperty("latency_ms").GetInt32());
    }

    public async Task<(string Route, double Confidence, int LatencyMs)> PredictReviewRouteAsync(
        object compactState, CancellationToken token = default)
    {
        var reply = await SendAsync("review_route", compactState, token);
        return (reply.GetProperty("review_route").GetString() ?? "",
            reply.GetProperty("confidence").GetDouble(),
            reply.GetProperty("latency_ms").GetInt32());
    }

    private async Task<JsonElement> SendAsync(string kind, object compactState, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (worker is null || !worker.IsRunning)
            {
                if (worker is not null)
                {
                    await worker.DisposeAsync();
                    worker = null;
                    Restarts++;
                }
                var script = await WslWorkspace.MapFileAsync(Path.Combine(AppContext.BaseDirectory,
                    "scripts", "laya_worker.py"), token);
                var start = new ProcessStartInfo("wsl.exe");
                foreach (var arg in new[] { "-d", "Ubuntu", "--exec",
                    "/home/worker/localbrain-v2/laya-venv/bin/python", "-u", script, "--threads", "2" })
                    start.ArgumentList.Add(arg);
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                worker = JsonlWorker.Start(start, message =>
                {
                    if (message.TryGetProperty("type", out var kind) && kind.GetString() == "ready")
                        ready.TrySetResult();
                });
                var startup = await Task.WhenAny(ready.Task, worker.Completion)
                    .WaitAsync(TimeSpan.FromMinutes(5), token);
                if (startup != ready.Task) throw new IOException("Laya exited during startup");
            }
            var reply = await worker.SendAsync(new() { ["kind"] = kind, ["state"] = compactState },
                TimeSpan.FromSeconds(60), token);
            if (reply.TryGetProperty("error", out _)) throw new InvalidDataException("Laya inference failed");
            return reply;
        }
        catch
        {
            Errors++;
            if (worker is not null)
            {
                await worker.DisposeAsync();
                worker = null;
            }
            throw;
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            if (worker is not null) await worker.DisposeAsync();
            worker = null;
        }
        // Keep the gate available so a concurrent or repeated DisposeAsync can
        // observe disposed and return. PredictAsync then fails its disposed check.
        finally { gate.Release(); }
    }
}
