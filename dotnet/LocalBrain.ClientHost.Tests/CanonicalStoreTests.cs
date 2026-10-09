using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;
using Microsoft.Data.Sqlite;

internal static class CanonicalStoreTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string message) {
        try { action(); } catch (Exception e) when (e is InvalidDataException or SqliteException or IOException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException(message);
    }
    private static CanonicalStore.EventInput Input(string key, string[]? refs = null, string task = "task_a",
        string ws = "ws_main", string? cause = null) => new(task, ws, "fixture", key,
        "fixture_event", "model_claim", "unverified", refs ?? [],
        JsonSerializer.SerializeToElement(new { claim = "model says PASS" }), CausedByEventId: cause);
    private static void Setup(CanonicalStore store, string parent) {
        store.RegisterWorkspace(new("repo_a", "ws_main", Path.Combine(parent, "main"),
            Path.Combine(parent, "main"), "main", "base"));
        store.RegisterWorkspace(new("repo_a", "ws_worker", Path.Combine(parent, "main"),
            Path.Combine(parent, "worker"), "remote_worker", "base"));
        store.RegisterWorkspace(new("repo_b", "ws_other", Path.Combine(parent, "other"),
            Path.Combine(parent, "other"), "main", "base"));
        var request = "{}"; var hash = CanonicalStore.Hash(Encoding.UTF8.GetBytes(request));
        store.CreateTask("task_a", "ws_main", hash, request);
        store.CreateTask("task_b", "ws_main", hash, request);
    }

    internal static void CrashFixture(string root, string boundary) {
        using var store = new CanonicalStore(Path.Combine(root, "state"),
            point => { if (point == boundary) Environment.FailFast("intentional canonical crash boundary"); });
        Setup(store, root);
        var blob = store.PutEvidence(Encoding.UTF8.GetBytes("crash evidence"));
        store.Checkpoint(Input("crash", [blob]), "{\"phase\":\"committed\"}", 3, 5, 1, 2);
        throw new InvalidOperationException("Crash boundary was not reached");
    }

