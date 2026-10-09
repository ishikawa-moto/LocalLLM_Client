using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

/// <summary>A short-lived, one-use approval issued only by the interactive CLI.</summary>
internal static class HighRiskApproval
{
    private sealed record Proof(string RequestHash, string GitHead, DateTimeOffset ExpiresAt);

    public static async Task ApproveAsync(string root, AgentTaskRequest request,
        CancellationToken token = default)
    {
        if (request.EffectiveRisk != "HIGH" || !request.ApprovedHighRisk)
            throw new InvalidDataException("Only an explicitly requested HIGH task can be approved");
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new InvalidOperationException("HIGH approval requires an interactive user terminal");
        var before = await GitEvidence.CaptureAsync(root, request.AllowedFiles, token);
        if (before.ChangedFiles.Length != 0)
            throw new InvalidOperationException("HIGH approval requires a clean Git working tree");
        var hash = AgentTaskRunner.RequestHash(request);
        Console.WriteLine($"Workspace: {root}");
        Console.WriteLine($"Request SHA-256: {hash}");
        Console.WriteLine($"Git HEAD: {before.Head}");
        Console.WriteLine("Allowed changed files:");
        foreach (var file in request.AllowedFiles) Console.WriteLine($"  {file}");
        Console.WriteLine("Required tests:");
        foreach (var test in request.RequiredTests)
            Console.WriteLine($"  {test.Kind} {test.Target ?? ""}");
        Console.WriteLine("Review the request JSON yourself. Type the complete request SHA-256 to approve this exact task once:");
        if (!string.Equals(Console.ReadLine(), hash, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("HIGH approval was not confirmed");
        var directory = Path.Combine(CanonicalStore.DefaultRoot,"pending-approvals",HostTaskJournal.WorkspaceId(root));
        Directory.CreateDirectory(directory);
        if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException("Linked Agent v2 state directories are not allowed");
        var path = Path.Combine(directory, "high-risk-approval.json");
        if (File.Exists(path) && new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException("Linked approval files are not allowed");
        var temporary = Path.Combine(directory, $"high-risk-approval.{Guid.NewGuid():N}.tmp");
        try
        {
            var proof = new Proof(hash, before.Head, DateTimeOffset.UtcNow.AddMinutes(30));
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(proof), token);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Console.WriteLine("HIGH approval recorded for one run within 30 minutes.");
    }

    public static void Consume(string root, string requestHash, string head,HostTaskJournal? journal=null)
    {
        var path = Path.Combine(CanonicalStore.DefaultRoot,"pending-approvals",HostTaskJournal.WorkspaceId(root),"high-risk-approval.json");
        CanonicalStore.GuardPath(path);
        if (!File.Exists(path) || new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            new FileInfo(path).Length > 1_024)
            throw new UnauthorizedAccessException("A valid interactive HIGH approval is required");
        Proof? proof;
        try { proof = JsonSerializer.Deserialize<Proof>(File.ReadAllText(path)); }
        catch (JsonException) { throw new UnauthorizedAccessException("HIGH approval is invalid"); }
        if (proof is null || proof.RequestHash != requestHash || proof.GitHead != head ||
            proof.ExpiresAt <= DateTimeOffset.UtcNow ||
            proof.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(30))
            throw new UnauthorizedAccessException("HIGH approval does not match this task or Git HEAD");
        if(journal is null)throw new UnauthorizedAccessException("Approval requires a canonical task binding");
        journal.BindApproval(CanonicalStore.Hash(System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path))),proof.ExpiresAt);
        File.Delete(path);
    }
}
