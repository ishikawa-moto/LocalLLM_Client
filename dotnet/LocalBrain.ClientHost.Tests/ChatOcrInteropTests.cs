using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.AgentV2;
using LocalBrain.ClientHost.Chat;
using LocalBrain.ClientHost.Ocr;
using LocalBrain.ClientHost.Slack;

// Uses the real C# DTO/processor/renderers and the real Python Server mailbox.
// OCR pages and mTLS peer identities are public fixtures, not real inference or TLS.
internal static class ChatOcrInteropTests
{
    private sealed class Pipe : IDisposable
    {
        private readonly Process process;
        internal Pipe(string python, string source, string root)
        {
            var start = new ProcessStartInfo(python) { UseShellExecute=false, RedirectStandardInput=true,
                RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true };
            foreach (var item in new[]{"-I","-B",Path.Combine(AppContext.BaseDirectory,"Fixtures","chat_ocr_mailbox.py"),source,root})
                start.ArgumentList.Add(item);
            process=Process.Start(start)??throw new Exception("PUBLIC_PIPE_START");
        }
        internal JsonElement Call(object value)
        {
            process.StandardInput.WriteLine(JsonSerializer.Serialize(value,new JsonSerializerOptions(ChatOcrProcessor.Json){WriteIndented=false}));
            process.StandardInput.Flush();
            var line=process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult()
                ??throw new Exception("PUBLIC_PIPE_EOF");
            using var doc=JsonDocument.Parse(line);
            return doc.RootElement.Clone();
        }
        internal JsonElement Ok(object value)
        {
            var response=Call(value);
            if(response.GetProperty("status").GetInt32()!=200)
                throw new Exception("PUBLIC_MAILBOX_REJECTED:"+response.GetProperty("body").GetRawText());
            return response.GetProperty("body").Clone();
        }
        public void Dispose()
        {
            process.StandardInput.Close();
            if(!process.WaitForExit(10000))process.Kill(entireProcessTree:true);
            var code=process.HasExited?process.ExitCode:-1;
            process.Dispose();
            if(code!=0)throw new Exception("PUBLIC_PIPE_EXIT");
        }
    }
    private sealed class Handler(Pipe pipe):HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var raw=await request.Content!.ReadAsStringAsync(token);
            using var body=JsonDocument.Parse(raw);
            var response=pipe.Call(new{action="worker",path=request.RequestUri!.AbsolutePath,
                owner=request.Headers.GetValues("X-LocalBrain-Approval-Owner").Single(),body=body.RootElement});
            return new((HttpStatusCode)response.GetProperty("status").GetInt32())
                {Content=new StringContent(response.GetProperty("body").GetRawText(),Encoding.UTF8,"application/json")};
        }
    }
    internal static async Task RunAsync(string source,string python,string root,string? publicInput=null,string? publicPage=null)
    {
        root=Path.GetFullPath(root);source=Path.GetFullPath(source);python=Path.GetFullPath(python);
        CanonicalStore.GuardPath(root);
        if(Directory.Exists(root)||!File.Exists(Path.Combine(source,"chat_ocr.py")))throw new Exception("NEW_PUBLIC_INTEROP_ROOT_REQUIRED");
        Directory.CreateDirectory(root);
        var config=new OcrConfig(true,Path.Combine(root,"ocr"),Path.Combine(root,"assets"),Path.Combine(root,"assets","python.exe"),
            Path.Combine(root,"assets","worker.py"),Path.Combine(root,"assets","models"),[Path.Combine(root,"inbox")],
            new(){["a"]="a",["b"]="b",["c"]="c"},["AUTO","PDF_DOCUMENT"],[],1024L<<20,1024L<<20,704L<<20,25,new string('a',64));
        var options=new ChatOcrRelayOptions(true,Path.Combine(root,"relay"));
        var processor=new ChatOcrProcessor(options,config);var store=new OcrJobStore(config);
        using var pipe=new Pipe(python,source,Path.Combine(root,"server"));
        using var gateway=new GatewayClient(new Uri("https://public.invalid"),new Handler(pipe));
        using var restricted=RestrictedExecution.EnterChat();
        int checks=0;
        void Check(bool condition,string code){if(!condition)throw new Exception("CHAT_OCR_INTEROP:"+code);checks++;}
        async Task<ChatOcrRequest> Poll()
        {
            using var json=JsonDocument.Parse(await gateway.ChatOcrJsonAsync("/v1/chat-ocr/next","{\"schema\":\"localbrain.chat-ocr.poll.v1\"}",CancellationToken.None));
            return JsonSerializer.Deserialize<ChatOcrRequest>(json.RootElement.GetProperty("job"),ChatOcrProcessor.Json)!;
        }
        async Task Update(string report)
        {
            using var ack=JsonDocument.Parse(await gateway.ChatOcrJsonAsync("/v1/chat-ocr/update",report,CancellationToken.None));
            Check(ack.RootElement.GetProperty("accepted").GetBoolean(),"worker report accepted");
        }
        JsonElement Chat(string route,object body)=>pipe.Ok(new{action="chat",route,body});
        ChatOcrRequest Submit(bool pdf,string[] formats,byte[]? preserved=null)
        {
            byte[] bytes=preserved??(pdf?Encoding.ASCII.GetBytes("%PDF-1.7 PUBLIC FIXTURE"):[137,80,78,71,13,10,26,10,0,0]);
            var job=new ChatOcrRequest(ChatOcrRequest.Version,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("D"),
                CanonicalStore.Hash(bytes),pdf?".pdf":".png",formats,Convert.ToBase64String(bytes),false);
            pipe.Ok(new{action="submit",body=new{job_id=job.JobId,conversation_id=job.ConversationId,input_sha256=job.InputSha256,
                extension=job.Extension,formats=job.Formats,bytes=job.Bytes}});
            return job;
        }
        byte[]? preservedBytes=publicInput is null?null:File.ReadAllBytes(publicInput);
        JsonElement? preservedPage=publicPage is null?null:JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(publicPage));
        if(preservedPage is {} evidence)Check(preservedBytes is not null&&evidence.GetProperty("input_sha256").GetString()==CanonicalStore.Hash(preservedBytes),"preserved public OCR matches original fixture");
        var scenarios=new List<(int Pages,bool Table,bool All,bool Preserved)>{(1,false,false,false),(1,true,false,false),(1,true,true,false),(26,false,false,false),(200,false,false,false)};
        if(preservedPage is not null)scenarios.Add((1,true,true,true));
        foreach(var scenario in scenarios)
        {
            var submitted=Submit(scenario.Pages>1,scenario.All?["md","html","csv"]:["md"],scenario.Preserved?preservedBytes:null);
            var job=await Poll();Check(job.JobId==submitted.JobId&&job.Decode().SequenceEqual(submitted.Decode()),"Python next consumed by C# DTO");
            await Update(processor.Report(job));
            var duplicate=await Poll();Check(duplicate.JobId==job.JobId&&duplicate.Bytes is null,"registration ACK deletes Server source");
            processor.Report(duplicate);
            var oid=CanonicalStore.Hash(Encoding.UTF8.GetBytes("chat_"+job.JobId))[..32];
            var manifest=store.GetManifest(oid);store.Transition(oid,"queued",count:scenario.Pages);
            await Update(processor.Report(duplicate)); // Real progress serialization at 1/26/200 pages.
            store.Transition(oid,"running");
            for(var page=0;page<scenario.Pages;page++)
            {
                var body=JsonSerializer.SerializeToElement(new{page,status="complete",source_method="ocr",input_sha256=job.InputSha256,
                    external_ai="deny",network_attempts=0,text="PUBLIC 日本語 1,234 <script>PUBLIC</script>",needs_review=true,warnings=Array.Empty<string>(),
                    coordinate_system=new{units=job.Extension==".pdf"?"pdf_display_points":"original_image_pixels",origin="top_left",width=100,height=100,raster_width=100,raster_height=100},
                    engine=new{engine="RapidOCR",runtime="onnxruntime",requested_providers=new[]{"CPUExecutionProvider"},threads=2},
                    spans=Array.Empty<object>(),tables=scenario.Table?new[]{new{table_id="PUBLIC",rows=1,columns=1,cells=new[]{new{
                        row_index=0,column_index=0,row_span=1,column_span=1,text="<script>PUBLIC</script>",needs_review=true}}}}:[]});
                store.CommitPage(manifest,page,scenario.Preserved?preservedPage!.Value:body);
            }
            store.Finish(manifest);
            var report=processor.Report(job with{Bytes=null});await Update(report);await Update(report);
            Check(new ChatOcrProcessor(options,config).Report(job with{Bytes=null})==report,"frozen C# terminal replay");
            var status=Chat("status",new{conversation_id=job.ConversationId}).GetProperty("jobs")[0];
            Check(status.GetProperty("state").GetString()=="complete"&&status.GetProperty("completed_pages").GetInt32()==scenario.Pages,"real Server accepts Client complete");
            var expected=scenario.All?4:scenario.Table?2:1;
            Check(status.GetProperty("artifacts").GetArrayLength()==expected,"default/requested artifacts");
            foreach(var a in status.GetProperty("artifacts").EnumerateArray())
            {
                var format=a.GetProperty("format").GetString();
                var download=Chat("artifact",new{conversation_id=job.ConversationId,job_id=job.JobId,format});
                var bytes=download.GetProperty("bytes").GetBytesFromBase64();
                Check(CanonicalStore.Hash(bytes)==a.GetProperty("sha256").GetString(),"download hash equals Client artifact");
                if(format=="html")Check(!Encoding.UTF8.GetString(bytes).Contains("<script>PUBLIC</script>"),"HTML escapes document scripts");
            }
            Check(!File.Exists(store.Input(manifest)),"owned source removed on completion");
        }
        var cancelled=Submit(false,["md"]);var pending=await Poll();await Update(processor.Report(pending));
        Chat("cancel",new{conversation_id=pending.ConversationId,job_id=pending.JobId});
        var cancelPoll=await Poll();Check(cancelPoll.CancelRequested,"Server cancel reaches actual Client");
        await Update(processor.Report(cancelPoll));
        var mid=Chat("status",new{conversation_id=pending.ConversationId}).GetProperty("jobs")[0];
        Check(!mid.GetProperty("worker_quiescent").GetBoolean(),"Server waits for OCR quiescence");
        var cancelledId=CanonicalStore.Hash(Encoding.UTF8.GetBytes("chat_"+pending.JobId))[..32];store.Transition(cancelledId,"cancelled");
        await Update(processor.Report(await Poll()));
        var end=Chat("status",new{conversation_id=pending.ConversationId}).GetProperty("jobs")[0];
        Check(end.GetProperty("state").GetString()=="cancelled"&&end.GetProperty("worker_quiescent").GetBoolean(),"terminal cancellation releases assignment");
        var receipt=new{status="PASS_ACTUAL_CLIENT_PYTHON_MAILBOX",checks,page_cases=new[]{1,26,200},real_client_processor=true,
            real_server_mailbox=true,transport="private subprocess pipe and HTTP handler",real_tls=false,real_ocr=false,
            model_runs=0,private_input=false,production_changes=false,preserved_real_ocr_page=preservedPage is not null,
            preserved_input_sha256=preservedBytes is null?null:CanonicalStore.Hash(preservedBytes),
            preserved_page_sha256=publicPage is null?null:CanonicalStore.Hash(File.ReadAllBytes(publicPage))};
        File.WriteAllText(Path.Combine(root,"interop-evidence.json"),JsonSerializer.Serialize(receipt,ChatOcrProcessor.Json));
        Console.WriteLine(JsonSerializer.Serialize(receipt));
    }
}
