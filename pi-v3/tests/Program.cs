using System.Text;
using LocalBrain.ClientHost.AgentV3;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Reject(byte[] bytes, int max=1024) {
    using var stream = new MemoryStream(bytes);
    var reader = new PiJsonlReader(stream,max);
    try { await reader.ReadAsync(CancellationToken.None); }
    catch (InvalidDataException) { return; }
    throw new Exception("invalid record was accepted");
}
var raw=Encoding.UTF8.GetBytes("{\"type\":\"event\",\"text\":\"x\u2028y\"}\r\n{\"id\":\"second\"}\n");
using(var stream=new MemoryStream(raw)) {
    var reader=new PiJsonlReader(stream,1024);
    var first=await reader.ReadAsync(CancellationToken.None);
    var second=await reader.ReadAsync(CancellationToken.None);
    Check(first?.GetProperty("text").GetString()=="x\u2028y", "Unicode line separator split a record");
    Check(second?.GetProperty("id").GetString()=="second", "second record lost");
    Check(await reader.ReadAsync(CancellationToken.None) is null,"EOF is not clean");
}
await Reject(Encoding.UTF8.GetBytes("{bad}\n"));
await Reject(Encoding.UTF8.GetBytes("[]\n"));
await Reject(Encoding.UTF8.GetBytes("{\"a\":1}"));
await Reject(new byte[]{0xff,0x0a});
await Reject(Encoding.UTF8.GetBytes("{\"too\":\"long\"}\n"),8);
var events=Encoding.UTF8.GetBytes("{\"id\":\"test-1\",\"type\":\"response\",\"command\":\"prompt\",\"success\":true,\"data\":{\"disposition\":\"started\"}}\n{\"type\":\"agent_end\"}\n{\"type\":\"agent_settled\"}\n");
using(var input=new MemoryStream(events)) using(var output=new MemoryStream()) {
    var turn=new PiRpcTurn(input,output,4096);var observed=new List<string>();
    await turn.RunPromptAsync("test-1","do the task", (e,_)=>{observed.Add(e.GetProperty("type").GetString()!);return Task.CompletedTask;}, CancellationToken.None);
    Check(observed.SequenceEqual(new[]{"agent_end","agent_settled"}),"completion did not wait for agent_settled");
    using var sent=System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString(output.ToArray()).TrimEnd('\n'));
    Check(sent.RootElement.GetProperty("id").GetString()=="test-1" && sent.RootElement.GetProperty("message").GetString()=="do the task","prompt framing incorrect");
}
using(var input=new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":\"wrong\",\"type\":\"response\",\"command\":\"prompt\",\"success\":true}\n"))) using(var output=new MemoryStream()) {
    var turn=new PiRpcTurn(input,output,4096);
    try { await turn.RunPromptAsync("test-1","task",(_,_)=>Task.CompletedTask,CancellationToken.None); throw new Exception("mismatched response accepted"); }
    catch(InvalidDataException) { }
    try { await turn.RunPromptAsync("test-2","task",(_,_)=>Task.CompletedTask,CancellationToken.None); throw new Exception("failed stream reused"); }
    catch(InvalidOperationException) { }
}
using(var input=new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":\"test-1\",\"type\":\"response\",\"command\":\"prompt\",\"success\":true,\"data\":{\"disposition\":\"started\"}}\n{\"type\":\"agent_end\"}\n"))) using(var output=new MemoryStream()) {
    var turn=new PiRpcTurn(input,output,4096);
    try { await turn.RunPromptAsync("test-1","task",(_,_)=>Task.CompletedTask,CancellationToken.None); throw new Exception("incomplete turn accepted"); }
    catch(EndOfStreamException) { }
}
using(var input=new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":\"test-1\",\"type\":\"response\",\"command\":\"prompt\",\"success\":true,\"data\":{\"disposition\":\"handled\"}}\n"))) using(var output=new MemoryStream()) {
    var turn=new PiRpcTurn(input,output,4096);
    try { await turn.RunPromptAsync("test-1","task",(_,_)=>Task.CompletedTask,CancellationToken.None); throw new Exception("handled prompt accepted as a run"); }
    catch(InvalidDataException) { }
}
using(var input=new MemoryStream(events)) using(var output=new MemoryStream()) using(var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(50))) {
    var turn=new PiRpcTurn(input,output,4096);
    try { await turn.RunPromptAsync("test-1","task",(_,_)=>new TaskCompletionSource().Task,cancel.Token); throw new Exception("observer cancellation was ignored"); }
    catch(OperationCanceledException) { }
}
Console.WriteLine("Pi RPC framing fixtures passed");
