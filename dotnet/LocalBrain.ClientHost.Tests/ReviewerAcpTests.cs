using System.Diagnostics;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class ReviewerAcpTests
{
    internal static async Task RunAsync(string root)
    {
        ReviewQualification.GuardRoot(root);
        if (Directory.Exists(root)) throw new IOException("Fresh fixture root required");
        var cases = new List<object>();
        foreach (var failure in new[] { "lingering_child", "auth_loss", "wrong_model" }) {
            var work = Directory.CreateDirectory(Path.Combine(root, failure)).FullName;
            var start = new ProcessStartInfo(Environment.ProcessPath!) { WorkingDirectory = work,
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--review-acp-fixture");
            start.ArgumentList.Add(failure);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var rejected = false;
            try {
                using var acp = await ReviewerCopilotAcp.OpenAsync(start, deadline.Token);
                await acp.ReviewAsync("PUBLIC_FIXTURE_SHOULD_NOT_ARRIVE");
            } catch (Exception error) when (error is IOException or InvalidDataException) {
                rejected = true;
            }
            var prompt = File.Exists(Path.Combine(work, "prompt.txt"));
            var ownPid = int.Parse(File.ReadAllText(Path.Combine(work, "reviewer.pid")));
            var helperFile = Path.Combine(work, "helper.pid");
            var helperPid = File.Exists(helperFile) ? int.Parse(File.ReadAllText(helperFile)) : (int?)null;
            if (!rejected || prompt || (failure == "lingering_child" && helperPid is null))
                throw new Exception("Reviewer bootstrap failure did not stop packet: " + failure);
            await ConfirmExitedAsync(ownPid, deadline.Token);
            if (helperPid is int child) await ConfirmExitedAsync(child, deadline.Token);
            cases.Add(new { failure, blocked = true, prompt_received = false, processes_reaped = true });
        }
        var sealedWork = Directory.CreateDirectory(Path.Combine(root, "sealed")).FullName;
        var sealedStart = new ProcessStartInfo(Environment.ProcessPath!) { WorkingDirectory = sealedWork,
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        sealedStart.ArgumentList.Add("--review-acp-fixture");
        sealedStart.ArgumentList.Add("sealed");
        using var sealedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var sealedReviewer = await ReviewerCopilotAcp.OpenAsync(sealedStart, sealedDeadline.Token);
        var approved = await sealedReviewer.ReviewAsync("PUBLIC_FIXTURE_SEALED");
        if (approved.Status != "OK" || approved.Answer != "PUBLIC_FIXTURE_SEALED" ||
            File.ReadAllText(Path.Combine(sealedWork, "spawn-error.txt")) != "1816")
            throw new Exception("The sealed Job did not block child creation");
        Console.WriteLine(JsonSerializer.Serialize(new { status = "PASS", cases, sealed_job_child_win32 = 1816,
            sealed_packet_delivered = true, model_requests = 0 }));
    }

    private static async Task ConfirmExitedAsync(int pid, CancellationToken token)
    {
        try {
            using var process = Process.GetProcessById(pid);
            await process.WaitForExitAsync(token);
        } catch (ArgumentException) { }
    }

    internal static async Task FixtureAsync(string failure)
    {
        ReviewQualification.GuardRoot(Environment.CurrentDirectory);
        File.WriteAllText("reviewer.pid", Environment.ProcessId.ToString());
        while (await Console.In.ReadLineAsync() is { } line) {
            using var frame = JsonDocument.Parse(line);
            var id = frame.RootElement.GetProperty("id").GetInt32();
            object response;
            if (id == 1) response = new { jsonrpc = "2.0", id, result = new { protocolVersion = 1 } };
            else if (id == 4) {
                File.WriteAllText("prompt.txt", "PUBLIC_FIXTURE_RECEIVED");
                if (failure == "sealed") {
                    try {
                        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--review-acp-linger")
                            { UseShellExecute = false });
                        throw new Exception("Child unexpectedly started in sealed Job");
                    } catch (System.ComponentModel.Win32Exception error) {
                        File.WriteAllText("spawn-error.txt", error.NativeErrorCode.ToString());
                    }
                    var text = frame.RootElement.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString();
                    Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "session/update",
                        @params = new { sessionId = "fixture-3", update = new { sessionUpdate = "agent_message_chunk",
                            content = new { type = "text", text } } } }));
                }
                response = new { jsonrpc = "2.0", id, result = new { stopReason = "end_turn" } };
            } else if (id == 3 && failure == "auth_loss")
                response = new { jsonrpc = "2.0", id, error = new { code = -32000, message = "fixture auth lost" } };
            else {
                if (id == 2 && failure == "lingering_child") {
                    var start = new ProcessStartInfo(Environment.ProcessPath!, "--review-acp-linger") { UseShellExecute = false };
                    using var child = Process.Start(start)!;
                    File.WriteAllText("helper.pid", child.Id.ToString());
                }
                var model = id == 3 && failure == "wrong_model" ? "wrong-model" : "claude-opus-5.5";
                response = new { jsonrpc = "2.0", id, result = new { sessionId = $"fixture-{id}", models = new { currentModelId = model } } };
            }
            Console.WriteLine(JsonSerializer.Serialize(response));
        }
    }
}
