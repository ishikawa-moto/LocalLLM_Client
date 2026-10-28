using System.Text.Json;
using LocalBrain.ClientHost.AgentV3;

static JsonElement Event(string json) => JsonDocument.Parse(json).RootElement.Clone();
static void Check(bool value, string message) { if (!value) throw new Exception(message); }

var ok=new PiRunOutcome();
ok.Observe(Event("{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\"stop\"}}"));
ok.Observe(Event("{\"type\":\"agent_settled\"}"));
Check(ok.Settled && ok.CompletedWithoutModelError,"successful settled response rejected");
foreach(var reason in new[]{"error","aborted","length"}) {
    var failed=new PiRunOutcome();
    failed.Observe(Event("{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\""+reason+"\"}}"));
    failed.Observe(Event("{\"type\":\"agent_settled\"}"));
    Check(!failed.CompletedWithoutModelError,"failed model response accepted");
}
var missing=new PiRunOutcome(); missing.Observe(Event("{\"type\":\"agent_settled\"}"));
Check(!missing.CompletedWithoutModelError,"missing assistant response accepted");
var unsettled=new PiRunOutcome();
unsettled.Observe(Event("{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\"stop\"}}"));
Check(!unsettled.CompletedWithoutModelError,"unsettled response accepted");
var toolFailure=new PiRunOutcome();
toolFailure.Observe(Event("{\"type\":\"tool_execution_end\",\"isError\":true}"));
Check(toolFailure.ToolErrorObserved,"tool failure evidence lost");
Console.WriteLine("Pi outcome evidence fixtures passed");
