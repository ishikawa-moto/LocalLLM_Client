using System.Net;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.Chat;
using LocalBrain.ClientHost.AgentV2;
using LocalBrain.ClientHost.Ocr;
using LocalBrain.ClientHost.Slack;

internal static class ChatOcrTests
{
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> call):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t)=>call(r);}
    internal static async Task RunAsync(){
        int checks=0;void Check(bool b,string name){if(!b)throw new Exception("CHAT_OCR_TEST: "+name);checks++;}
        void Reject(Action a,string name){try{a();throw new Exception("accepted: "+name);}catch(Exception e)when(e is InvalidDataException or UnauthorizedAccessException or JsonException or OcrException){checks++;}}
        var root=Directory.CreateTempSubdirectory("lb-public-chat-ocr-").FullName;
        var config=new OcrConfig(true,Path.Combine(root,"ocr"),Path.Combine(root,"assets"),Path.Combine(root,"assets","python.exe"),Path.Combine(root,"assets","worker.py"),Path.Combine(root,"assets","models"),[Path.Combine(root,"inbox")],new(){["a"]="a",["b"]="b",["c"]="c"},["AUTO","PDF_DOCUMENT"],["HANDWRITING"],512L<<20,512L<<20,384L<<20,25,new string('a',64));
        var options=new ChatOcrRelayOptions(true,Path.Combine(root,"relay"));var processor=new ChatOcrProcessor(options,config);var store=new OcrJobStore(config);
        byte[] png=[137,80,78,71,13,10,26,10,0,0];var hash=CanonicalStore.Hash(png);var conv=Guid.NewGuid().ToString("D");
        ChatOcrRequest Request(string[]? formats=null)=>new(ChatOcrRequest.Version,Guid.NewGuid().ToString("N"),conv,hash,".png",formats??["md"],Convert.ToBase64String(png),false);
        var job=Request();Check(job.Decode().SequenceEqual(png),"image hash and magic admitted");
        foreach(var bad in new[]{job with{Schema="other"},job with{JobId="../private"},job with{ConversationId="../session"},job with{InputSha256=new string('A',64)},job with{Extension=".exe"},job with{Formats=["html"]},job with{Formats=["md","md"]},job with{Formats=["md","https://public.invalid/"]},job with{Bytes="not-base64"},job with{Bytes=Convert.ToBase64String(new byte[]{1,2,3})},job with{Extension=".pdf"}})Reject(()=>bad.Decode(),"invalid envelope/data");
        Reject(()=>JsonSerializer.Deserialize<ChatOcrRequest>(JsonSerializer.Serialize(job,ChatOcrProcessor.Json)[..^1]+",\"path\":\"PRIVATE\"}",ChatOcrProcessor.Json),"unknown executable/path fields denied");
        foreach(var pair in new[]{(".jpg",new byte[]{255,216,255}), (".webp",Encoding.ASCII.GetBytes("RIFFxxxxWEBP")),(".pdf",Encoding.ASCII.GetBytes("%PDF-1.7"))}){
            var value=job with{Extension=pair.Item1,Bytes=Convert.ToBase64String(pair.Item2),InputSha256=CanonicalStore.Hash(pair.Item2)};Check(value.Decode().SequenceEqual(pair.Item2),"same supported source format");
        }
        var local=Path.Combine(root,"public.png");File.WriteAllBytes(local,png);
        Reject(()=>store.Submit(local,hash,"AUTO","public_chat_origin",origin:"chat"),"outside Host restriction cannot forge chat origin");
        using(RestrictedExecution.EnterChat()){
            Check(RestrictedExecution.Active,"chat context restricted");Reject(RestrictedExecution.DenyExternal,"external AI denied");Reject(RestrictedExecution.DenyAgentTools,"agent tools denied");
        }
        Check(!RestrictedExecution.Active,"restriction scope released");
        var first=JsonSerializer.Deserialize<JsonElement>(processor.Step(job));Check(first.GetProperty("state").GetString()=="queued","same OCR scheduler queue");
        var oid=CanonicalStore.Hash(Encoding.UTF8.GetBytes("chat_"+job.JobId))[..32];var manifest=store.GetManifest(oid);
        Check(manifest.Origin=="chat"&&manifest.ExternalAi=="deny"&&!manifest.RetainOriginal,"source/privacy immutable");
        var replay=processor.Step(job with{Bytes=null});Check(store.GetManifest(oid).InputSha256==hash&&Directory.GetDirectories(store.Jobs).Length==1,"restart/poll does not duplicate OCR");
        Reject(()=>processor.Step(job with{Formats=["md","html"]}),"frozen output format binding");Reject(()=>processor.Step(job with{ConversationId=Guid.NewGuid().ToString("D")}),"conversation binding");
        void Complete(ChatOcrRequest x,bool table){
            processor.Step(x);var id=CanonicalStore.Hash(Encoding.UTF8.GetBytes("chat_"+x.JobId))[..32];var m=store.GetManifest(id);store.Transition(id,"running",count:1);
            var page=JsonSerializer.SerializeToElement(new{page=0,input_sha256=hash,external_ai="deny",text="PUBLIC 1,234",needs_review=true,spans=Array.Empty<object>(),tables=table?new[]{new{table_id="PUBLIC",rows=1,columns=1,cells=new[]{new{row_index=0,column_index=0,row_span=1,column_span=1,text="1,234",needs_review=true}}}}:[]});
            store.CommitPage(m,0,page);store.Finish(m);
        }
        Complete(job,true);var completed=processor.Step(job with{Bytes=null});var result=JsonSerializer.Deserialize<JsonElement>(completed);
        Check(result.GetProperty("state").GetString()=="complete"&&result.GetProperty("result").GetProperty("origin").GetString()=="chat","canonical local result returned");
        var artifacts=result.GetProperty("artifacts").EnumerateArray().ToArray();Check(artifacts.Select(a=>a.GetProperty("format").GetString()).ToHashSet().SetEquals(new[]{"md","json"}),"default Markdown and table JSON");
        foreach(var a in artifacts){var bytes=a.GetProperty("bytes").GetBytesFromBase64();Check(CanonicalStore.Hash(bytes)==a.GetProperty("sha256").GetString(),"frozen artifact hash");}
        Check(new ChatOcrProcessor(options,config).Step(job with{Bytes=null})==completed,"lost terminal response returns identical durable report");
        var frozenPath=Path.Combine(options.Root,job.JobId+".update.json");var savedFrozen=File.ReadAllBytes(frozenPath);
        Check(JsonSerializer.Deserialize<JsonElement>(processor.Report(job with{Bytes=null,CancelRequested=true})).GetProperty("state").GetString()=="cancelled"&&File.ReadAllBytes(frozenPath).SequenceEqual(savedFrozen),"cancellation after local completion preserves frozen evidence");
        var corrupt=System.Text.Json.Nodes.JsonNode.Parse(savedFrozen)!;corrupt["payload"]=corrupt["payload"]!.GetValue<string>()+" ";File.WriteAllText(frozenPath,corrupt.ToJsonString());var badFrozen=File.ReadAllBytes(frozenPath);
        Reject(()=>processor.Report(job with{Bytes=null}),"corrupt terminal evidence held without replacement");Check(File.ReadAllBytes(frozenPath).SequenceEqual(badFrozen),"unknown evidence not overwritten");File.WriteAllBytes(frozenPath,savedFrozen);
        var text=Request();Complete(text,false);var textOutput=JsonSerializer.Deserialize<JsonElement>(processor.Step(text));Check(textOutput.GetProperty("artifacts").GetArrayLength()==1&&textOutput.GetProperty("artifacts")[0].GetProperty("format").GetString()=="md","non-table default MD only");
        var all=Request(["md","html","csv"]);Complete(all,true);var allOutput=JsonSerializer.Deserialize<JsonElement>(processor.Step(all));Check(allOutput.GetProperty("artifacts").GetArrayLength()==4,"explicit HTML/CSV plus default table JSON");
        var cancel=Request() with{CancelRequested=true,Bytes=null};var count=Directory.GetDirectories(store.Jobs).Length;
        Check(JsonSerializer.Deserialize<JsonElement>(processor.Step(cancel)).GetProperty("state").GetString()=="cancelled"&&Directory.GetDirectories(store.Jobs).Length==count,"cancel before receipt never starts OCR");Check(processor.Step(cancel)==processor.Step(cancel),"cancel restart idempotent");
        var running=Request();processor.Step(running);var rid=CanonicalStore.Hash(Encoding.UTF8.GetBytes("chat_"+running.JobId))[..32];Check(JsonSerializer.Deserialize<JsonElement>(processor.Step(running with{Bytes=null,CancelRequested=true})).GetProperty("state").GetString()=="cancelling","cancel requests owned OCR quiescence");store.Transition(rid,"cancelled");Check(JsonSerializer.Deserialize<JsonElement>(processor.Step(running with{Bytes=null,CancelRequested=true})).GetProperty("state").GetString()=="cancelled","cancelled reports no result");
        var unqualifiedConfig=config with{QualifiedProfiles=["PDF_DOCUMENT"],Root=Path.Combine(root,"unqualified-ocr")};
        var unavailable=new ChatOcrProcessor(options with{Root=Path.Combine(root,"unqualified-relay")},unqualifiedConfig);var badProfile=Request();
        var unavailableReport=unavailable.Report(badProfile);Check(JsonSerializer.Deserialize<JsonElement>(unavailableReport).GetProperty("error_code").GetString()=="OCR_PROFILE_UNQUALIFIED","unqualified profile reports terminal failure, not an endless wait");
        Check(unavailable.Report(badProfile with{Bytes=null})==unavailableReport,"lost failure response remains immutable");
        var calls=0;using var gateway=new GatewayClient(new Uri("https://public.invalid"),new Handler(async request=>{
            calls++;Check(request.Headers.GetValues("X-LocalBrain-Approval-Owner").Single()=="pc73-windows-host","existing owning Host header");Check(request.RequestUri!.AbsolutePath=="/v1/chat-ocr/next","fixed route");await request.Content!.ReadAsByteArrayAsync();return new(HttpStatusCode.OK){Content=new StringContent("{\"job\":null}")};}));
        Reject(()=>gateway.ChatOcrJsonAsync("/v1/chat-ocr/next","{}",CancellationToken.None).GetAwaiter().GetResult(),"nonrestricted relay denied");
        using(RestrictedExecution.EnterChat()){
            Check(await gateway.ChatOcrJsonAsync("/v1/chat-ocr/next","{}",CancellationToken.None)=="{\"job\":null}","private polling context allowed");
            Reject(()=>gateway.ChatOcrJsonAsync("https://public.invalid/collect","{}",CancellationToken.None).GetAwaiter().GetResult(),"arbitrary outbound destination denied");
        }
        foreach(var p in new[]{"/v1/chat-ocr/next","/v1/chat-ocr/update","/V1/CHAT-OCR/next","/%76%31/chat-ocr/next"})Reject(()=>gateway.SendAsync(HttpMethod.Post,p,null,null,null,Guid.NewGuid().ToString(),CancellationToken.None).GetAwaiter().GetResult(),"ordinary Bridge cannot call mailbox");
        Check(calls==1,"denied requests never dispatched");processor.Cleanup(DateTimeOffset.UtcNow.AddHours(25));Check(!File.Exists(Path.Combine(options.Root,job.JobId+".update.json")),"terminal local cache expires separately from canonical OCR");
        Console.WriteLine(JsonSerializer.Serialize(new{chat_ocr_public_checks=checks,status="PASS",real_gateway=false,real_ocr=false,model_runs=0,production_changes=0}));
    }
}
