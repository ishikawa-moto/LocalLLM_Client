using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.AgentV2;

internal static class WorkLoopSupervisorTests
{
    static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
    static JsonObject Message(string role,string value)=>new(){["role"]=role,["content"]=value};
    internal static byte[] History(int count,bool errors=false,bool changing=false,int cycle=1,bool newUser=false)
    {
        var messages=new JsonArray(Message("system","PUBLIC_POLICY_KEEP_APPROVALS"),Message("user","PUBLIC_TASK no private information"));
        for(var i=0;i<count;i++){
            var id="call_"+i;
            messages.Add(new JsonObject{["role"]="assistant",["tool_calls"]=new JsonArray(new JsonObject{["id"]=id,["type"]="function",
                ["function"]=new JsonObject{["name"]="public_read",["arguments"]=i%2==0?"{\"path\":\"public.txt\",\"mode\":1}":"{\"mode\":1,\"path\":\"public.txt\"}"}})});
            messages.Add(new JsonObject{["role"]="tool",["tool_call_id"]=id,["content"]=errors?"Error: PUBLIC_TEST_FAILURE":"PUBLIC_RESULT_"+(changing?i:i%cycle)});
        }
        if(newUser)messages.Add(Message("user","Intentional new user request; inspect the previous observation once"));
        return Encoding.UTF8.GetBytes(new JsonObject{["model"]="local-qwen38",["max_tokens"]=32,["stream"]=false,["messages"]=messages}.ToJsonString());
    }
    internal static async Task RunAsync()
    {
        Check(WorkLoopSupervisor.Inspect(History(4)).BlockReason=="repeated_tool_result_cycle","Identical calls/results must stop even if JSON property order differs");
        Check(WorkLoopSupervisor.Inspect(History(8,cycle:2)).BlockReason=="repeated_tool_result_cycle","A/B cycles must stop");
        Check(WorkLoopSupervisor.Inspect(History(12,cycle:3)).BlockReason=="repeated_tool_result_cycle","A/B/C cycles must stop");
        Check(WorkLoopSupervisor.Inspect(History(3,true)).BlockReason=="repeated_identical_error","Three identical consecutive errors must stop");
        Check(WorkLoopSupervisor.Inspect(History(24,changing:true)).BlockReason is null,"Different results are observable progress, not a repetition loop");
        Check(WorkLoopSupervisor.Inspect(History(4,newUser:true)).Tools==0,"New explicit user request resets the work segment");
        Check(WorkLoopSupervisor.Inspect(History(3)).BlockReason is null,"A short repeated check must not be classified as a four-cycle loop");
        var root=Path.Combine(Path.GetTempPath(),"work-loop-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var config=new ClientConfig("https://public.invalid",18081,"unused",Path.Combine(root,"projects.json"),Path.Combine(root,"audit.jsonl"));var audit=new Audit(config);
        await using(var monitor=new WorkLoopSupervisor()){
            var original=History(1);await monitor.CheckAsync(original,"public-a",audit,CancellationToken.None);
            await monitor.CheckAsync(History(4,changing:true),"public-b",audit,CancellationToken.None);
            var blocked=false;try{await monitor.CheckAsync(History(4),"public-c",audit,CancellationToken.None);}catch(WorkLoopBlockedException){blocked=true;}
            Check(blocked,"Hard loops stop before dispatch");
        }
        Check(!File.ReadAllText(config.AuditLog).Contains("PUBLIC_POLICY_KEEP_APPROVALS"),"Audits must not contain source policy, commands or tool results");
        await TransportAsync("loop");await TransportAsync("success");await TransportAsync("header-timeout");await TransportAsync("stream-timeout");await TransportAsync("total-timeout");
        Console.WriteLine("Work-loop monitoring: identical/cyclic/error loops, new user reset, distinct progress, original-byte preservation and HTTP timeout/no-replay fixtures PASS.");
    }
    private sealed class Peer(string mode):HttpMessageHandler
    {
        internal int Forwards;internal byte[]? Original;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t){
            if(r.RequestUri!.AbsolutePath!="/v1/chat/completions")return new(HttpStatusCode.OK){Content=new StringContent("{}")};
            Forwards++;Original=await r.Content!.ReadAsByteArrayAsync(t);
            if(mode=="header-timeout")await Task.Delay(Timeout.Infinite,t);
            return new(HttpStatusCode.OK){Content=mode is "stream-timeout" or "total-timeout"?new SlowContent(mode):new StringContent("{\"choices\":[{\"message\":{\"content\":\"PUBLIC_OK\"}}]}")};
        }
    }
    private sealed class SlowContent(string mode):HttpContent
    {
        protected override bool TryComputeLength(out long n){n=0;return false;}
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? c)=>SerializeToStreamAsync(stream,c,CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream,TransportContext? c,CancellationToken token){using var source=new SlowStream(mode);await source.CopyToAsync(stream,token);}
        protected override Task<Stream> CreateContentReadStreamAsync()=>Task.FromResult<Stream>(new SlowStream(mode));
    }
    private sealed class SlowStream(string mode):Stream
    {
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>0;set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){
            await Task.Delay(mode=="stream-timeout"?Timeout.Infinite:30,token);buffer.Span[0]=32;return 1;
        }
        public override int Read(byte[] b,int o,int n)=>throw new NotSupportedException();public override void Flush(){}public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();
        public override void SetLength(long l)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int n)=>throw new NotSupportedException();
    }
    private static async Task TransportAsync(string mode)
    {
        var root=Path.Combine(Path.GetTempPath(),"work-loop-http-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var tcp=new TcpListener(IPAddress.Loopback,0);tcp.Start();var port=((IPEndPoint)tcp.LocalEndpoint).Port;tcp.Stop();
        var config=new ClientConfig("https://public.invalid",port,"unused",Path.Combine(root,"projects.json"),Path.Combine(root,"audit.jsonl"));
        var monitor=new WorkLoopSupervisor(){
            IdleLimit=TimeSpan.FromMilliseconds(150),RequestLimit=TimeSpan.FromMilliseconds(mode=="total-timeout"?350:3000)};
        var measured=0;
        var memory=new ContinueContextMemory(Path.Combine(root,"archive"),(r,t)=>{measured++;return Task.FromResult(100);},(_,_,_,_)=>throw new Exception("No memory model required"));
        var peer=new Peer(mode);using var gateway=new GatewayClient(new Uri("http://public.invalid"),peer);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(mode=="native-success"?120:12));var server=new Bridge(config,gateway,new Audit(config),memory,monitor).RunAsync(stop.Token);
        using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(mode=="native-success"?100:5)};var original=History(mode=="loop"?4:1);
        try{
            if(mode=="total-timeout"){
                var aborted=false;try{using var result=await client.PostAsync($"http://127.0.0.1:{port}/v1/chat/completions",new ByteArrayContent(original));await result.Content.ReadAsStringAsync();}catch(HttpRequestException){aborted=true;}
                Check(aborted&&peer.Forwards==1,"An idle or absolute timeout must abort an incomplete response without retry or DONE");
            }else{
                using var response=await client.PostAsync($"http://127.0.0.1:{port}/v1/chat/completions",new ByteArrayContent(original));var body=await response.Content.ReadAsStringAsync();
                if(mode=="loop")Check((int)response.StatusCode==422&&peer.Forwards==0&&measured==0&&body.Contains("work_loop_paused"),"Hard loop must stop before memory measurement/compaction and not reach model peer");
                else if(mode is "header-timeout" or "stream-timeout")Check((int)response.StatusCode==422&&peer.Forwards==1&&body.Contains("dispatch_outcome_unknown"),"Timeout before any bytes must return a nonretryable unknown-dispatch error");
                else Check(response.IsSuccessStatusCode&&peer.Forwards==1&&peer.Original!.SequenceEqual(original),"Normal operation must preserve exact body and send once");
            }
            using var status=await client.GetAsync($"http://127.0.0.1:{port}/localbrain/supervision/status");
            var text=await status.Content.ReadAsStringAsync();Check(text.Contains("active_requests")&&!text.Contains("PUBLIC_TASK"),"Read-only coverage status must expose counts without source contents");
        }finally{stop.Cancel();await server;}
    }
}