    public static async Task RunAsync() {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Host tests require Windows");
        var root = Path.Combine(Path.GetTempPath(), "localbrain-canonical-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var stateRoot = Path.Combine(root, "state");
        CanonicalStore.EventReceipt committed;
        string blob;
        using (var store = new CanonicalStore(stateRoot)) {
            Setup(store, root);
            Reject(() => new CanonicalStore(stateRoot).Dispose(), "Only one logical writer lease is allowed");
            blob = store.PutEvidence(Encoding.UTF8.GetBytes("immutable original evidence"));
            Check(store.PutEvidence(Encoding.UTF8.GetBytes("immutable original evidence")) == blob, "Content addressed reuse");
            Check(Encoding.UTF8.GetString(store.ReadEvidence(blob)) == "immutable original evidence", "Exact immutable bytes");
            committed = store.Checkpoint(Input("once", [blob]), "{\"phase\":\"validated\"}", 2, 7, 1, 3);
            var duplicate = store.Checkpoint(Input("once", [blob]), "{\"phase\":\"validated\"}", 2, 7, 1, 3);
            Check(duplicate.Duplicate && duplicate.EventId == committed.EventId && duplicate.Sequence == 1, "Exactly-once replay");
            var snapshot = store.GetTask("task_a")!;
            Check(snapshot.ActorTurns == 2 && snapshot.ToolCalls == 7 && snapshot.Handoffs == 1 && snapshot.ExternalReviews == 3,
                "Duplicate replay cannot repeat task counters");
            Reject(() => store.Checkpoint(Input("once", [blob]), "{\"phase\":\"different\"}", 2), "Conflicting payload must fail");
            Reject(() => store.Append(Input("unknown_task", task:"unknown")), "Unknown task must fail");
            Reject(() => store.Append(Input("unknown_ws", ws:"unknown")), "Unknown workspace must fail");
            Reject(() => store.Append(Input("unrelated", ws:"ws_other")), "Other repository workspace must fail");
            Reject(() => store.Append(Input("bad_cause", task:"task_b", cause:committed.EventId)), "Cross-task causality must fail");
            Reject(() => store.Append(Input("missing", [new string('a',64)])), "Missing bytes must fail");
            Reject(() => store.Checkpoint(Input("negative"), "{}", -1), "Task counters cannot reset");
            store.RegisterWorker("ServerPC", ["candidate_patch"]);
            var startHash = CanonicalStore.Hash(Encoding.UTF8.GetBytes("starting state"));
            var spec = new CanonicalStore.TaskSpec("task_b", "assign_a", "ServerPC", "ws_worker", "repo_a",
                "base", startHash, startHash, ["allowed.cs"], ["reference.cs"], "candidate work", ["criteria"],
                [new("git_diff_check", null)], [], 1);
            var assignment = store.ReserveAssignment(spec);
            var replacement = store.ReserveAssignment(spec with { AssignmentId = "assign_b", Attempt = 2 });
            var result = new CanonicalStore.WorkerResult("task_b", "assign_a", "ServerPC", assignment.Generation,
                "base", startHash, startHash, startHash, null, ["allowed.cs"], [], "worker claims tests PASS");
            Reject(() => store.CollectResult(result), "Late result from a reassigned generation must fail");
            var currentResult = result with { AssignmentId = "assign_b", Generation = replacement.Generation };
            Reject(() => store.CollectResult(currentResult with { ChangedFiles = ["forbidden.cs"] }),
                "Worker writes outside TaskSpec must fail");
            var collected = store.CollectResult(currentResult);
            Check(store.CollectResult(currentResult).Duplicate, "Worker result collection is idempotent");
            Reject(() => store.CollectResult(currentResult with { Summary = "changed claim" }), "Worker result is immutable");
            Reject(() => store.ReserveAssignment(spec with { AssignmentId="assign_main", WorkspaceId="ws_main" }),
                "Worker cannot reserve the authoritative main workspace");
            Check(store.Append(Input("worker_event", ws:"ws_worker", cause:committed.EventId)).Sequence == 2,
                "Same repository workspace and cause accepted with distinct identity");
            await Task.WhenAll(Enumerable.Range(0,24).Select(i => Task.Run(() => store.Append(Input("parallel_"+i)))));
            Check(store.EventIds("task_a").Length == 26, "Concurrent producers serialize without sequence loss");

            var path = Path.Combine(stateRoot, "evidence", blob + ".blob");
            File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllText(path, "tampered");
            Reject(() => store.Append(Input("corrupt", [blob])), "Corrupt bytes cannot be committed");
            Reject(() => store.Checkpoint(Input("once", [blob]), "{\"phase\":\"validated\"}",2,7,1,3),
                "Duplicate receipt must not hide evidence corruption");
            File.WriteAllText(path,"immutable original evidence"); File.SetAttributes(path, FileAttributes.ReadOnly);

            Check(store.ReadPolicy("repo_a",Path.Combine(root,"main")).Mode == "deny", "Missing policy denies");
            store.ChangePolicies([new("repo_a",Path.Combine(root,"main"),"auto",["write.cs"],["read.cs"])],"host-test");
            var accepted = store.ReadPolicy("repo_a",Path.Combine(root,"main"));
            Check(accepted.Mode == "auto" && accepted.AuditMatched, "Audited policy activates");
            Check(store.ReadPolicy("repo_a",Path.Combine(root,"other")).Mode == "deny", "Identity AND root policy binding");
            var policyPath = Path.Combine(stateRoot,"codex-egress-policies.json");
            File.AppendAllText(policyPath," ");
            Check(store.ReadPolicy("repo_a",Path.Combine(root,"main")).Mode == "deny", "Unaudited file edit fail-closed");
            store.ChangePolicies([new("repo_a",Path.Combine(root,"main"),"manual",["write.cs"],["read.cs"])],"host-test");
            var journalTask = Guid.NewGuid().ToString("N");
            var request = new AgentTaskRequest("create fixture", ["fixture acceptance"], "LOW", false,
                ["fixture.txt"], [new("git_diff_check", null)]);
            using (var journal = new HostTaskJournal(store, Path.Combine(root,"main"), journalTask, request,"base"))
                journal.Checkpoint(new { phase="needs_work", actor_turns=2, tool_calls=7 });
            using (var journal = new HostTaskJournal(store, Path.Combine(root,"main"), journalTask, request,"base"))
            {
                journal.Checkpoint(new {phase="running",actor_turns=2});
                Check(store.GetTask(journalTask)!.ToolCalls==7,"New invocation start duplicated prior tool calls");
                journal.Checkpoint(new {phase="running",actor_turns=3,tool_calls=0});
                journal.Checkpoint(new { phase="needs_work", actor_turns=3, tool_calls=3 });
                Reject(() => journal.Checkpoint(new { phase="needs_work", actor_turns=1, tool_calls=0 }),
                    "Resume cannot reset task counters");
            }
            Check(store.GetTask(journalTask)!.ToolCalls == 10 && store.GetTask(journalTask)!.ActorTurns == 3,
                "Runner journal preserves totals across separate invocations");
            var failedTask = Guid.NewGuid().ToString("N");
            using (var journal = new HostTaskJournal(store, Path.Combine(root,"main"), failedTask, request,"base"))
            {
                var toolStart = JsonSerializer.SerializeToElement(new { type="tool_execution_start",toolCallId="tool_a",toolName="read" });
                journal.RecordToolEvent(toolStart);
                journal.RecordToolEvent(toolStart);
                journal.ReviewStarted("fixture_reviewer");
                journal.Checkpoint(new { phase="failed", actor_turns=1 });
                journal.RecordPrompt("host instructions only", true);
            }
            var failed = store.GetTask(failedTask)!;
            Check(failed.ToolCalls == 1 && failed.ExternalReviews == 1,
                "Tool/review counts survive failure before Actor results; duplicate start is exactly once");
            Check(CanonicalStore.ReadLatest(stateRoot, HostTaskJournal.WorkspaceId(Path.Combine(root,"main")))!.ToolCalls == 1,
                "Read-only canonical status works while writer is active");
        }
        using (var store = new CanonicalStore(stateRoot)) {
            Check(store.EventIds("task_a")[0] == committed.EventId, "Host restart preserves committed event");
            Check(store.GetTask("task_a")!.ToolCalls == 7, "Host restart preserves cumulative counters");
            Check(store.ReadEvidence(blob).Length > 0, "Host restart retains evidence");
            Check(store.ReadPolicy("repo_a",Path.Combine(root,"main")).Mode == "manual", "Audited policy survives restart");
        }
        using (var connection = new SqliteConnection("Data Source="+Path.Combine(stateRoot,"canonical.db")+";Pooling=False")) {
            connection.Open();
            foreach (var sql in new[] {"UPDATE events SET event_type='false';","DELETE FROM events;","DELETE FROM evidence;","DELETE FROM egress_policy_audit;"}) {
                using var cmd = connection.CreateCommand(); cmd.CommandText=sql;
                Reject(() => cmd.ExecuteNonQuery(), "Append-only schema prohibits mutation");
            }
            using var health = connection.CreateCommand(); health.CommandText="PRAGMA integrity_check;";
            Check((string?)health.ExecuteScalar()=="ok","SQLite integrity_check");
        }

        foreach (var boundary in new[] {"evidence_flushed","evidence_renamed","event_before_commit","event_after_commit"}) {
            var childRoot=Path.Combine(root,boundary);
            var start=new ProcessStartInfo("dotnet") {RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
            start.ArgumentList.Add(typeof(CanonicalStoreTests).Assembly.Location);
            start.ArgumentList.Add("--canonical-crash"); start.ArgumentList.Add(childRoot); start.ArgumentList.Add(boundary);
            using var process=Process.Start(start)!;
            var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await stdout; await stderr;
            Check(process.ExitCode != 0, "Fixture must terminate at crash boundary");
            using var recovered=new CanonicalStore(Path.Combine(childRoot,"state"));
            var events=recovered.EventIds("task_a");
            var state=recovered.GetTask("task_a")!;
            if (boundary=="event_after_commit") {
                Check(events.Length==1 && state.ActorTurns==3 && state.ToolCalls==5 &&
                    state.Handoffs==1 && state.ExternalReviews==2 && state.StateJson.Contains("committed"),
                    "After-COMMIT kill must recover the entire transaction");
                var hash=CanonicalStore.Hash(Encoding.UTF8.GetBytes("crash evidence"));
                Check(recovered.ReadEvidence(hash).Length>0,"Committed event cannot lack bytes");
                Check(recovered.Checkpoint(Input("crash",[hash]),"{\"phase\":\"committed\"}",3,5,1,2).Duplicate,
                    "Replay after post-COMMIT kill is idempotent");
            } else {
                Check(events.Length==0 && state.ActorTurns==0 && state.ToolCalls==0 && state.StateJson=="{}",
                    "Pre-COMMIT kill must recover the complete previous state");
            }
            Console.WriteLine("Crash boundary "+boundary+": PASS");
        }
        Console.WriteLine("Canonical state, evidence, writer concurrency, replay, policy and process-kill checks passed.");
    }
}
