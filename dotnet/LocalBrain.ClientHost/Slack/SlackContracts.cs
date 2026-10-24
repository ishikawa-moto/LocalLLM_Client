using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalBrain.ClientHost.AgentV2;

namespace LocalBrain.ClientHost.Slack;

// Host code constructs the classification; neither Slack text nor model JSON selects it.
internal sealed record SlackRestriction
{
    public string Origin => "slack";
    public string DataClass => "slack_restricted";
    public string ExternalAi => "deny";
    public bool CloudFallback => false;
    public static readonly SlackRestriction Instance = new();
}

internal static class RestrictedExecution
{
    private static readonly AsyncLocal<int> depth = new();
    internal static bool Active => depth.Value > 0;
    private sealed class Scope : IDisposable { public void Dispose() { depth.Value--; } }
    internal static IDisposable EnterSlack() { depth.Value++; return new Scope(); }
    // Browser OCR shares the same deny-external/deny-agent boundary; source identity stays chat.
    internal static IDisposable EnterChat() { depth.Value++; return new Scope(); }
    internal static void DenyExternal() { if (Active) throw new UnauthorizedAccessException("SLACK_EXTERNAL_AI_DENIED"); }
    internal static void DenyAgentTools() { if (Active) throw new UnauthorizedAccessException("SLACK_AGENT_TOOLS_DENIED"); }
}

internal sealed record SlackOptions(bool Enabled, string Root, string TeamId, string ChannelId, string BotUserId,
    string AppTokenFile, string BotTokenFile, int MaxQueue = 100, int MaxFileBytes = 4 * 1024 * 1024,
    int MaxFiles = 4, int OutputTokens = 1024, int InferenceSeconds = 120, int CompressionThreshold = 8000,
    bool ReturnFileResults = false, bool AllowSharedThreads = true)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static string ConfigPath => Path.Combine(CanonicalStore.DefaultRoot, "slack", "config.json");
    internal static SlackOptions Load(string path)
    {
        CanonicalStore.GuardPath(path);
        var bytes = File.ReadAllBytes(path); if (bytes.Length > 32768) throw new InvalidDataException("SLACK_CONFIG_SIZE");
        var value = JsonSerializer.Deserialize<SlackOptions>(bytes, Json) ?? throw new InvalidDataException("SLACK_CONFIG_INVALID");
        value.Validate(); return value;
    }
    internal void Validate()
    {
        if (!Regex.IsMatch(TeamId, "^T[A-Z0-9]{2,32}$") || !Regex.IsMatch(ChannelId, "^C[A-Z0-9]{2,32}$") ||
            !Regex.IsMatch(BotUserId, "^U[A-Z0-9]{2,32}$") || MaxQueue is <1 or >1000 ||
            MaxFileBytes is <1024 or >4*1024*1024 || MaxFiles is <1 or >4 || OutputTokens is <64 or >1024 ||
            InferenceSeconds is <5 or >120 || CompressionThreshold is <1024 or >12000)
            throw new InvalidDataException("SLACK_CONFIG_INVALID");
        foreach (var path in new[] { Root, AppTokenFile, BotTokenFile }) {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\")) throw new InvalidDataException("SLACK_PATH_INVALID");
            CanonicalStore.GuardPath(path);
        }
        // State must never be a project artifact or normal TaskMemory/SecondBrain corpus.
        if (File.Exists(Path.Combine(Root,".git")) || Directory.Exists(Path.Combine(Root,".git"))) throw new InvalidDataException("SLACK_PRIVATE_ROOT_REQUIRED");
        for(var parent=new DirectoryInfo(Root).Parent;parent is not null;parent=parent.Parent)
            if(File.Exists(Path.Combine(parent.FullName,".git"))||Directory.Exists(Path.Combine(parent.FullName,".git")))throw new InvalidDataException("SLACK_PRIVATE_ROOT_REQUIRED");
    }
    internal static string Secret(string path, string prefix)
    {
        CanonicalStore.GuardPath(path); var text = File.ReadAllText(path).Trim();
        if (text.Length is <20 or >1024 || !text.StartsWith(prefix, StringComparison.Ordinal) || text.Any(char.IsWhiteSpace))
            throw new InvalidDataException("SLACK_TOKEN_INVALID");
        return text;
    }
}

internal sealed record SlackMention(string EventId, string TeamId, string ChannelId, string UserId, string Ts,
    string RootTs, string Text, string[] FileIds, DateTimeOffset ReceivedAt)
{
    internal static SlackMention? Parse(JsonElement payload, SlackOptions options, DateTimeOffset received)
    {
        string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        if (S(payload,"type") != "event_callback" || S(payload,"team_id") != options.TeamId || !payload.TryGetProperty("event",out var ev)) return null;
        if (S(ev,"type") != "app_mention" || S(ev,"channel") != options.ChannelId || ev.TryGetProperty("bot_id",out _) || ev.TryGetProperty("subtype",out _)) return null;
        var user = S(ev,"user"); var text = S(ev,"text"); var ts = S(ev,"ts"); var root = S(ev,"thread_ts");
        if (user == options.BotUserId || !Regex.IsMatch(user,"^U[A-Z0-9]{2,32}$") || !text.Contains("<@"+options.BotUserId+">",StringComparison.Ordinal) ||
            text.Contains("<!channel",StringComparison.Ordinal) || text.Contains("<!here",StringComparison.Ordinal) || text.Length > 16000 ||
            !ValidTs(ts) || (root!="" && !ValidTs(root))) return null;
        var id = S(payload,"event_id"); if (!Regex.IsMatch(id,"^[A-Za-z0-9_-]{3,100}$")) return null;
        var files = ev.TryGetProperty("files",out var f) && f.ValueKind==JsonValueKind.Array ? f.EnumerateArray().Select(x=>S(x,"id")).ToArray() : [];
        if (files.Length > options.MaxFiles || files.Any(x=>!Regex.IsMatch(x,"^F[A-Z0-9]{2,32}$"))) return null;
        return new(id,options.TeamId,options.ChannelId,user,ts,root==""?ts:root,text,files,received);
    }
    internal static bool ValidTs(string ts) => Regex.IsMatch(ts,@"^[0-9]{10,12}\.[0-9]{6}$")&&decimal.TryParse(ts,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var n)&&n<=253402300799m;
    internal static DateTimeOffset Timestamp(string ts) => DateTimeOffset.FromUnixTimeMilliseconds((long)(decimal.Parse(ts,CultureInfo.InvariantCulture)*1000));
}

internal static class SlackHash
{
    internal static string Of(string value) => Of(Encoding.UTF8.GetBytes(value));
    internal static string Of(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    internal static string Id() => Guid.NewGuid().ToString("N");
}
