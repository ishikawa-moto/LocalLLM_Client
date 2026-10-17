using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LocalBrain.ClientHost.AgentV2;

namespace LocalBrain.ClientHost;

internal sealed class WorkLoopBlockedException(string reason) : Exception(reason);

/// <summary>Request-local supervision of all chat histories crossing this Bridge.
/// No tools, approvals, file changes or external reviewers are executed here.</summary>
internal sealed class WorkLoopSupervisor : IAsyncDisposable
{
    private readonly LayaSupervisor laya = new();
    private readonly Func<SupervisorState,CancellationToken,Task<SupervisorDecision>>? predict;
    internal TimeSpan IdleLimit { get; init; } = TimeSpan.FromMinutes(10);
    internal TimeSpan RequestLimit { get; init; } = TimeSpan.FromMinutes(30);
    private long requests, active, inspected, predictions, blocked;
    internal WorkLoopSupervisor(Func<SupervisorState,CancellationToken,Task<SupervisorDecision>>? predict = null) => this.predict=predict;
    internal object Status => new { enabled=true,scope="all HTTP requests through this Bridge; chat tool histories before context compaction",
        requests=Interlocked.Read(ref requests),active_requests=Interlocked.Read(ref active),chat_histories=Interlocked.Read(ref inspected),laya_checks=Interlocked.Read(ref predictions),
        blocked=Interlocked.Read(ref blocked),idle_seconds=IdleLimit.TotalSeconds,request_seconds=RequestLimit.TotalSeconds,
        external_continue_tools="results observed on next model request; external running process not owned or cancelled",
        agent_v2="existing Pi milestone supervisor and task budgets retained",automatic_replay=false };
    internal void RequestStarted(){Interlocked.Increment(ref requests);Interlocked.Increment(ref active);}
    internal void RequestFinished()=>Interlocked.Decrement(ref active);
    internal sealed record Observation(int Tools,int Errors,int RepeatedResult,int RepeatedError,string? BlockReason);

