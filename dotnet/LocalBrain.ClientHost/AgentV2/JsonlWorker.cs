using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

/// <summary>Supervises one stdio JSONL child. stdout is protocol-only; stderr is counted, not persisted.</summary>
internal sealed class JsonlWorker : IAsyncDisposable
{
    private const int MaxLineChars = 2_000_000;
    private readonly Process process;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> requests = new();
    private readonly Task reader;
    private readonly Task stderrReader;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly Action<JsonElement>? onEvent;
    private int stderrLines;
    private bool disposed;

    private JsonlWorker(Process process, Action<JsonElement>? onEvent)
    {
        this.process = process;
        this.onEvent = onEvent;
        reader = ReadAsync();
        stderrReader = ReadStderrAsync();
    }

    public bool IsRunning => !process.HasExited;
    public int ExitCode => process.HasExited ? process.ExitCode : -1;
    public int StderrLineCount => Volatile.Read(ref stderrLines);
    public Task Completion => reader;

    public static JsonlWorker Start(ProcessStartInfo start, Action<JsonElement>? onEvent = null)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardInputEncoding = new UTF8Encoding(false,true);
        start.StandardOutputEncoding = new UTF8Encoding(false,true);
        start.StandardErrorEncoding = new UTF8Encoding(false,true);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Worker failed to start");
        return new JsonlWorker(process, onEvent);
    }

    public async Task<JsonElement> SendAsync(Dictionary<string, object?> request, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!IsRunning) throw new InvalidOperationException("Worker has exited");
        var id = Guid.NewGuid().ToString("N");
        request["id"] = id;
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!requests.TryAdd(id, pending)) throw new InvalidOperationException("Duplicate request ID");
        try
        {
            await WriteAsync(JsonSerializer.Serialize(request), cancellationToken);
            return await pending.Task.WaitAsync(timeout, cancellationToken);
        }
        finally { requests.TryRemove(id, out _); }
    }

    public async Task WriteAsync(string line, CancellationToken token = default)
    {
        if (line.Contains('\n') || line.Length > MaxLineChars)
            throw new ArgumentException("Invalid JSONL request size or delimiter");
        await writeLock.WaitAsync(token);
        try
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
        }
        finally { writeLock.Release(); }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length > MaxLineChars) throw new InvalidDataException("Worker JSONL line is too large");
                JsonElement message;
                try { using var parsed = JsonDocument.Parse(line); message = parsed.RootElement.Clone(); }
                catch (JsonException) { throw new InvalidDataException("Worker emitted invalid JSONL"); }
                if (message.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String &&
                    requests.TryGetValue(idValue.GetString()!, out var pending)) pending.TrySetResult(message);
                else onEvent?.Invoke(message);
            }
        }
        catch (Exception error)
        {
            foreach (var pending in requests.Values) pending.TrySetException(error);
            throw;
        }
        finally
        {
            foreach (var pending in requests.Values)
                pending.TrySetException(new IOException("Worker closed its JSONL stream"));
        }
    }

    private async Task ReadStderrAsync()
    {
        try
        {
            while (await process.StandardError.ReadLineAsync() is not null)
                Interlocked.Increment(ref stderrLines);
        }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        finally
        {
            try {await Task.WhenAll(reader, stderrReader);}
            finally {process.Dispose();writeLock.Dispose();}
        }
    }
}
