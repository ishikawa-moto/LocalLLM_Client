using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

internal sealed record AutomaticDecisionPolicy(int version,bool enabled,string policy,string workspace,string[] projects,string authorization_source)
{
    internal const string Name="decision-review-and-reaffirm-v1";
    internal static string FilePath=>Path.Combine(CanonicalStore.DefaultRoot,"automatic-decision-policy.json");
    internal static (AutomaticDecisionPolicy Policy,string Hash,byte[] Bytes)? Load(ClientConfig config,string? hostRoot=null)
    {
        var path=hostRoot is null?FilePath:Path.Combine(hostRoot,"automatic-decision-policy.json");
        if(!File.Exists(path))return null;CanonicalStore.GuardPath(path);
        var bytes=File.ReadAllBytes(path);if(bytes.Length>16000)throw new InvalidDataException("Decision policy exceeds budget");
        var policy=JsonSerializer.Deserialize<AutomaticDecisionPolicy>(bytes)??throw new InvalidDataException("Decision policy missing");
        if(policy.version!=1||policy.policy!=Name||string.IsNullOrWhiteSpace(policy.authorization_source)||policy.projects is null or {Length:0 or >30})throw new InvalidDataException("Unknown automatic decision policy");
        if(!policy.enabled)return null;
        var root=new ProjectAllowlist(config).Resolve(policy.workspace);
        if(!string.Equals(root,Path.GetFullPath(policy.workspace),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Policy workspace is not an exact registered Git root");
        return (policy,CanonicalStore.Hash(bytes),bytes);
    }
}
