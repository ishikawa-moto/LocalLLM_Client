using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using LocalBrain.ClientHost;

internal static class ContinueContextMemoryTests
{
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static JsonObject Message(string role,string text)=>new(){["role"]=role,["content"]=text};
    static Task<int> Count(JsonObject request,CancellationToken token)=>Task.FromResult(request["messages"]!.AsArray().Sum(m=>m!.ToJsonString().Length));
    static byte[] Bytes(JsonObject value)=>Encoding.UTF8.GetBytes(value.ToJsonString());
    internal static async Task NativeAsync(string root)
    {
        // Explicit disposable control root with an already-qualified native input manifest;
        // production canonical DB, knowledge, services and model settings are never modified.
        if(!File.Exists(Path.Combine(root,"taskmemory-q8","config.json")))throw new Exception("Explicit trial config required");
        var messages=new JsonArray(Message("system","Public context-handoff test. Answer only from the quoted public fixture and current request."),
            Message("user","Investigate the public fixture. Do not call tools or modify files."));
        messages.Add(Message("assistant","Public fixture observation: the version manager record VOLTA_PUBLIC_CONTEXT_73 has acceptance code BLUE_LANTERN_731."));
        for(var i=0;i<40;i++)messages.Add(Message(i%2==0?"user":"assistant",string.Join(' ',Enumerable.Range(0,250).Select(j=>$"public_record_{i}_{j}"))));
        messages.Add(Message("user","What is the acceptance code in the earlier VOLTA_PUBLIC_CONTEXT_73 version manager observation? Reply only with that code."));
        var payload=new JsonObject{["model"]="local-qwen38",["max_tokens"]=512,["stream"]=false,["messages"]=messages,["temperature"]=0};
        using var stop=new CancellationTokenSource(TimeSpan.FromMinutes(18));
        await using var memory=new ContinueContextMemory(root);
        var prepared=await memory.PrepareAsync(Bytes(payload),stop.Token);
        Check(prepared.OriginalTokens>32768 && prepared.ForwardedTokens<=24576,"Native test must actually cross32K and return under physical admission");
        Check(Encoding.UTF8.GetString(prepared.Body).Contains("BLUE_LANTERN_731"),"Native memory must retrieve the old public observation");
        File.WriteAllBytes(Path.Combine(root,"public-prepared-request.json"),prepared.Body);
        var receipt=JsonNode.Parse(File.ReadAllText(Directory.GetFiles(Path.Combine(root,"continue-memory"),"*.receipt.json").Single()))!;
        Check(receipt["native_diagnostics"]?["model_sha256"]?.GetValue<string>()?.Equals("4a673518b11b1c4445f9b9a9d3c40356f5ba39f6a6260dcb25f3fbfd475d9efb",StringComparison.OrdinalIgnoreCase)==true,"Actual native model identity required");
        var proof=new{status="PASS_ACTUAL_QWEN_MEASUREMENT_AND_NATIVE_SALUKI_RECALL",prepared.OriginalTokens,prepared.ForwardedTokens,prepared.SourceHash,
            native=receipt["native_diagnostics"],public_fixture_only=true,pc70_inference_calls=0,knowledge_adoption=0};
        File.WriteAllText(Path.Combine(root,"native-proof.json"),JsonSerializer.Serialize(proof));Console.WriteLine(JsonSerializer.Serialize(proof));
    }
    internal static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"continue-memory-proof-"+Guid.NewGuid().ToString("N"));
        var calls=0;string? selected=null;
        await using var memory=new ContinueContextMemory(root,Count,(task,query,candidates,token)=>{
            calls++;selected=candidates.Single(c=>c.Preview.Contains("VOLTA_ORIGINAL_FACT",StringComparison.Ordinal)).EventId;
            var oldTool=candidates.Single(c=>c.Preview.Contains("ARCHIVED_TOOL_FIELD",StringComparison.Ordinal)).EventId;
            var middleTool=candidates.Single(c=>c.Preview.Contains("ARCHIVED_MID_TOOL",StringComparison.Ordinal)).EventId;
            return Task.FromResult("{\"event_ids\":[\""+selected+"\",\""+oldTool+"\",\""+middleTool+"\"]}");
        });
        var shortRequest=new JsonObject{["model"]="local-qwen38",["max_tokens"]=4096,["stream"]=true,
            ["messages"]=new JsonArray(Message("system","DO_NOT_CHANGE_POLICY"),Message("user","small"))};
        var shortBytes=Bytes(shortRequest);var pass=await memory.PrepareAsync(shortBytes);
        Check(pass.Body.SequenceEqual(shortBytes)&&calls==0&&!Directory.Exists(root),"Short request must pass byte-for-byte without memory or storage");
        var full=(JsonObject)shortRequest.DeepClone();var messages=full["messages"]!.AsArray();messages.RemoveAt(1);
        messages.Add(Message("user","ORIGINAL_TASK_CONSTRAINT: do not install or delete packages"));
        messages.Add(Message("user","VOLTA_ORIGINAL_FACT the configured Node lives under the version manager, not evidence of missing packages"));
        messages.Add(new JsonObject{["role"]="assistant",["content"]="ARCHIVED_TOOL_FIELD historical tools evidence",
            ["tool_calls"]=new JsonArray(new JsonObject{["id"]="old_call",["type"]="function",["function"]=new JsonObject{["name"]="public_old_tool",["arguments"]="{}"}})});
        messages.Add(new JsonObject{["role"]="tool",["tool_call_id"]="old_call",["content"]="historical result"});
        for(var i=0;i<70;i++)messages.Add(Message(i%2==0?"user":"assistant","unrelated history "+i+new string('x',600)));
        var latest=Message("user","Use VOLTA_ORIGINAL_FACT and ARCHIVED_TOOL_FIELD and ARCHIVED_MID_TOOL to explain the version manager and retain the tools");messages.Add(latest);
        messages.Add(new JsonObject{["role"]="assistant",["content"]="ARCHIVED_MID_TOOL historical tools after current user",
            ["tool_calls"]=new JsonArray(new JsonObject{["id"]="mid_call",["type"]="function",["function"]=new JsonObject{["name"]="public_middle_tool",["arguments"]="{}"}})});
        messages.Add(new JsonObject{["role"]="tool",["tool_call_id"]="mid_call",["content"]="middle result"});
        var toolCall=new JsonObject{["role"]="assistant",["content"]=null,["tool_calls"]=new JsonArray(new JsonObject{["id"]="call_current",["type"]="function",["function"]=new JsonObject{["name"]="read_file",["arguments"]="{\"path\":\"public.txt\"}"}})};
        messages.Add(toolCall);var toolResult=new JsonObject{["role"]="tool",["tool_call_id"]="call_current",["content"]="CURRENT_TOOL_RESULT"};messages.Add(toolResult);
        full["tools"]=new JsonArray(new JsonObject{["type"]="function",["function"]=new JsonObject{["name"]="read_file",["description"]="PUBLIC_SCHEMA"}});
        var original=Bytes(full);var prepared=await memory.PrepareAsync(original);var forwarded=JsonNode.Parse(prepared.Body)!.AsObject();
        Check(prepared.OriginalTokens>32768&&prepared.ForwardedTokens<=24576&&calls==1,"Overflow must recall before admission");
        Check(JsonNode.DeepEquals(forwarded["messages"]![0],messages[0]),"System policy must remain exact");
        var fm=forwarded["messages"]!.AsArray();
        Check(JsonNode.DeepEquals(fm[fm.Count-3],latest)&&JsonNode.DeepEquals(fm[fm.Count-2],toolCall)&&JsonNode.DeepEquals(fm[fm.Count-1],toolResult),"Current request and paired tools must remain exact and ordered");
        Check(JsonNode.DeepEquals(fm[2],messages[1]),"Original user task constraints remain exact");
        Check(JsonNode.DeepEquals(forwarded["tools"],full["tools"])&&forwarded["stream"]!.GetValue<bool>()&&forwarded["max_tokens"]!.GetValue<int>()==4096,"Streaming/output/tool definitions preserved");
        Check(forwarded["messages"]![1]!["content"]!.GetValue<string>().Contains("VOLTA_ORIGINAL_FACT"),"Deep old source must be recalled into the fresh request");
        Check(fm[1]!["content"]!.GetValue<string>().Contains("public_old_tool")&&fm.Count(m=>m?["tool_calls"] is JsonArray)==1,"Recalled historical tool_calls are plain source text, not an API tool group to execute or validate");
        Check(fm[1]!["content"]!.GetValue<string>().Contains("public_middle_tool"),"Selected tool history between current user and retained suffix is also inert text");
        Check(File.ReadAllBytes(Path.Combine(root,prepared.SourceHash+".json")).SequenceEqual(original),"Full immutable source must be preserved");
        var repeat=await memory.PrepareAsync(original);
        Check(calls==1&&repeat.SelectionCacheHit&&repeat.Body.SequenceEqual(prepared.Body),"Identical input/profile may reuse validated IDs without repeating slow CPU inference");
        var firstSource=prepared.SourceHash;
        await using var bad=new ContinueContextMemory(root+"-invalid",Count,(_,_,_,_)=>Task.FromResult("{\"event_ids\":[\"invented_other_task\"]}"));
        var rejected=false;try{await bad.PrepareAsync(original);}catch(InvalidDataException){rejected=true;}
        Check(rejected,"Foreign memory IDs cannot manufacture history");
        await using var unavailable=new ContinueContextMemory(root+"-unavailable",Count,(_,_,_,_)=>throw new IOException("fixture unavailable"));
        rejected=false;try{await unavailable.PrepareAsync(original);}catch(IOException){rejected=true;}
        Check(rejected&&Directory.EnumerateFiles(root+"-unavailable","*.json").Count()==1,"Memory failure must retain source and produce no prepared result");
        var enormous=(JsonObject)shortRequest.DeepClone();enormous["messages"]=new JsonArray(Message("system",new string('x',33000)),Message("user","still required"));
        rejected=false;try{await memory.PrepareAsync(Bytes(enormous));}catch(InvalidDataException){rejected=true;}
        Check(rejected,"Pinned instructions must never be truncated to force admission");
        full["max_tokens"]=32768;rejected=false;try{await memory.PrepareAsync(Bytes(full));}catch(InvalidDataException){rejected=true;}
        Check(rejected,"Output cannot expand the physical actor context");
        var orphan=new JsonArray(Message("user","test"),new JsonObject{["role"]="tool",["tool_call_id"]="not_owned",["content"]="bad"});
        rejected=false;try{ContinueContextMemory.ValidateToolPairs(orphan);}catch(InvalidDataException){rejected=true;}
        Check(rejected,"Orphan tool result refused");
        Check(File.ReadAllBytes(Path.Combine(root,firstSource+".json")).SequenceEqual(original),"Negative requests must not replace prior source");
        await using(var blocked=new ContinueContextMemory(root+"-cancel",async(_,t)=>{await Task.Delay(Timeout.Infinite,t);return 1;},(_,_,_,_)=>throw new Exception("Must not select"))){
            using var deadline=new CancellationTokenSource(TimeSpan.FromMilliseconds(50));var cancelled=false;
            try{await blocked.PrepareAsync(shortBytes,deadline.Token);}catch(OperationCanceledException){cancelled=true;}
            Check(cancelled,"Cancellation must bound an unavailable measurement before selection/forwarding");
        }
        await TransportAsync(false);await TransportAsync(true);await TransportAsync(false,true);await TransportAsync(false,false,true);
        Console.WriteLine("Continue >32K history recall, exact latest request/system/tool pairs, physical32K admission, immutable source and failure isolation PASS; public fixtures, model0, production0.");
    }

    private sealed class Peer:HttpMessageHandler
    {
        internal int Forwarded;internal string? Body;internal bool ReplyLost,StreamInterrupted;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.RequestUri!.AbsolutePath=="/v1/chat/completions"){
                Forwarded++;Body=await request.Content!.ReadAsStringAsync(token);
                if(ReplyLost)throw new HttpRequestException("Public fixture lost the reply after receiving the request");
            }
            return new(HttpStatusCode.OK){Content=StreamInterrupted&&request.RequestUri!.AbsolutePath=="/v1/chat/completions"?new BrokenContent():new StringContent("{\"fixture\":true}",Encoding.UTF8,"application/json")};
        }
    }
    private sealed class BrokenContent:HttpContent
    {
        internal BrokenContent(){Headers.ContentType=new("text/event-stream");}
        protected override bool TryComputeLength(out long length){length=0;return false;}
        protected override async Task SerializeToStreamAsync(Stream stream,TransportContext? context)
        {await stream.WriteAsync(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n"));await stream.FlushAsync();throw new IOException("public partial stream failure");}
    }
    private static async Task TransportAsync(bool unavailable,bool replyLost=false,bool streamInterrupted=false)
    {
        var root=Path.Combine(Path.GetTempPath(),"continue-transport-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var reserve=new TcpListener(IPAddress.Loopback,0);reserve.Start();var port=((IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
        var memory=new ContinueContextMemory(Path.Combine(root,"archive"),(p,t)=>Task.FromResult(p["messages"]!.AsArray().Count>10?40000:1000),
            (_,_,_,_)=>unavailable?throw new IOException("public fixture unavailable"):Task.FromResult("{\"event_ids\":[]}"));
        var peer=new Peer{ReplyLost=replyLost,StreamInterrupted=streamInterrupted};using var gateway=new GatewayClient(new Uri("http://public-fixture.invalid"),peer);
        var config=new ClientConfig("https://public-fixture.invalid",port,"unused",Path.Combine(root,"projects.json"),Path.Combine(root,"audit.jsonl"));
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serving=new Bridge(config,gateway,new Audit(config),memory).RunAsync(stop.Token);
        var messages=new JsonArray(Message("system","policy"),Message("user","original"));
        for(var i=0;i<20;i++)messages.Add(Message("assistant","historic"));messages.Add(Message("user","current"));
        var request=new JsonObject{["model"]="local-qwen38",["messages"]=messages,["stream"]=false,["max_tokens"]=512};
        using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(10)};
        try{
            if(streamInterrupted){
                var failed=false;
                try{using var broken=await client.PostAsync($"http://127.0.0.1:{port}/v1/chat/completions",new StringContent(request.ToJsonString(),Encoding.UTF8,"application/json"));await broken.Content.ReadAsStringAsync();}
                catch(HttpRequestException){failed=true;}
                var audit=File.ReadAllText(config.AuditLog);
                Check(failed&&peer.Forwarded==1&&audit.Contains("response_received")&&!audit.Contains("continue_context_handoff_failed"),"Partial stream abort must not be completed, recast as no-dispatch, replayed or closed twice");return;
            }
            using var response=await client.PostAsync($"http://127.0.0.1:{port}/v1/chat/completions",new StringContent(request.ToJsonString(),Encoding.UTF8,"application/json"));
            var body=await response.Content.ReadAsStringAsync();
            if(replyLost)Check((int)response.StatusCode==422&&peer.Forwarded==1&&body.Contains("dispatch_outcome_unknown")&&!body.Contains("context_handoff_unavailable"),"Lost reply must preserve unknown dispatch, prohibit implicit retry and never claim no send");
            else if(unavailable)Check((int)response.StatusCode==422&&peer.Forwarded==0&&body.Contains("automatic_retry")&&body.Contains("context_handoff_unavailable"),"Admission failure must be nonretryable and send nothing to the model peer");
            else Check(response.IsSuccessStatusCode&&peer.Forwarded==1&&response.Headers.Contains("X-LocalBrain-Context-Recall")&&peer.Body!.Contains("current"),"Real loopback transport must forward the prepared request exactly once and return its receipt header");
        }finally{stop.Cancel();await serving;}
    }
}