    internal static Observation Inspect(byte[] bytes)
    {
        var root=JsonNode.Parse(bytes)?.AsObject() ?? throw new InvalidDataException("Chat JSON object required");
        if(root["messages"] is not JsonArray messages)return new(0,0,0,0,null);
        // Explicit new user turns start a new work segment, including an intentional retry.
        var start=0;for(var i=0;i<messages.Count;i++)if(messages[i]?["role"]?.GetValue<string>()=="user")start=i+1;
        var calls=new Dictionary<string,(string Name,string Arguments)>();
        var signatures=new List<string>();var errors=0;var repeatedErrors=0;string? lastError=null;
        for(var i=start;i<messages.Count;i++){
            if(messages[i] is not JsonObject message)continue;
            if(message["role"]?.GetValue<string>()=="assistant" && message["tool_calls"] is JsonArray tools)
                foreach(var item in tools.OfType<JsonObject>()){
                    var id=item["id"]?.GetValue<string>();var f=item["function"];
                    if(id is not null && f?["name"] is JsonValue name){
                        var arguments=f["arguments"]?.GetValue<string>()??"";
                        try{arguments=Canonical(JsonNode.Parse(arguments));}catch(System.Text.Json.JsonException){}
                        calls[id]=(name.GetValue<string>(),arguments);
                    }
                }
            if(message["role"]?.GetValue<string>()!="tool")continue;
            var callId=message["tool_call_id"]?.GetValue<string>();
            if(callId is null || !calls.Remove(callId,out var call))continue;
            var result=Canonical(message["content"]);
            signatures.Add(Hash(call.Name+"\n"+call.Arguments+"\n"+result));
            var failed=message["isError"]?.ToString()=="true" || message["is_error"]?.ToString()=="true";
            if(message["content"] is JsonValue v && v.TryGetValue<string>(out var text)){
                var trimmed=text.TrimStart();
                failed |= trimmed.StartsWith("Error:",StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("MCP error",StringComparison.OrdinalIgnoreCase)
                    || trimmed.Equals("Not connected",StringComparison.OrdinalIgnoreCase) || trimmed.Equals("Request timed out",StringComparison.OrdinalIgnoreCase);
                try{if(JsonNode.Parse(text) is JsonObject obj)failed |= obj["isError"]?.ToString()=="true" || obj["is_error"]?.ToString()=="true" || obj["error"] is not null;}catch(System.Text.Json.JsonException){}
            }
            if(failed){errors++;var fingerprint=Hash(call.Name+"\n"+result);repeatedErrors=fingerprint==lastError?repeatedErrors+1:1;lastError=fingerprint;}
            else{lastError=null;repeatedErrors=0;}
        }
        var repetitions=0;
        // Also catch A/B and A/B/C cycles, but only if the entire trailing cycle is identical.
        for(var width=1;width<=3 && width<=signatures.Count;width++){
            var repeats=1;
            for(var offset=width*2;offset<=signatures.Count;offset+=width){
                if(!Enumerable.Range(0,width).All(j=>signatures[signatures.Count-width+j]==signatures[signatures.Count-offset+j]))break;
                repeats++;
            }
            repetitions=Math.Max(repetitions,repeats);
        }
        return new(signatures.Count,errors,repetitions,repeatedErrors,repeatedErrors>=3?"repeated_identical_error":repetitions>=4?"repeated_tool_result_cycle":null);
    }

    internal async Task CheckAsync(byte[] original,string requestId,Audit audit,CancellationToken token)
    {
        var observation=Inspect(original);Interlocked.Increment(ref inspected);
        audit.Write("work_loop_observed",new{requestId,observation.Tools,observation.Errors,observation.RepeatedResult,observation.RepeatedError,source="original_history_before_compaction"});
        if(observation.BlockReason is not null)Block(observation.BlockReason,requestId,audit);
        // All histories get deterministic inspection. Laya sees compact numeric milestones,
        // not source text. Avoid loading a CPU model for every ordinary short question.
        if(observation.Tools==0 || (observation.Tools!=1 && observation.Tools%4!=0 && observation.Errors==0))return;
        var state=new SupervisorState("implementation",observation.Tools,0,0,0,false,observation.RepeatedError,
            Math.Max(0,observation.RepeatedResult-1),0,0,observation.RepeatedResult>1?"low":"medium",observation.RepeatedResult<=1,false,false,false,false);
        SupervisorDecision decision;
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(token);budget.CancelAfter(TimeSpan.FromSeconds(45));
        try{
            Interlocked.Increment(ref predictions);
            if(predict is not null)decision=await predict(state,budget.Token);
            else{
                var (action,confidence,latency)=await laya.PredictAsync(state.Compact,budget.Token);
                decision=new(action,"laya",confidence>=0.55?"typed_decision":"low_confidence",confidence,latency,confidence<0.55);
            }
        }catch(Exception e) when(e is not OperationCanceledException || !token.IsCancellationRequested){
            audit.Write("work_loop_laya_unavailable",new{requestId,error=e.GetType().Name,deterministic_monitoring=true});return;
        }
        if(decision.Confidence is {} confidenceValue && !double.IsFinite(confidenceValue))
            decision=decision with{Confidence=null,Fallback=true,Reason="invalid_laya_confidence"};
        audit.Write("work_loop_laya_decision",new{requestId,decision.Action,decision.Source,decision.Reason,decision.Confidence,decision.LatencyMs,decision.Fallback});
        // Never convert a prediction into a tool call, approval, cloud review, compaction
        // or a success claim. Only a confident replan/escalation can pause this request.
        if(!decision.Fallback && decision.Confidence is {} c && double.IsFinite(c) && c>=0.55 && decision.Action is "REPLAN" or "ESCALATE_SOL")
            Block("laya_requested_replan",requestId,audit);
    }
    private void Block(string reason,string requestId,Audit audit){Interlocked.Increment(ref blocked);audit.Write("work_loop_blocked",new{requestId,reason,forwarded=false,automatic_retry=false});throw new WorkLoopBlockedException(reason);}
    private static string Hash(string s)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    private static string Canonical(JsonNode? node)=>node switch{
        JsonObject obj=>"{"+string.Join(',',obj.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>System.Text.Json.JsonSerializer.Serialize(p.Key)+":"+Canonical(p.Value)))+"}",
        JsonArray arr=>"["+string.Join(',',arr.Select(Canonical))+"]",_=>node?.ToJsonString()??"null"};
    public ValueTask DisposeAsync()=>laya.DisposeAsync();
}
