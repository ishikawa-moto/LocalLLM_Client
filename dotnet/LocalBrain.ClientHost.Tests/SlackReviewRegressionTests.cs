using System.Net;
using System.Text.Json;
using LocalBrain.ClientHost.Ocr;
using LocalBrain.ClientHost.Slack;

internal static class SlackReviewRegressionTests
{
    internal static async Task RunAsync()
    {
        var checks=0;void Check(bool ok,string name){if(!ok)throw new Exception("SLACK_REVIEW_REGRESSION: "+name);checks++;}
        var root=Directory.CreateTempSubdirectory("slack-review-public-").FullName;var now=DateTimeOffset.UtcNow;
        SlackOptions Options(string name)=>new(true,Path.Combine(root,name),"TTEST","CTEST","UBOT",Path.Combine(root,"app"),Path.Combine(root,"bot"));
        string Accept(SlackStore store,string id,string thread="public-thread",string prompt="PUBLIC question",string[]? files=null)=>store.Accept(new(id,"TTEST","CTEST","UALICE",now.ToUnixTimeSeconds()+".000000",thread,prompt,files??[],now),now.AddMinutes(-1),now).JobId!;
        void Ack(SlackStore store){while(store.ClaimReply(now.AddMinutes(1)) is {} reply)store.ReplySent(reply.Id,"1700000000.000001");}
        void Ocr(SlackStore store,string job,string file,string text){store.SaveAttachment(job,file,".png",[1,2,3]);store.SaveOcrResult(job,file,JsonSerializer.Serialize(new{text}));}
        Check(SlackStore.SafeText("<@UBOB> OK")=="&lt;@UBOB> OK","mention escape preserves complete short answer");
        Check(SlackStore.SafeText("<!here> and <@UBOB> DONE")=="&lt;!here> and &lt;@UBOB> DONE","both mention types preserve suffix");
        var clipped=SlackStore.SafeText(new string('x',2999)+"😀 ending");Check(clipped.Length==2999&&!char.IsSurrogate(clipped[^1]),"truncation never splits a Unicode pair");
        Check(SlackStore.SafeText(new string('x',4000)).Length==3000,"escaped reply still obeys output bound");
        var oldOptions=Options("destination");string replyId;
        using(var store=new SlackStore(oldOptions)){var id=Accept(store,"EV-DEST");replyId=id+"-accepted";}
        using(var changed=new SlackStore(oldOptions with{ChannelId="COTHER"})){
            try{changed.ClaimReply(now);throw new Exception("wrong destination admitted");}catch(InvalidDataException e){Check(e.Message=="SLACK_REPLY_DESTINATION_BINDING","old frozen destination is refused");}
        }
        using(var original=new SlackStore(oldOptions)){var reply=original.ClaimReply(now);Check(reply is {Channel:"CTEST",State:"pending",Attempts:0}&&reply.Id==replyId,"refusal leaves original pending destination and attempts unchanged");}
        using(var store=new SlackStore(Options("scheduling"))){
            var held=Accept(store,"EV-UNCERTAIN","uncertain");var accepted=store.ClaimReply(now)!;store.ReplyUncertain(accepted.Id);
            Check(!store.HasPendingJobs()&&store.ClaimNext() is null,"uncertain acceptance does not block unrelated maintenance");
            var pending=Accept(store,"EV-ACK-WAIT","ack-wait");Check(!store.HasPendingJobs(),"unsent acceptance is not runnable work");Ack(store);
            Check(store.HasPendingJobs(),"sent acceptance is runnable work");Check(store.ClaimNext()!.Id==pending&&store.HasPendingJobs(),"running work still blocks maintenance");
            store.WaitForSummary(pending);Check(!store.HasPendingJobs(),"summary waiter in another session can run maintenance");
            Check(SlackConversation.NeedsCompression(store,store.GetSession(store.GetJob(pending).SessionId),8000),"summary waiter overrides small history threshold");
            Check(store.GetJob(held).Phase=="queued","uncertain source stays pending without replay");
            var waiting=Accept(store,"EV-OCR-WAIT","ocr-wait",files:["FPUBLIC"]);Ack(store);Check(store.ClaimNext()!.Id==waiting,"other acknowledged session remains runnable");store.WaitForOcr(waiting);
            Check(store.HasPendingJobs(),"waiting OCR still blocks competing maintenance");
        }
        using(var store=new SlackStore(Options("ocr-threshold"))){
            var id=Accept(store,"EV-LARGE-OCR",files:["FPREVIOUS"]);Ack(store);store.ClaimNext();Ocr(store,id,"FPREVIOUS",new string('x',18000));store.Finish(id,"PUBLIC OCR references",true,true);Ack(store);
            var follow=Accept(store,"EV-FOLLOW");Ack(store);var job=store.ClaimNext()!;var session=store.GetSession(job.SessionId);
            Check(SlackConversation.Bytes(JsonSerializer.Serialize(store.History(session.Id)))<8000,"large OCR has short job metadata");
            Check(SlackConversation.Context(store,session,store.Snapshot(session.Id).Jobs.Where(x=>x.Phase=="complete").ToArray(),job.Text,8000) is null,"retained OCR requires summary");
            store.WaitForSummary(follow);Check(SlackConversation.NeedsCompression(store,store.GetSession(session.Id),8000),"18KB transcription follow-up requires a real hard-limit summary");
            Check(store.FailWaitingSummary(session.Id)==1&&store.GetJob(follow).Phase=="failed","unavailable compressor can now finish this waiter explicitly");
        }
        using(var store=new SlackStore(Options("ocr-within-hard-limit"))){
            var id=Accept(store,"EV-FITTING-OCR",files:["FPREVIOUS"]);Ack(store);store.ClaimNext();Ocr(store,id,"FPREVIOUS",new string('x',14000));store.Finish(id,"PUBLIC OCR references",true,true);Ack(store);
            var next=Accept(store,"EV-FITTING-FOLLOW");Ack(store);var job=store.ClaimNext()!;var session=store.GetSession(job.SessionId);
            var context=SlackConversation.Context(store,session,store.Snapshot(session.Id).Jobs.Where(x=>x.Phase=="complete").ToArray(),job.Text,8000);
            Check(context is not null&&SlackConversation.Fits(context,job.Text),"14KB OCR inside hard budget must not wait for soft-threshold summary");
            using var parsed=JsonDocument.Parse(context!);Check(parsed.RootElement.GetProperty("latest_ocr")[0].GetProperty("ocr_text").GetString()!.Length==14000,"direct context retains every OCR character");
        }
        var options=Options("document-history");using(var store=new SlackStore(options)){
            var old=Accept(store,"EV-PREVIOUS",prompt:"PUBLIC preserve the stated comparison constraint",files:["FPREVIOUS"]);Ack(store);store.ClaimNext();Ocr(store,old,"FPREVIOUS","PUBLIC previous document");store.Finish(old,"PUBLIC earlier answer",true,true);Ack(store);
            var current=Accept(store,"EV-COMPARE",prompt:"PUBLIC compare this image with the previous one",files:["FCURRENT"]);Ocr(store,current,"FCURRENT","PUBLIC current document");Ack(store);
            using var api=new SlackApi(new Handler(_=>throw new Exception("No Slack HTTP expected")),"xapp-public","xoxb-public");
            var ocr=new OcrConfig(true,Path.Combine(root,"ocr"),Path.Combine(root,"assets"),Path.Combine(root,"assets","python.exe"),Path.Combine(root,"assets","worker.py"),Path.Combine(root,"assets","models"),[root],new(){["a"]="a",["b"]="b",["c"]="c"},["AUTO","PDF_DOCUMENT"],[],512L<<20,512L<<20,384L<<20,30,new string('a',64));
            var inference=new Capture();var processor=new SlackProcessor(options,store,api,inference,ocr,SlackPriority.ForFixture(ocr,()=>new(long.MaxValue,long.MaxValue,0)));
            using var stop=new CancellationTokenSource(300);try{await processor.Run(stop.Token);}catch(OperationCanceledException)when(stop.IsCancellationRequested){}
            Check(store.GetJob(current).Phase=="complete"&&inference.Calls==1,"document follow-up completes through real processor");
            using var parsed=JsonDocument.Parse(inference.Context!);var history=parsed.RootElement.GetProperty("history");var currentOcr=parsed.RootElement.GetProperty("current_document");
            Check(history.GetProperty("latest_ocr")[0].GetProperty("ocr_text").GetString()=="PUBLIC previous document","previous document reaches paired inference");
            Check(history.GetProperty("history")[0].GetProperty("user").GetString()!.Contains("comparison constraint",StringComparison.Ordinal),"earlier conversation constraints preserved");
            Check(currentOcr.GetProperty("untrusted_ocr").GetString()=="PUBLIC current document"&&SlackConversation.Fits(inference.Context!,inference.Prompt!),"current OCR and history share final byte budget");
            var next=Accept(store,"EV-BUDGET",prompt:"PUBLIC analyze",files:["FNEXT"]);Ocr(store,next,"FNEXT",new string('x',13000));Ack(store);var job=store.ClaimNext()!;
            var budget=SlackConversation.JobBudget(store,job);store.WaitForSummary(next,budget);
            Check(budget<4096&&store.WaitingSummaryBudget(job.SessionId)==budget,"summary budget accounts for pending document and metadata");
            store.SaveSummary(job.SessionId,new string('s',budget),store.HistoryHash(job.SessionId));
            var packed=SlackConversation.Context(store,store.GetSession(job.SessionId),store.History(job.SessionId).Where(x=>x.Phase=="complete").ToArray(),job.Text,8000,SlackConversation.CurrentDocument(store,job));
            Check(packed is not null&&SlackConversation.Fits(packed,job.Text),"bound summary and large new document fit without truncation");
        }
        using(var store=new SlackStore(Options("unacknowledged-maintenance"))){
            Accept(store,"EV-NOT-ACKNOWLEDGED");var accepted=store.ClaimReply(now)!;store.ReplyUncertain(accepted.Id);
            using var api=new SlackApi(new Handler(_=>throw new Exception("No Slack HTTP expected")),"xapp-public","xoxb-public");
            var ocr=new OcrConfig(true,Path.Combine(root,"maintenance-ocr"),Path.Combine(root,"assets"),Path.Combine(root,"assets","python.exe"),Path.Combine(root,"assets","worker.py"),Path.Combine(root,"assets","models"),[root],new(){["a"]="a",["b"]="b",["c"]="c"},["AUTO","PDF_DOCUMENT"],[],512L<<20,512L<<20,384L<<20,30,new string('a',64));
            var inference=new Capture();var processor=new SlackProcessor(Options("unacknowledged-maintenance"),store,api,inference,ocr,SlackPriority.ForFixture(ocr,()=>new(long.MaxValue,long.MaxValue,0)));
            using var stop=new CancellationTokenSource(150);try{await processor.Maintenance(stop.Token);}catch(OperationCanceledException)when(stop.IsCancellationRequested){}
            Check(inference.Calls==0&&store.UncertainReplies().Length==1,"maintenance neither reviews unacknowledged input nor replays uncertain receipt");
        }
        var script=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../tools/slack/Set-ApprovalPassword.ps1"));
        Check(File.ReadLines(script).First().Trim()=="#Requires -Version 7.2","password UI rejects unsupported PowerShell before asking for secrets");
        Console.WriteLine(JsonSerializer.Serialize(new{slack_review_regression_checks=checks,status="PASS",real_slack=false,real_ocr=false,real_model=false}));
    }
    private sealed class Capture:ISlackLocalInference
    {
        internal int Calls;internal string? Context,Prompt;
        public Task<string> Chat(string requestId,string session,string context,string prompt,int outputTokens,int seconds,CancellationToken token){Calls++;Context=context;Prompt=prompt;return Task.FromResult("PUBLIC comparison");}
        public Task<SlackReviewedCandidate?> Candidate(string session,string source,CancellationToken token)=>throw new Exception("No review expected");
        public Task<string> RegisterProposal(string id,string reviewRequestId,string session,CancellationToken token)=>throw new Exception("No proposal expected");
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> action):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(action(request));}
}
