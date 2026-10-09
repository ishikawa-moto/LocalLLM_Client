using System.Text.Json;

namespace LocalBrain.ClientHost;

internal sealed class Audit(ClientConfig config)
{
    private readonly object sync = new();
    public void Write(string name, object? fields = null)
    {
        var record = new Dictionary<string, object?> { ["timestamp"] = DateTimeOffset.UtcNow, ["event"] = name };
        if (fields is not null)
            foreach (var item in JsonSerializer.SerializeToElement(fields).EnumerateObject()) record[item.Name] = item.Value.Clone();
        lock (sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(config.AuditLog)!);
            File.AppendAllText(config.AuditLog, JsonSerializer.Serialize(record) + Environment.NewLine);
        }
    }
}
