using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class FailedResumeTests
{
    public static async Task RunAsync(string? destination = null)
    {
        var parent = Path.GetFullPath(destination ?? Path.Combine(Path.GetTempPath(), "lb-failed-resume-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(parent);
        var results = new List<object>();
        var request = AgentTaskRequest.Parse("""
            {"requirement":"Create greeting.txt","acceptance_criteria":["Scoped file only"],"risk":"LOW","approved_high_risk":false,"allowed_files":["greeting.txt"],"required_tests":[{"kind":"git_diff_check"}]}
            """);
        var read = typeof(AgentTaskRunner).GetMethod("ReadResumeCheckpoint", BindingFlags.NonPublic | BindingFlags.Static)!;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); results.Add(new { name, passed = true }); }
        void Reject(Func<object?> action, string name)
        {
            try { action(); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException or InvalidDataException)
            { results.Add(new { name, passed = true }); return; }
            throw new Exception(name);
        }
        foreach (var turns in new[] { 0, 1 })
        {
            var root = Path.Combine(parent, "turns" + turns, "repo"); Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), ".localbrain/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "greeting.txt"), "");
            await Git(root, "init"); await Git(root, "add", ".gitignore", "greeting.txt");
            await Git(root, "-c", "user.name=Test", "-c", "user.email=test@localhost", "commit", "-m", "fixture");
            var initial = await GitEvidence.CaptureAsync(root, request.AllowedFiles, default);
            var control = Path.Combine(parent, "turns" + turns, "control");
            string taskId, sessionId, approvalId;
            using (var store = new CanonicalStore(control))
            {
                taskId = Guid.NewGuid().ToString("N"); sessionId = Guid.NewGuid().ToString();
                using var journal = new HostTaskJournal(store, root, taskId, request, initial.Head);
                journal.Checkpoint(new { task_id = taskId, phase = "failed", pi_session_id = sessionId,
                    git_head = initial.Head, request_hash = AgentTaskRunner.RequestHash(request), actor_turns = turns,
                    changed_files = initial.ChangedFiles, review_diff_sha256 = CanonicalStore.Hash(Encoding.UTF8.GetBytes(initial.ReviewDiff)),
                    recovery_count = 0, replans = 2, tests_without_improvement = 1, false_verified = true, last_error = "TaskCanceledException" });
                approvalId = CanonicalStore.Hash(Encoding.UTF8.GetBytes("fixture approval " + turns));
                store.BindApproval(approvalId, taskId, AgentTaskRunner.RequestHash(request), initial.Head, DateTimeOffset.UtcNow.AddMinutes(5));
            }
            using var reopened = new CanonicalStore(control);
            var saved = reopened.GetTask(taskId)!;
            object? Read(string hash, GitEvidence.Snapshot snapshot, bool recover = false) =>
                read.Invoke(null, [root, hash, snapshot, recover, reopened]);
            var resumed = JsonSerializer.SerializeToElement(Read(AgentTaskRunner.RequestHash(request), initial));
            Check(resumed.GetProperty("TaskId").GetString() == taskId && resumed.GetProperty("SessionId").GetString() == sessionId &&
                resumed.GetProperty("Head").GetString() == initial.Head && resumed.GetProperty("ActorTurns").GetInt32() == turns &&
                resumed.GetProperty("RecoveryCount").GetInt32() == 0 && resumed.GetProperty("FalseVerified").GetBoolean() &&
                resumed.GetProperty("Replans").GetInt32() == 2 && resumed.GetProperty("TestsWithoutImprovement").GetInt32() == 1,
                "canonical failed empty diff preserves identity and counters turns" + turns);
            Check(reopened.GetTask(taskId)!.StateJson == saved.StateJson && reopened.HasApproval(taskId, AgentTaskRunner.RequestHash(request), initial.Head),
                "resume read preserves durable state and approval turns" + turns);
            Reject(() => Read("different request", initial), "different request refused turns" + turns);
            Reject(() => Read(AgentTaskRunner.RequestHash(request), initial with { Head = "different HEAD" }), "HEAD drift refused turns" + turns);
            Reject(() => Read(AgentTaskRunner.RequestHash(request), initial, true), "extra recovery budget unchanged turns" + turns);
            await File.WriteAllTextAsync(Path.Combine(root, "greeting.txt"), "drift\n");
            var drift = await GitEvidence.CaptureAsync(root, request.AllowedFiles, default);
            Reject(() => Read(AgentTaskRunner.RequestHash(request), drift), "actual source drift refused turns" + turns);
            await File.WriteAllTextAsync(Path.Combine(root, "greeting.txt"), "");
            Directory.CreateDirectory(Path.Combine(root, ".localbrain"));
            await File.WriteAllTextAsync(Path.Combine(root, ".localbrain", "task-state.json"), saved.StateJson);
            using var legacy = new CanonicalStore(Path.Combine(parent, "turns" + turns, "legacy-control"));
            Reject(() => read.Invoke(null, [root, AgentTaskRunner.RequestHash(request), initial, false, legacy]), "legacy empty failed remains refused turns" + turns);
        }
        var output = JsonSerializer.Serialize(new { status = "PASS_CANONICAL_FAILED_NO_DIFF_RESUME", checks = results, real_inference = false });
        await File.WriteAllTextAsync(Path.Combine(parent, "result.json"), output);
        Console.WriteLine(output);
    }
    private static async Task Git(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await stdout; await stderr;
        if (process.ExitCode != 0) throw new Exception("Git fixture failed");
    }
}
