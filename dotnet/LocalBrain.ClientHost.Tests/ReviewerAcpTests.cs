using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class ReviewerAcpTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, Width, Height, CharsX, CharsY, Fill, Flags;
        public ushort Show, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string executable, IntPtr command, IntPtr processAttrs,
        IntPtr threadAttrs, bool inherit, uint flags, IntPtr environment, string? cwd,
        ref StartupInfo startup, out ProcessInfo child);

    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    internal static async Task VerifyJobScopeAsync(string root)
    {
        ReviewQualification.GuardRoot(root);
        if (Directory.Exists(root)) throw new IOException("Fresh fixture root required");
        var work = Directory.CreateDirectory(Path.Combine(root, "reviewer")).FullName;
        const string canary = "PUBLIC_JOB_SCOPE_CANARY";
        File.WriteAllText(Path.Combine(root, "outside-read.txt"), canary);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { WorkingDirectory = work,
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--review-acp-fixture");
        start.ArgumentList.Add("os_scope");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var reviewer = await ReviewerCopilotAcp.OpenAsync(start, deadline.Token);
        var result = await reviewer.ReviewAsync("PUBLIC_SCOPE_CHECK");
        if (result.Status != "OK" || result.Answer != canary ||
            File.ReadAllText(Path.Combine(root, "outside-write.txt")) != "PUBLIC_JOB_SCOPE_WRITE" ||
            File.ReadAllText(Path.Combine(work, "breakaway-error.txt")) != "5")
            throw new Exception("Unexpected sealed Job scope");
        Console.WriteLine(JsonSerializer.Serialize(new { breakaway_win32 = 5, in_process_read = true,
            in_process_write = true, model_requests = 0, fixture_only = true }));
    }

    private static string ProbeJobScope()
    {
        var root = Path.GetDirectoryName(Environment.CurrentDirectory)!;
        ReviewQualification.GuardRoot(root);
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        // Attempt breakaway with a suspended image; terminate without running it if admitted.
        if (CreateProcessW(Environment.ProcessPath!, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                false, 0x01000004, IntPtr.Zero, null, ref startup, out var child)) {
            try { TerminateProcess(child.Process, 1); }
            finally { CloseHandle(child.Thread); CloseHandle(child.Process); }
            throw new Exception("Breakaway unexpectedly admitted");
        }
        File.WriteAllText("breakaway-error.txt", Marshal.GetLastWin32Error().ToString());
        using var output = new FileStream(Path.Combine(root, "outside-write.txt"), FileMode.CreateNew, FileAccess.Write);
        output.Write("PUBLIC_JOB_SCOPE_WRITE"u8);
        return File.ReadAllText(Path.Combine(root, "outside-read.txt"));
    }
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
                if (failure is "sealed" or "os_scope") {
                    try {
                        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--review-acp-linger")
                            { UseShellExecute = false });
                        throw new Exception("Child unexpectedly started in sealed Job");
                    } catch (System.ComponentModel.Win32Exception error) {
                        File.WriteAllText("spawn-error.txt", error.NativeErrorCode.ToString());
                    }
                    var text = failure == "os_scope" ? ProbeJobScope() :
                        frame.RootElement.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString();
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
