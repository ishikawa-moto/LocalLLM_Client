using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

internal sealed record PiProgressSnapshot(int ToolCalls, int ToolErrors, int FilesRead,
    int FilesChanged, int SameFileReadCount, int SameCommandCount, int SameErrorCount,
    int ToolCallsSinceProgress, bool NoProgress, int DistinctObservations = 0);

/// <summary>Counts repeated work without storing tool arguments or output text.</summary>
internal sealed class ProgressTracker(Func<string,string,bool>? commandVerifier=null)
{
    // Pi reports a tool cancelled by the RPC abort with this exact result text.
    internal const string HostAbortedToolText = "Operation aborted";
    private readonly HashSet<string> observedCommandReceipts=new();
    private readonly HashSet<string> observations = new();
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
    /// <summary>Successful read-only observations whose output was not seen before in this run.
    /// Varying arguments that return the same output is not new evidence, and observations never
    /// reset <see cref="ToolCallsSinceProgress"/>.</summary>
    public int DistinctObservations { get; private set; }
    /// <summary>Tools cancelled by a Host-requested abort; these are not tool failures.</summary>
    public int AbortedTools { get; private set; }
    public bool NoProgress => SameErrorCount >= 3 || SameCommandCount >= 4 ||
        SameFileReadCount >= 4 || ToolCallsSinceProgress >= 15;

    public PiProgressSnapshot Snapshot() => new(ToolCalls, ToolErrors, FilesRead,
        FilesChanged, SameFileReadCount, SameCommandCount, SameErrorCount,
        ToolCallsSinceProgress, NoProgress, DistinctObservations);

    public void Observe(JsonElement message, bool hostAbortRequested = false)
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
            if (!failed && name is "read" or "ls" or "grep" or "find" or "host_memory_search" or "host_recall" &&
                ResultText(message) is { Length: > 0 } output && observations.Add(Hash(name + "\n" + output)))
                DistinctObservations++;
            if(!failed && name is ("host_command_start" or "host_command_status") && message.TryGetProperty("result",out var commandResult) && commandResult.ValueKind==JsonValueKind.Object &&
                commandResult.TryGetProperty("details",out var details) && details.ValueKind==JsonValueKind.Object && details.TryGetProperty("operation_id",out var operationId) && operationId.ValueKind==JsonValueKind.String &&
                details.TryGetProperty("receipt_hash",out var receiptHash) && receiptHash.ValueKind==JsonValueKind.String){
                var hash=receiptHash.GetString();var commandId=operationId.GetString();
                if(hash is not null && commandId is not null && !observedCommandReceipts.Contains(hash) && commandVerifier?.Invoke(commandId,hash)==true){
                    observedCommandReceipts.Add(hash);ToolCallsSinceProgress=0;SameErrorCount=SameCommandCount=SameFileReadCount=0;lastError=lastCommand=lastRead=null;
                }
            }
            if (failed)
            {
                var sample = "";
                if (message.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    foreach (var block in content.EnumerateArray())
                        if (block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        {
                            sample = text.GetString()![..Math.Min(512, text.GetString()!.Length)];
                            break;
                        }
                if (hostAbortRequested && sample == HostAbortedToolText)
                {
                    AbortedTools++;
                    return;
                }
                ToolErrors++;
                var fingerprint = Hash(name + sample);
                SameErrorCount = fingerprint == lastError ? SameErrorCount + 1 : 1;
                lastError = fingerprint;
            }
            else if (name is "edit" or "write" or "host_edit" or "host_write" ||
                name=="host_edit_and_validate" && message.TryGetProperty("result",out var fused) &&
                fused.TryGetProperty("details",out var fusionDetails) && fusionDetails.ValueKind==JsonValueKind.Object &&
                fusionDetails.TryGetProperty("edit_hash",out var editHash) && editHash.ValueKind==JsonValueKind.String)
            {
                FilesChanged++;
                ToolCallsSinceProgress = 0;
                SameErrorCount = 0;
                lastError = null;
            }
        }
    }

    private static string ResultText(JsonElement message)
    {
        if (!message.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return "";
        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
            if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("text", out var value) &&
                value.ValueKind == JsonValueKind.String) text.Append(value.GetString()).Append('\n');
        return text.ToString().Trim();
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
