using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LocalBrain.ClientHost;

internal sealed class WorkLoopBlockedException(string reason) : Exception(reason);

/// <summary>Request-local supervision of all chat histories crossing this Bridge.
/// No tools, approvals, file changes or external reviewers are executed here.</summary>
internal sealed class WorkLoopSupervisor : IAsyncDisposable
{
    internal TimeSpan IdleLimit { get; init; } = TimeSpan.FromMinutes(10);
    internal TimeSpan RequestLimit { get; init; } = TimeSpan.FromMinutes(30);
    private long requests, active, inspected, blocked;
    internal object Status => new { enabled=true,scope="all HTTP requests through this Bridge; chat tool histories before context compaction",
        requests=Interlocked.Read(ref requests),active_requests=Interlocked.Read(ref active),chat_histories=Interlocked.Read(ref inspected),
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

    internal Task CheckAsync(byte[] original,string requestId,Audit audit,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var observation=Inspect(original);Interlocked.Increment(ref inspected);
        audit.Write("work_loop_observed",new{requestId,observation.Tools,observation.Errors,observation.RepeatedResult,observation.RepeatedError,source="original_history_before_compaction"});
        if(observation.BlockReason is not null)Block(observation.BlockReason,requestId,audit);
        return Task.CompletedTask;
    }
    private void Block(string reason,string requestId,Audit audit){Interlocked.Increment(ref blocked);audit.Write("work_loop_blocked",new{requestId,reason,forwarded=false,automatic_retry=false});throw new WorkLoopBlockedException(reason);}
    private static string Hash(string s)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    private static string Canonical(JsonNode? node)=>node switch{
        JsonObject obj=>"{"+string.Join(',',obj.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>System.Text.Json.JsonSerializer.Serialize(p.Key)+":"+Canonical(p.Value)))+"}",
        JsonArray arr=>"["+string.Join(',',arr.Select(Canonical))+"]",_=>node?.ToJsonString()??"null"};
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
