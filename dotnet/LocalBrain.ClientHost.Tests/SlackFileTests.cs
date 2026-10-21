using System.Net;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.Slack;

internal static class SlackFileTests
{
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> call):HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>call(request); }
    internal static async Task RunAsync()
    {
        var checks=0;void Check(bool ok,string name){if(!ok)throw new Exception("SLACK_FILE_TEST_FAILED: "+name);checks++;}
        var root=Directory.CreateTempSubdirectory("lb-public-file-return-").FullName;
        var options=new SlackOptions(true,root,"TTEST","CTEST","UBOT",Path.Combine(root,"app.token"),Path.Combine(root,"bot.token"),ReturnFileResults:true);
        var now=DateTimeOffset.UtcNow;using var store=new SlackStore(options);
        var raw=string.Concat(Enumerable.Repeat("PUBLIC 文字起こし\n",5000));
        var sequence=0;string Prepare(string label){
            var ts=now.ToUnixTimeSeconds()+"."+(++sequence).ToString("D6");
            var m=new SlackMention("EV-"+label,"TTEST","CTEST","UALICE",ts,ts,"<@UBOT> 添付画像の文字をテキストで返してください。",["FPUBLIC"],now);
            var id=store.Accept(m,now.AddMinutes(-1),now).JobId!;
            while(store.ClaimReply(now) is{} ack)store.ReplySent(ack.Id,now.ToUnixTimeSeconds()+".000002");
            Check(store.ClaimNext()?.Id==id,"single accepted job");
            store.SaveAttachment(id,"FPUBLIC",".png",[137,80,78,71,13,10,26,10]);store.BindOcr(id,"FPUBLIC",new string('a',32));
            store.SaveOcrResult(id,"FPUBLIC",JsonSerializer.Serialize(new{text=raw}));
            store.Finish(id,"{\"output_kind\":\"ocr_text\"}",true,true);return id;
        }
        var job=Prepare("SUCCESS");
        Check(SlackResultText.OcrOnly(store.GetJob(job).Text)&&!SlackResultText.OcrOnly("添付資料の要点を要約してください"),"transcription distinguished from interpretation");
        var file=store.ClaimResultFile(now)!;
        Check(Encoding.UTF8.GetString(file.Bytes)==raw&&file.Bytes.Length>48000,"long raw UTF8 transcription untruncated");
        Check(file.Channel=="CTEST"&&file.Thread==now.ToUnixTimeSeconds()+".000001"&&file.Filename=="ocr-"+job+".txt","original channel thread and txt binding");
        store.RetryResultFile(file,now.AddSeconds(-1));
        var calls=0;using var api=new SlackApi(new Handler(async request=>{
            calls++;var url=request.RequestUri!;
            if(url.Host=="files.slack.com"){
                Check(request.Headers.Authorization is null,"upload capability receives no bearer secret");
                Check((await request.Content!.ReadAsByteArrayAsync()).SequenceEqual(file.Bytes),"upload exact frozen text bytes");
                return new(HttpStatusCode.OK){Content=new StringContent("OK")};
            }
            Check(request.Headers.Authorization?.Parameter=="xoxb-PUBLIC","official API uses bot token");
            if(url.AbsolutePath.EndsWith("getUploadURLExternal")){
                Check(request.Content!.Headers.ContentType?.MediaType=="application/x-www-form-urlencoded","ticket uses live-compatible official form contract");
                var form=(await request.Content.ReadAsStringAsync()).Split('&').Select(x=>x.Split('=',2)).ToDictionary(x=>Uri.UnescapeDataString(x[0]),x=>Uri.UnescapeDataString(x[1]));
                Check(form.Count==2&&form["filename"]==file.Filename&&int.Parse(form["length"])==file.Bytes.Length,"exact filename and byte length; no token in form");
                return Json("{\"ok\":true,\"file_id\":\"FPUBLIC\",\"upload_url\":\"https://files.slack.com/upload/v1/PUBLIC\"}");
            }
            var body=JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsByteArrayAsync());
            Check(body.GetProperty("channel_id").GetString()==file.Channel&&body.GetProperty("thread_ts").GetString()==file.Thread,"share only original thread");
            return Json("{\"ok\":true,\"files\":[{\"id\":\"FPUBLIC\"}]}");
        }),"xapp-PUBLIC","xoxb-PUBLIC");
        for(var i=0;i<4;i++)await SlackFileDelivery.Step(store,api,CancellationToken.None);
        Check(calls==3&&store.ClaimResultFile(now) is null,"one ticket upload completion; terminal no resend");
        Check(store.QueueSavedOcrText(job)=="queued"&&store.ClaimResultFile(now) is null,"saved result deduplicates sent content without OCR rerun");
        Check(SlackResultReturnRequests.Submit(options,job)=="queued_for_host","operator request uses file protocol not second DB writer");
        SlackResultReturnRequests.Step(options,store);
        Check(File.Exists(Path.Combine(root,"result-requests",job+".json.receipt"))&&SlackResultReturnRequests.Submit(options,job)=="existing_request_observe_only","durable operator receipt");
        foreach(var bad in new[]{"https://evil.invalid/upload/v1/X","http://files.slack.com/upload/v1/X","https://files.slack.com:444/upload/v1/X","https://files.slack.com/files-pri/X"}){
            try{SlackApi.ValidateResultUploadUrl(bad);throw new Exception("unsafe URL accepted");}catch(UnauthorizedAccessException){checks++;}
        }
        var lost=Prepare("LOST");var lostCalls=0;
        using var lostApi=new SlackApi(new Handler(request=>{lostCalls++;return Task.FromException<HttpResponseMessage>(new HttpRequestException("PUBLIC lost response"));}),"xapp-PUBLIC","xoxb-PUBLIC");
        await SlackFileDelivery.Step(store,lostApi,CancellationToken.None);store.Recover();await SlackFileDelivery.Step(store,lostApi,CancellationToken.None);
        Check(lostCalls==1&&store.GetJob(lost).Phase=="complete","ambiguous request retained; no retry or false OCR failure");
        try{store.RetryRejectedOcrTicket(lost);throw new Exception("uncertain reset");}catch(InvalidDataException){checks++;}
        var rejected=Prepare("REJECTED");using var rejectApi=new SlackApi(new Handler(_=>Task.FromResult(Json("{\"ok\":false,\"error\":\"invalid_arguments\"}"))),"xapp-PUBLIC","xoxb-PUBLIC");
        await SlackFileDelivery.Step(store,rejectApi,CancellationToken.None);
        Check(store.RetryRejectedOcrTicket(rejected)=="queued","operator may recover only known rejected pre-upload ticket");
        var same=store.ClaimResultFile(now)!;Check(same.JobId==rejected&&same.Stage=="ticket"&&same.RemoteId is null&&Encoding.UTF8.GetString(same.Bytes)==raw,"recovery freezes original content and destination");store.HoldResultFile(same,false,"public_fixture_hold");
        Check(store.Cleanup(now.AddDays(31),false)==1,"uncertain file preserves its session while sent session expires");
        var retry=Prepare("RATE");using var rateApi=new SlackApi(new Handler(_=>{
            var response=new HttpResponseMessage(HttpStatusCode.TooManyRequests);response.Headers.RetryAfter=new(TimeSpan.FromSeconds(60));return Task.FromResult(response);
        }),"xapp-PUBLIC","xoxb-PUBLIC");
        await SlackFileDelivery.Step(store,rateApi,CancellationToken.None);
        Check(store.ClaimResultFile(now) is null,"known rate limit defers without ambiguous replay");
        Console.WriteLine(JsonSerializer.Serialize(new{slack_file_public_checks=checks,status="PASS",real_slack=false,private_data=false,model_or_ocr_runs=0}));
    }
    private static HttpResponseMessage Json(string value)=>new(HttpStatusCode.OK){Content=new StringContent(value,Encoding.UTF8,"application/json")};
}
