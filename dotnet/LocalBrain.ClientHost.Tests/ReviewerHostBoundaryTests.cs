using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.AgentV2;

internal static class ReviewerHostBoundaryTests
{
    internal static async Task RunAsync(string directory)
    {
        ReviewQualification.GuardRoot(directory);
        var root = Path.GetFullPath(directory);
        var repo = Path.Combine(root, "repo");
        var control = Path.Combine(root, "control");
        if (Directory.Exists(control)) throw new InvalidOperationException("Use a fresh fixture control root");
        var canary = Path.Combine(root, "outside-canary.txt");
        var blocked = Path.Combine(repo, "blocked-write.txt");
        var marker = Path.Combine(repo, "shell-marker.txt");
        if (File.Exists(blocked) || File.Exists(marker)) throw new InvalidOperationException("Fresh targets required");
        var before = CanonicalStore.Hash(File.ReadAllBytes(canary));
        var head = ProjectAllowlist.CurrentHead(repo);
        var task = AgentTaskRequest.Parse("""
            {"requirement":"Public fixture for negative Host relay requests","acceptance_criteria":["No mutation"],
             "risk":"LOW","approved_high_risk":false,"allowed_files":["allowed.txt"],"required_tests":[{"kind":"git_diff_check"}]}
            """);
        using var store = new CanonicalStore(control);
        using var journal = new HostTaskJournal(store, repo, Guid.NewGuid().ToString("N"), task, head);
        await using var gate = new SideEffectGate(repo, task.AllowedFiles, head, true, journal);
        var command = new TaskCommandRequest("denied_shell", "windows", "cmd.exe",
            ["/d", "/c", "echo PUBLIC>shell-marker.txt"]);
        var cases = new[] {
            new SideEffectGate.Request(gate.PipeName, gate.Nonce, "read", Path: canary),
            new SideEffectGate.Request(gate.PipeName, gate.Nonce, "write", Path: "blocked-write.txt", Content: "PUBLIC\n"),
            new SideEffectGate.Request(gate.PipeName, gate.Nonce, "command_start", Command: command)
        };
        var mismatched = (gate.Nonce[0] == '0' ? '1' : '0') + gate.Nonce[1..];
        foreach (var request in cases)
        {
            await ExpectDenial(request with { Nonce = mismatched }, "capability", "UnauthorizedAccessException");
            await ExpectDenial(request, "operation_or_scope",
                request.Operation == "read" ? "InvalidDataException" : "UnauthorizedAccessException");
        }
        if (File.Exists(blocked) || File.Exists(marker) || CanonicalStore.Hash(File.ReadAllBytes(canary)) != before)
            throw new InvalidOperationException("Dedicated fixture changed");
        var events = store.EventIds(journal.TaskId).Select(id => store.ReadTaskEvent(journal.TaskId, id)).ToArray();
        if (events.Any(e => e.Input.EventType is "side_effect_proposed" or "side_effect_applied" or "command_launch_intent"))
            throw new InvalidOperationException("Denied request reached side effect admission");
        Console.WriteLine(JsonSerializer.Serialize(new { status = "PASS", checks = 6, model_requests = 0,
            task_id = journal.TaskId, canonical_event_ids = events.Select(e => e.EventId),
            scope = "Windows Host named-pipe relay only; no reviewer CLI or OS qualification" }));

        async Task ExpectDenial(SideEffectGate.Request request, string scenario, string expected)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var pipe = new NamedPipeClientStream(".", gate.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(deadline.Token);
            var frame = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, ClientConfig.JsonOptions).Replace("\r", "").Replace("\n", "") + "\n");
            await pipe.WriteAsync(frame, deadline.Token);
            await pipe.FlushAsync(deadline.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync(deadline.Token) ?? throw new IOException("Missing Host denial reply");
            var reply = JsonSerializer.Deserialize<SideEffectGate.Reply>(line, ClientConfig.JsonOptions);
            if (reply is null || reply.Ok || !reply.Status.EndsWith("_" + expected, StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected Host relay result for " + request.Operation);
            // This is an observer record of the real wire response, not a policy decision.
            journal.Record("qualification_host_relay_observed", new {
                request_id = Guid.NewGuid().ToString("N"), request.Operation, scenario,
                target = request.Path ?? request.Command?.OperationId, host_reply = reply.Status
            }, "host_observed");
        }
    }
}
