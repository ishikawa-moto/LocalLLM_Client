using System.Security.Cryptography;

namespace LocalBrain.ClientHost.AgentV2;

internal static class HostDecisionTransport
{
    internal static async Task<string> DispatchAsync(HostTaskJournal journal,string approvalId,ClientConfig config,CancellationToken token=default)
    {
        if(!OperatingSystem.IsWindows() || HostTaskJournal.Current!=journal)throw new UnauthorizedAccessException("Decision dispatch must belong to the Windows Host");
        using var certificate=config.LoadCertificate();
        var certificateHash=certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
        using var gateway=new GatewayClient(config);
        var adapter=new DecisionAdoption(journal.Canonical,journal.TaskId);
        return await adapter.DispatchAsync(approvalId,certificateHash,(reservation,cancellation)=>gateway.SendOwnedDecisionAsync(journal,reservation,cancellation),token);
    }
}
