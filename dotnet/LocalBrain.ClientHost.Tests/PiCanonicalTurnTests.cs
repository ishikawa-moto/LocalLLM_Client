using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;
using LocalBrain.ClientHost.AgentV3;

internal static class PiCanonicalTurnTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Responses(MemoryStream sent, string events, bool beforeResponse = false, CancellationTokenSource? cancel = null) : Stream
    {
        private MemoryStream? input;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (cancel != null) { cancel.Cancel(); token.ThrowIfCancellationRequested(); }
            if (input is null) {
                using var doc = JsonDocument.Parse(sent.ToArray());
                string id = doc.RootElement.GetProperty("id").GetString()!;
                string response = JsonSerializer.Serialize(new { type = "response", id, command = "prompt", success = true, data = new { disposition = "started" } });
                input = new MemoryStream(Encoding.UTF8.GetBytes(beforeResponse ? events + response + "\n" : response + "\n" + events));
            }
            return await input.ReadAsync(buffer, token);
        }
        public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken t) => ReadAsync(b.AsMemory(o,c),t).AsTask();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] b,int o,int c) => throw new NotSupportedException(); public override void Flush() { }
        public override long Seek(long o,SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b,int o,int c) => throw new NotSupportedException();
    }
    public static async Task RunAsync()
    {
        string root = Path.Combine(Environment.CurrentDirectory, "pi-v3", "tests", "fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            string state = Path.Combine(root, "state");
            using (var store = new CanonicalStore(state)) {
                store.RegisterWorkspace(new("repo", "ws", Path.Combine(root,"work"), Path.Combine(root,"work"), "main", "base"));
                store.CreateTask("task", "ws", CanonicalStore.Hash(Encoding.UTF8.GetBytes("{}")), "{}");
                store.CreateTask("task2", "ws", CanonicalStore.Hash(Encoding.UTF8.GetBytes("{}")), "{}");
                var adapter = new PiCanonicalTurn(store);
                async Task<(PiCanonicalTurn.Result, long)> Run(string events) {
                    using var sent = new MemoryStream();
                    var result = await adapter.RunAsync("task", "ws", new PiRpcTurn(new Responses(sent, events), sent, 131072), "fixture prompt", Guid.NewGuid(), CancellationToken.None);
                    return (result, sent.Length);
                }
                const string stop = "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\"stop\"}}\n";
                const string settled = "{\"type\":\"agent_settled\"}\n";
                var success = await Run(stop + "{\"type\":\"tool_execution_end\",\"isError\":true}\n" + settled);
                Check(success.Item1.CompletedWithoutModelError && success.Item1.ToolErrorObserved, "Settled result and independent tool error");
                Check(!(await Run(settled)).Item1.CompletedWithoutModelError, "Settlement alone is insufficient");
                Check(!(await Run(stop.Replace("stop\"}", "error\"}") + settled)).Item1.CompletedWithoutModelError, "Model error is not success");
                foreach (var reason in new[] { "error", "aborted", "length" })
                    Check(!(await Run(stop.Replace("stop\"}", reason + "\"}") + stop + settled)).Item1.CompletedWithoutModelError, "Earlier model error remains visible");
                foreach (var reason in new[] { "unexpected", "pending", "deferred" })
                    Check(!(await Run(stop.Replace("stop\"}", reason + "\"}") + stop + settled)).Item1.CompletedWithoutModelError, "Unsupported reason remains indeterminate after stop");
                Check(!(await Run(stop + "{\"type\":\"message_start\",\"message\":{\"role\":\"assistant\"}}\n" + settled)).Item1.CompletedWithoutModelError, "New assistant invalidates stop");
                Check((await Run(stop.Replace("stop\"}", "toolUse\"}") + stop + settled)).Item1.CompletedWithoutModelError, "Tool continuation is not model error");
                foreach (string events in new[] { stop, "{\"type\":\"tool_execution_end\"}\n" + settled, string.Concat(Enumerable.Repeat("{\"type\":\"other\"}\n", 257)) + settled }) {
                    try { await Run(events); throw new Exception("Invalid stream accepted"); } catch (InvalidDataException) { } catch (EndOfStreamException) { }
                }
                Check(store.TaskEvidence("task").Any(h => Encoding.UTF8.GetString(store.ReadEvidence(h)) == "{\"type\":\"tool_execution_end\"}"), "Malformed tool observation retained");
                foreach (var pair in new[] { ("missing", "ws"), ("task", "wrong") }) {
                    using var sent = new MemoryStream();
                    try { await adapter.RunAsync(pair.Item1,pair.Item2,new PiRpcTurn(new Responses(sent, settled),sent,131072),"prompt",Guid.NewGuid(),CancellationToken.None); throw new Exception("Invalid task accepted"); }
                    catch (UnauthorizedAccessException) { Check(sent.Length == 0, "Denied before prompt"); }
                }
                using (var sent = new MemoryStream()) {
                    using var cts = new CancellationTokenSource();
                    var attempt = Guid.NewGuid();
                    try { await adapter.RunAsync("task","ws",new PiRpcTurn(new Responses(sent,"",cancel:cts),sent,131072),"prompt",attempt,cts.Token); throw new Exception("Cancellation accepted"); }
                    catch (OperationCanceledException) { Check(sent.Length > 0, "Interruption after prompt"); }
                    using var retry = new MemoryStream();
                    try { await adapter.RunAsync("task","ws",new PiRpcTurn(new Responses(retry,stop+settled),retry,131072),"prompt",attempt,CancellationToken.None); throw new Exception("Unknown attempt retried"); }
                    catch (InvalidOperationException) { Check(retry.Length == 0, "Unknown attempt cannot transmit again"); }
                }
                using (var sent = new MemoryStream()) {
                    try { await adapter.RunAsync("task","ws",new PiRpcTurn(new Responses(sent,stop,beforeResponse:true),sent,131072),"prompt",Guid.NewGuid(),CancellationToken.None); throw new Exception("Early event accepted"); }
                    catch (InvalidDataException) { }
                }
                using (var sent = new MemoryStream()) {
                    var turn = new PiRpcTurn(new Responses(sent,stop+settled),sent,131072);
                    var attempt = Guid.NewGuid();
                    await adapter.RunAsync("task","ws",turn,"prompt",attempt,CancellationToken.None);
                    long length = sent.Length;
                    try { await adapter.RunAsync("task2","ws",turn,"prompt",Guid.NewGuid(),CancellationToken.None); throw new Exception("Instance reused"); }
                    catch (InvalidOperationException) { Check(sent.Length == length, "Cross-task reuse cannot transmit"); }
                    using var retry = new MemoryStream();
                    const string differentPrompt = "different duplicate prompt evidence";
                    string duplicateHash = CanonicalStore.Hash(Encoding.UTF8.GetBytes(differentPrompt));
                    Check(!File.Exists(Path.Combine(state,"evidence",duplicateHash+".blob")), "Duplicate fixture evidence absent before retry");
                    try { await adapter.RunAsync("task","ws",new PiRpcTurn(new Responses(retry,stop+settled),retry,131072),differentPrompt,attempt,CancellationToken.None); throw new Exception("Attempt reused"); }
                    catch (InvalidOperationException) { Check(retry.Length == 0, "Duplicate attempt cannot transmit"); }
                    Check(!File.Exists(Path.Combine(state,"evidence",duplicateHash+".blob")), "Duplicate attempt writes no different prompt evidence");
                }
                Check(store.GetTask("task")!.StateJson == "{}", "Task state unchanged");
                using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(state,"canonical.db") + ";Mode=ReadOnly;Pooling=False");
                db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM events WHERE task_id='task' AND workspace_id='ws' AND event_type='pi_v3_turn_unknown' AND caused_by_event_id IS NOT NULL";
                Check(Convert.ToInt32(cmd.ExecuteScalar()) >= 4, "Interrupted and malformed runs persist scoped unknown events");
            }
            using (var store = new CanonicalStore(state)) {
                Check(store.EventIds("task").Length >= 12, "Events persisted");
                Check(store.TaskEvidence("task").Any(h => Encoding.UTF8.GetString(store.ReadEvidence(h)) == "fixture prompt"), "Prompt persisted");
                Check(store.TaskEvidence("task").Any(h => Encoding.UTF8.GetString(store.ReadEvidence(h)).Contains("agent_settled")), "Observation persisted");
            }
            Console.WriteLine("Pi canonical adapter fixture passed.");
        } finally {
            foreach (string file in Directory.GetFiles(root,"*",SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root,true);
        }
    }
}
