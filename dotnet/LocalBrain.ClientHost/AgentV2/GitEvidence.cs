using System.Diagnostics;
using System.Text;

namespace LocalBrain.ClientHost.AgentV2;

/// <summary>Builds a bounded, fresh review packet from the current Git working tree.</summary>
internal static class GitEvidence
{
    internal sealed record Snapshot(string Head, string[] ChangedFiles, string[] UnexpectedFiles,
        string ReviewDiff);

    public static async Task<Snapshot> CaptureAsync(string root, string[] allowedFiles,
        CancellationToken token = default)
    {
        var head = (await GitAsync(root, ["rev-parse", "HEAD"], token)).Trim();
        var tracked = await GitAsync(root, ["diff", "HEAD", "--name-only", "-z", "--"], token);
        var untracked = await GitAsync(root, ["ls-files", "--others", "--exclude-standard", "-z"], token);
        var changed = SplitNull(tracked).Concat(SplitNull(untracked)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var allowed = allowedFiles.Select(file => file.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unexpected = changed.Where(file => !allowed.Contains(file)).ToArray();
        var diffArgs = new[] { "diff", "HEAD", "--no-ext-diff", "--no-color", "--unified=2", "--" }
            .Concat(allowedFiles).ToArray();
        var diff = await GitAsync(root, diffArgs, token);
        if (diff.Length > 24_000) throw new InvalidDataException("Review diff exceeds 24,000 characters");
        var review = new StringBuilder(diff);
        foreach (var file in SplitNull(untracked))
        {
            if (!allowed.Contains(file)) continue;
            var full = Path.GetFullPath(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)));
            CanonicalStore.GuardPath(full);
            if (!CanonicalStore.Within(root,full) || !File.Exists(full) ||
                new FileInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("Review file escaped the Git root");
            var info = new FileInfo(full);
            if (info.Length > 4_000) throw new InvalidDataException("New review file exceeds 4,000 bytes");
            var bytes = await File.ReadAllBytesAsync(full, token);
            if (bytes.Length > 4_000) throw new InvalidDataException("New review file exceeds 4,000 bytes");
            var content = new UTF8Encoding(false, true).GetString(bytes);
            if (content.Contains('\0')) throw new InvalidDataException("Binary review file is unsupported");
            review.Append("\n--- new file: ").Append(file)
                .Append(" (bytes=").Append(bytes.Length)
                .Append(", newline_at_eof=").Append(bytes.Length > 0 && bytes[^1] == (byte)'\n')
                .Append(") ---\n").Append(content).Append('\n');
            if (review.Length > 32_000) throw new InvalidDataException("Review packet exceeds limit");
        }
        return new(head, changed, unexpected, review.ToString());
    }

    public static async Task RequireIgnoredAsync(string root,string path,CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(await GitAsync(root,["check-ignore","--",path],token)))
            throw new UnauthorizedAccessException("Host staging must be excluded from Git review");
    }
    private static IEnumerable<string> SplitNull(string value) =>
        value.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(file => file.Replace('\\', '/'));

    private static async Task<string> GitAsync(string root, string[] args, CancellationToken token)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add($"safe.directory={root}");
        start.ArgumentList.Add("-C"); start.ArgumentList.Add(root);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git did not start");
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var value = await stdout;
            _ = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException("Git evidence command failed");
            if (value.Length > 100_000) throw new InvalidDataException("Git evidence is too large");
            return value;
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
