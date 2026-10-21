using System.Net;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.Slack;
using LocalBrain.ClientHost.Ocr;

internal static class SlackConversationTests
{
    internal static async Task RunAsync()
    {
        var checks=0;void Check(bool ok,string name){if(!ok)throw new Exception("SLACK_CONVERSATION_TEST: "+name);checks++;}
        var root=Directory.CreateTempSubdirectory("slack-summary-public-").FullName;
        var options=new SlackOptions(true,Path.Combine(root,"store"),"TTEST","CTEST","UBOT",Path.Combine(root,"app"),Path.Combine(root,"bot"));
        var now=DateTimeOffset.UtcNow;var thread=now.ToUnixTimeSeconds()+".000000";
        string Accept(SlackStore s,string id,string text="PUBLIC question",string[]? files=null)=>s.Accept(new(id,"TTEST","CTEST","UALICE",thread,thread,text,files??[],now),now.AddMinutes(-1),now).JobId!;
        void Ack(SlackStore s){while(s.ClaimReply(now.AddMinutes(1)) is {} r)s.ReplySent(r.Id,"1700000000.000001");}
        var calls=0;var maxBytes=0;var stop=false;
        Task<string> Summary(string source,int budget,CancellationToken token){calls++;maxBytes=Math.Max(maxBytes,SlackConversation.Bytes(source));Check(SlackConversation.Bytes(source)<=SlackConversation.BatchBytes,"bounded chunk");using var document=JsonDocument.Parse(source);return Task.FromResult("PUBLIC summarized source IDs and constraints");}
        string session;
        using(var store=new SlackStore(options)){
            for(var i=0;i<60;i++){var id=Accept(store,"EV-SEED-"+i);Ack(store);Check(store.ClaimNext()?.Id==id,"same-session order");store.Finish(id,new string('x',3990),true,false);Ack(store);}
            session=store.Sessions().Single().Id;
            var original=store.HistoryHash(session);var pending=Accept(store,"EV-WAIT");Ack(store);store.ClaimNext();store.WaitForSummary(pending);
            Check(!await SlackConversation.Compress(store,session,4096,(source,budget,t)=>{stop=true;return Summary(source,budget,t);},()=>!stop,CancellationToken.None),"yield after checkpoint");
            Check(store.SummaryCheckpoint(session) is {Count:>0},"durable progress");
            Check(store.GetJob(pending).Phase=="waiting_summary","wait preserves source job");
            Check(store.HistoryHash(session)==original,"checkpoint leaves original history unchanged");
        }
        using(var store=new SlackStore(options)){
            var completed=store.SummaryCheckpoint(session)!.Count;
            Check(await SlackConversation.Compress(store,session,4096,Summary,()=>true,CancellationToken.None),"restart finishes long history");
            Check(calls>1&&maxBytes<=65536,"long history has multiple bounded calls");
            Check(store.SummaryCheckpoint(session)!.Count>completed,"restart continues prefix");
            Check(store.History(session).Last().Phase=="queued","summary wakes waiting head");
            var before=calls;Check(await SlackConversation.Compress(store,session,4096,Summary,()=>true,CancellationToken.None)&&calls==before,"finished prefix does not infer again");
            var job=store.ClaimNext()!;store.Finish(job.Id,"PUBLIC next",true,false);Ack(store);
            Check(await SlackConversation.Compress(store,session,4096,Summary,()=>true,CancellationToken.None)&&calls==before+1,"new suffix reuses prior prefix");
            var history=store.History(session).Where(x=>x.Phase=="complete").ToArray();
            var hash=store.HistoryHash(session);store.SaveSummary(session,new string('s',8000),hash);
            Check(SlackConversation.Context(store,store.GetSession(session),history,"PUBLIC",8000) is null,"old large summary requires compaction");
            Check(await SlackConversation.Compress(store,session,4096,Summary,()=>true,CancellationToken.None),"compact via verified checkpoint");
            var prompt=new string('問',3900);var budget=SlackConversation.Budget(prompt);
            store.SaveSummary(session,new string('s',budget),hash);
            var context=SlackConversation.Context(store,store.GetSession(session),history,prompt,8000)!;
            Check(context is not null&&SlackConversation.Fits(context,prompt),"packed summary includes exact prompt plus LF budget");
            Check(context!.Contains("history_is_summary",StringComparison.Ordinal),"omitted originals explicitly represented by bound summary");
            Check(SlackConversation.Fits("",new string('x',15999))&&!SlackConversation.Fits("",new string('x',16000)),"shared 16000 byte LF boundary");
            var file=Accept(store,"EV-OCR","PUBLIC OCR",["FPUBLIC"]);Ack(store);store.ClaimNext();
            var attachment=store.SaveAttachment(file,"FPUBLIC",".png",[1,2,3]);store.SaveOcrResult(file,"FPUBLIC",JsonSerializer.Serialize(new{text="PUBLIC OCR 😀"}));store.Finish(file,"PUBLIC refs",true,true);Ack(store);
            var records=SlackConversation.Records(store,store.Snapshot(session).Jobs);
            Check(records.Any(r=>r.Contains(attachment.Hash,StringComparison.Ordinal)&&JsonDocument.Parse(r).RootElement.GetProperty("text").GetString()=="PUBLIC OCR 😀"),"summary includes retained OCR text and source hash");
            store.SaveSummaryCheckpoint(session,1,new string('0',64),"UNTRUSTED STALE",store.HistoryHash(session));
            var sawStale=false;await SlackConversation.Compress(store,session,4096,(source,b,t)=>{sawStale|=source.Contains("UNTRUSTED STALE",StringComparison.Ordinal);return Summary(source,b,t);},()=>true,CancellationToken.None);
            Check(!sawStale,"mismatched prefix never reused");
            var id=Accept(store,"EV-TRANSPORT");Ack(store);
            using var restricted=RestrictedExecution.EnterSlack();
            foreach(var fixture in new[]{"{\"status\":\"deferred\",\"retry_safe\":true}","{\"status\":\"deferred\",\"retry_safe\":false}","{\"status\":\"busy\"}"}){
                using var gateway=new GatewayClient(new Uri("https://public-fixture.invalid"),new Handler(_=>new(HttpStatusCode.OK){Content=new StringContent(fixture)}));
                try{await new SlackLocalInference(gateway,store).Chat(id,session,"","PUBLIC",64,5,CancellationToken.None);throw new Exception("accepted noncomplete");}
                catch(SlackDeferred){Check(fixture.Contains("true",StringComparison.Ordinal),"retry only explicit safe defer");}
                catch(InvalidDataException){Check(!fixture.Contains("true",StringComparison.Ordinal),"uncertain defer is terminal");}
            }
            using(var gateway=new GatewayClient(new Uri("https://public-fixture.invalid"),new Handler(_=>throw new Exception("oversize input reached transport")))){
                try{await new SlackLocalInference(gateway,store).Chat("oversize-public",session,"[]",new string('x',15998),64,5,CancellationToken.None);throw new Exception("accepted 16001");}catch(InvalidDataException){checks++;}
            }
            var changed=Accept(store,"EV-BAD-SUMMARY");Ack(store);var newHash=store.HistoryHash(session);
            store.SaveSummaryCheckpoint(session,0,SlackHash.Of("slack-summary-v2"),"",newHash);
            try{await SlackConversation.Compress(store,session,256,(source,b,t)=>Task.FromResult(new string('x',257)),()=>true,CancellationToken.None);throw new Exception("oversize summary accepted");}catch(InvalidDataException){checks++;}
        }
        foreach(var safe in new[]{true,false}){
            using var store=new SlackStore(options with{Root=Path.Combine(root,"transport-"+safe)});
            var id=Accept(store,"EV-RETRY");Ack(store);var sent=new List<string>();
            using var gateway=new GatewayClient(new Uri("https://public-fixture.invalid"),new Handler(request=>{
                sent.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return !safe?new(HttpStatusCode.ServiceUnavailable){Content=new StringContent("{\"error\":\"resource_limit_exceeded\"}")}:
                    new(HttpStatusCode.OK){Content=new StringContent(sent.Count==1?"{\"status\":\"deferred\",\"retry_safe\":true,\"reason\":\"resource_limit_exceeded\"}":"{\"status\":\"complete\",\"text\":\"PUBLIC reply\"}")};
            }));
            using var api=new SlackApi(new Handler(_=>throw new Exception("Unexpected Slack HTTP")),"xapp-public","xoxb-public");
            var ocr=new OcrConfig(true,Path.Combine(root,"ocr"),Path.Combine(root,"assets"),Path.Combine(root,"assets","python.exe"),Path.Combine(root,"assets","worker.py"),Path.Combine(root,"assets","models"),[root],new(){["a"]="a",["b"]="b",["c"]="c"},["AUTO","PDF_DOCUMENT"],[],512L<<20,512L<<20,384L<<20,30,new string('a',64));
            var processor=new SlackProcessor(options,store,api,new SlackLocalInference(gateway,store),ocr,SlackPriority.ForFixture(ocr,()=>new(long.MaxValue,long.MaxValue,0)));
            using var stopToken=new CancellationTokenSource(1500);
            try{await processor.Run(stopToken.Token);}catch(OperationCanceledException)when(stopToken.IsCancellationRequested){}
            Check(store.GetJob(id).Phase==(safe?"complete":"failed"),"processor retries safe pre-reservation defer only");
            Check(safe?sent.Count==2&&sent[0]==sent[1]:sent.Count==1,"same frozen request reused; ambiguous HTTP failure never replayed");
        }
        Console.WriteLine(JsonSerializer.Serialize(new{slack_conversation_checks=checks,status="PASS",summary_calls=calls,max_batch_bytes=maxBytes,real_model=false,real_slack=false}));
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> action):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(action(request));}
}
