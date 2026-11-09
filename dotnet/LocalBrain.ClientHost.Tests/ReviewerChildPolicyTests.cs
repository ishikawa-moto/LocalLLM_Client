using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class ReviewerChildPolicyTests
{
    internal static async Task RunAsync(string root)
    {
        ReviewQualification.GuardRoot(root);
        if (Directory.Exists(root)) throw new IOException("Fresh fixture directory required");
        Directory.CreateDirectory(root);
        var exe = Environment.ProcessPath!;
        var baseline = Path.Combine(root, "control.txt");
        var blocked = Path.Combine(root, "blocked.txt");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using (var normal = Process.Start(Start(exe, baseline))!) {
            normal.StandardInput.Close();
            var result = await normal.StandardOutput.ReadToEndAsync(deadline.Token);
            await normal.WaitForExitAsync(deadline.Token);
            if (normal.ExitCode != 0 || !File.Exists(baseline) || !result.Contains("child_started"))
                throw new Exception("Positive child-start control did not execute");
        }
        using var reviewer = WindowsChildJob.StartReviewer(Start(exe, blocked));
        var packet = Encoding.UTF8.GetBytes("Public qualification fixture 日本語\n");
        await reviewer.Input!.WriteAsync(packet, deadline.Token);
        reviewer.Input.Dispose();
        var error = reviewer.Error.ReadToEndAsync(deadline.Token);
        var output = await reviewer.Output.ReadToEndAsync(deadline.Token);
        await reviewer.Process.WaitForExitAsync(deadline.Token);
        if (reviewer.Process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new Exception($"Reviewer fixture exited {reviewer.Process.ExitCode}: {await error}");
        using var proof = JsonDocument.Parse(output);
        var evidence = proof.RootElement;
        if (reviewer.Process.ExitCode != 0 || File.Exists(blocked) ||
            evidence.GetProperty("child").GetString() != "denied" ||
            evidence.GetProperty("win32").GetInt32() != 367 ||
            evidence.GetProperty("stdin_sha256").GetString() != Convert.ToHexString(SHA256.HashData(packet)))
            throw new Exception("Child creation or stdin restriction did not match: " + output);
        Console.WriteLine(JsonSerializer.Serialize(new { status = "PASS", control_child_executed = true,
            reviewer_pid = reviewer.Process.Id, reviewer_child = evidence, blocked_marker_exists = false,
            model_requests = 0 }));

        ProcessStartInfo Start(string binary, string marker)
        {
            var start = new ProcessStartInfo(binary) { WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--review-child-policy-attempt", exe, marker }) start.ArgumentList.Add(arg);
            return start;
        }
    }

    internal static async Task AttemptAsync(string exe, string marker)
    {
        using var packet = new MemoryStream();
        await Console.OpenStandardInput().CopyToAsync(packet);
        var hash = Convert.ToHexString(SHA256.HashData(packet.ToArray()));
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.ArgumentList.Add("--review-child-policy-marker");
        start.ArgumentList.Add(marker);
        try {
            using var child = Process.Start(start)!;
            await child.WaitForExitAsync();
            Console.WriteLine(JsonSerializer.Serialize(new { child = "child_started", child.ExitCode }));
        } catch (Win32Exception error) {
            Console.WriteLine(JsonSerializer.Serialize(new { child = "denied", win32 = error.NativeErrorCode, stdin_sha256 = hash }));
        }
    }

    internal static void WriteMarker(string path)
    {
        ReviewQualification.GuardRoot(Path.GetDirectoryName(path)!);
        if (Path.GetFileName(path) is not ("control.txt" or "blocked.txt"))
            throw new UnauthorizedAccessException("Expected dedicated fixture marker");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        file.Write("public fixture"u8);
    }

    internal static async Task LocalTransportAsync(string exe, string[] args)
    {
        ReviewQualification.GuardRoot(Environment.CurrentDirectory);
        if (!Path.IsPathFullyQualified(exe) || Path.GetFileName(exe) is not ("codex.exe" or "copilot.exe"))
            throw new UnauthorizedAccessException("Expected installed reviewer executable");
        if (!Uri.TryCreate(Environment.GetEnvironmentVariable("HTTPS_PROXY"), UriKind.Absolute, out var proxy) ||
            proxy.Host != "127.0.0.1" || proxy.Scheme != "http" ||
            (Path.GetFileName(exe) == "copilot.exe" && Environment.GetEnvironmentVariable("COPILOT_OFFLINE") != "true"))
            throw new UnauthorizedAccessException("Expected loopback test transport");
        var start = new ProcessStartInfo(exe) { WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var reviewer = WindowsChildJob.StartReviewer(start);
        var output = reviewer.Output.ReadToEndAsync(deadline.Token);
        var error = reviewer.Error.ReadToEndAsync(deadline.Token);
        await Console.OpenStandardInput().CopyToAsync(reviewer.Input!, deadline.Token);
        reviewer.Input!.Dispose();
        await reviewer.Process.WaitForExitAsync(deadline.Token);
        Console.Write(await output);
        Console.Error.Write(await error);
        Environment.ExitCode = reviewer.Process.ExitCode;
    }

    // ACP initialization and a new empty session exercise the native launch and model
    // resolution without session/prompt, tool requests, or exporting a review packet.
    internal static async Task CopilotMetadataAsync(string exe, string expectedHash, string root)
    {
        ReviewQualification.GuardRoot(root);
        if (!Path.IsPathFullyQualified(exe) || Path.GetFileName(exe) != "copilot.exe" || Directory.Exists(root))
            throw new UnauthorizedAccessException("Expected an installed Copilot binary and a fresh trial root");
        CanonicalStore.GuardPath(exe);
        using var binary = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(binary)), expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Unexpected Copilot SHA-256");
        Directory.CreateDirectory(root);
        var start = ReviewerCli.StartInfo(exe, root, "copilot", "high");
        start.ArgumentList.Insert(0, "--acp");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var reviewer = WindowsChildJob.StartReviewer(start);
        var init = await CallAsync(1, "initialize", new { protocolVersion = 1, clientCapabilities = new { } });
        var session = await CallAsync(2, "session/new", new { cwd = root, mcpServers = Array.Empty<string>() });
        reviewer.Input!.Dispose();
        await reviewer.Process.WaitForExitAsync(deadline.Token);
        var found = session.TryGetProperty("result", out var result) &&
            result.TryGetProperty("sessionId", out _) &&
            result.GetRawText().Contains("\"claude-opus-5.5\"", StringComparison.Ordinal);
        var errorCode = session.TryGetProperty("error", out var error) ? error.GetProperty("code").GetInt32() : (int?)null;
        Console.WriteLine(JsonSerializer.Serialize(new { provider = "copilot", requested_model = ReviewerCli.Model("copilot"),
            init_ok = init.TryGetProperty("result", out _), model_present_in_session = found,
            acp_error_code = errorCode, cli_exit = reviewer.Process.ExitCode, model_requests = 0,
            windows_child_creation_blocked = true }));
        if (!found || reviewer.Process.ExitCode != 0) Environment.ExitCode = 2;

        async Task<JsonElement> CallAsync(int id, string method, object parameters)
        {
            var frame = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }) + "\n";
            await reviewer.Input!.WriteAsync(Encoding.UTF8.GetBytes(frame), deadline.Token).AsTask().WaitAsync(deadline.Token);
            while (await reviewer.Output.ReadLineAsync(deadline.Token).AsTask().WaitAsync(deadline.Token) is { } line) {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("id", out var value) && value.TryGetInt32(out var responseId) && responseId == id)
                    return json.RootElement.Clone();
            }
            throw new IOException("ACP response was missing");
        }
    }
}
