using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

internal sealed record PiProgressSnapshot(int ToolCalls, int ToolErrors, int FilesRead,
    int FilesChanged, int SameFileReadCount, int SameCommandCount, int SameErrorCount,
    int ToolCallsSinceProgress, bool NoProgress);

/// <summary>Counts repeated work without storing tool arguments or output text.</summary>
internal sealed class ProgressTracker
{
    private readonly Dictionary<string, string> active = new();
    private string? lastRead;
    private string? lastCommand;
    private string? lastError;
    public int ToolCalls { get; private set; }
    public int ToolErrors { get; private set; }
    public int FilesRead { get; private set; }
    public int FilesChanged { get; private set; }
    public int ObservationRecalls { get; private set; }
    public int SameFileReadCount { get; private set; }
    public int SameCommandCount { get; private set; }
    public int SameErrorCount { get; private set; }
    public int ToolCallsSinceProgress { get; private set; }
    public bool NoProgress => SameErrorCount >= 3 || SameCommandCount >= 4 ||
        SameFileReadCount >= 4 || ToolCallsSinceProgress >= 15;

    public PiProgressSnapshot Snapshot() => new(ToolCalls, ToolErrors, FilesRead,
        FilesChanged, SameFileReadCount, SameCommandCount, SameErrorCount,
        ToolCallsSinceProgress, NoProgress);

    public void Observe(JsonElement message)
    {
        if (!message.TryGetProperty("type", out var type)) return;
        if (type.GetString() == "tool_execution_start")
        {
            ToolCalls++;
            ToolCallsSinceProgress++;
            var id = message.GetProperty("toolCallId").GetString();
            var name = message.GetProperty("toolName").GetString();
            if (id is null || name is null) return;
            active[id] = name;
            if (name is "obs_recall" or "host_recall") ObservationRecalls++;
            if (!message.TryGetProperty("args", out var args)) return;
            if (name == "read" && args.TryGetProperty("path", out var path) &&
                path.ValueKind == JsonValueKind.String)
            {
                FilesRead++;
                var fingerprint = Hash(path.GetString()!);
                SameFileReadCount = fingerprint == lastRead ? SameFileReadCount + 1 : 0;
                lastRead = fingerprint;
            }
            if (name is "bash" or "powershell" && args.TryGetProperty("command", out var command) &&
                command.ValueKind == JsonValueKind.String)
            {
                var fingerprint = Hash(command.GetString()!);
                SameCommandCount = fingerprint == lastCommand ? SameCommandCount + 1 : 0;
                lastCommand = fingerprint;
            }
        }
        else if (type.GetString() == "tool_execution_end")
        {
            var id = message.GetProperty("toolCallId").GetString();
            var name = id is not null && active.Remove(id, out var value) ? value : "unknown";
            var failed = message.TryGetProperty("isError", out var error) &&
                error.ValueKind == JsonValueKind.True;
            if (failed)
            {
                ToolErrors++;
                var sample = "";
                if (message.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    foreach (var block in content.EnumerateArray())
                        if (block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        {
                            sample = text.GetString()![..Math.Min(512, text.GetString()!.Length)];
                            break;
                        }
                var fingerprint = Hash(name + sample);
                SameErrorCount = fingerprint == lastError ? SameErrorCount + 1 : 1;
                lastError = fingerprint;
            }
            else if (name is "edit" or "write" or "host_edit" or "host_write")
            {
                FilesChanged++;
                ToolCallsSinceProgress = 0;
                SameErrorCount = 0;
                lastError = null;
            }
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
