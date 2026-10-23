using System.Net;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.Slack;

internal static class SlackArtifactDeliveryTests
{
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> call):HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t)=>call(r); }
    private static HttpResponseMessage Json(string s)=>new(HttpStatusCode.OK){Content=new StringContent(s,Encoding.UTF8,"application/json")};
    internal static async Task RunAsync()
    {
        int checks=0;void Check(bool value,string name){if(!value)throw new Exception("SLACK_ARTIFACT_DELIVERY: "+name);checks++;}
        var id=new string('a',32);var calls=0;
        using(var api=new SlackApi(new Handler(_=>{calls++;return Task.FromResult(Json("{\"ok\":true,\"file_id\":\"FPUBLIC\",\"upload_url\":\"https://files.slack.com/upload/v1/PUBLIC\"}"));}),"xapp-PUBLIC","xoxb-PUBLIC")){
            foreach(var filename in new[]{"ocr-"+id+".txt","ocr-"+id+".md","analysis-"+id+".txt","ocr-json-"+id+".json","ocr-html-"+id+".html","ocr-csv-"+id+".csv"}){
                var before=calls;await api.ResultUploadTicket(filename,12,CancellationToken.None);Check(calls==before+1,"supported filename reaches ticket once: "+filename);
            }
            foreach(var filename in new[]{"ocr-"+id+".html","ocr-json-"+id+".md","ocr-html-"+id+".json","ocr-csv-"+id+".exe","../ocr-"+id+".md","ocr-"+id+".md\n","ocr-"+id.ToUpperInvariant()+".md","ocr-"+id+".MD"}){
                var before=calls;try{await api.ResultUploadTicket(filename,12,CancellationToken.None);throw new Exception("unsafe filename accepted");}catch(InvalidDataException){Check(calls==before,"invalid filename never dispatched");}
            }
        }
        var root=Directory.CreateTempSubdirectory("lb-public-all-artifacts-").FullName;
        var options=new SlackOptions(true,root,"TTEST","CTEST","UBOT",Path.Combine(root,"app"),Path.Combine(root,"bot"),ReturnFileResults:true);
        using var store=new SlackStore(options);var now=DateTimeOffset.UtcNow;var ts=now.ToUnixTimeSeconds()+".000001";
        var job=store.Accept(new("EV-PUBLIC-ARTIFACTS","TTEST","CTEST","UALICE",ts,ts,"文字起こしを HTML CSV JSON で返してください",["FPUBLIC"],now),now.AddSeconds(-1),now).JobId!;
        while(store.ClaimReply(now) is{} ack)store.ReplySent(ack.Id,ts);store.ClaimNext();store.SaveAttachment(job,"FPUBLIC",".png",[1,2,3]);
        store.SaveOcrResult(job,"FPUBLIC",JsonSerializer.Serialize(new{text="PUBLIC",pages=new[]{new{page=0,spans=Array.Empty<object>(),tables=new[]{new{table_id="public",rows=1,columns=1,cells=new[]{new{row_index=0,column_index=0,row_span=1,column_span=1,text="1,234"}}}}}}}));
        store.Finish(job,"{\"output_kind\":\"ocr_text\"}",true,true);
        var tickets=0;var uploads=0;var completions=0;var names=new HashSet<string>();var payloads=new HashSet<string>();
        using var delivery=new SlackApi(new Handler(async request=>{
            if(request.RequestUri!.Host=="files.slack.com"){
                uploads++;Check(request.Headers.Authorization is null,"upload has no bot secret");Check(payloads.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await request.Content!.ReadAsByteArrayAsync()))),"one upload per frozen payload");return new(HttpStatusCode.OK){Content=new StringContent("OK")};
            }
            Check(request.Headers.Authorization?.Parameter=="xoxb-PUBLIC","only official API gets bot token");
            if(request.RequestUri.AbsolutePath.EndsWith("getUploadURLExternal")){
                var form=(await request.Content!.ReadAsStringAsync()).Split('&').Select(x=>x.Split('=',2)).ToDictionary(x=>Uri.UnescapeDataString(x[0]),x=>Uri.UnescapeDataString(x[1]));
                Check(names.Add(form["filename"]),"unique ticket per filename");tickets++;return Json("{\"ok\":true,\"file_id\":\"FPUBLIC"+tickets+"\",\"upload_url\":\"https://files.slack.com/upload/v1/PUBLIC"+tickets+"\"}");
            }
            var body=JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsByteArrayAsync());
            Check(body.GetProperty("channel_id").GetString()=="CTEST"&&body.GetProperty("thread_ts").GetString()==ts,"all completions use original source thread");
            completions++;return Json("{\"ok\":true,\"files\":[{\"id\":\"FPUBLIC"+completions+"\"}]}");
        }),"xapp-PUBLIC","xoxb-PUBLIC");
        for(var i=0;i<16;i++)await SlackFileDelivery.Step(store,delivery,CancellationToken.None);
        Check(tickets==4&&uploads==4&&completions==4,"MD JSON HTML CSV each completes once");
        Check(names.Select(Path.GetExtension).ToHashSet().SetEquals(new[]{".md",".json",".html",".csv"}),"requested artifact formats delivered");
        store.Recover();store.QueueSavedOcrText(job);for(var i=0;i<4;i++)await SlackFileDelivery.Step(store,delivery,CancellationToken.None);
        Check(tickets==4&&uploads==4&&completions==4&&store.ClaimResultFile(DateTimeOffset.UtcNow) is null,"restart and repeated return do not resend completed artifacts");
        Console.WriteLine(JsonSerializer.Serialize(new{slack_artifact_delivery_checks=checks,status="PASS",real_slack=false,private_data=false}));
    }
}
