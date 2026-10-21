using System.Text.Json;
using LocalBrain.ClientHost.Slack;

// Public, isolated durable-store/CTC-independent fixtures. No Slack API or model calls.
internal static class SlackSharedThreadTests
{
    internal static async Task RunAsync()
    {
        var checks=0;
        void Check(bool ok,string name){if(!ok)throw new Exception("SLACK_SHARED_THREAD_TEST: "+name);checks++;}
        var root=Directory.CreateTempSubdirectory("localbrain-shared-thread-public-").FullName;
        var options=new SlackOptions(true,root,"TTEST","CTEST","UBOT",Path.Combine(root,"app"),Path.Combine(root,"bot"));
        var now=DateTimeOffset.UtcNow;var online=now.AddMinutes(-1);var thread=now.ToUnixTimeSeconds()+".000000";
        SlackMention Mention(string id,string user,string text,string[]? files=null,string? otherThread=null)=>
            new(id,"TTEST","CTEST",user,thread,otherThread??thread,text,files??[],now);
        void Ack(SlackStore store){while(store.ClaimReply(now.AddMinutes(1)) is {} reply)store.ReplySent(reply.Id,"1700000000.000001");}
        string session;
        using(var store=new SlackStore(options)){
            var a=store.Accept(Mention("EV-IMAGE-A","UALICE","PUBLIC image explanation",["FPUBLIC"]),online,now);
            Check(a.Status=="queued","A image accepted");Ack(store);
            var first=store.ClaimNext()!;session=first.SessionId;store.WaitForOcr(first.Id);
            // The platform supplies the actor and root thread; B does not impersonate A.
            var payload=JsonSerializer.SerializeToElement(new{envelope_id="public-b-envelope",type="events_api",payload=new{type="event_callback",event_id="EV-FOLLOW-B",team_id="TTEST",@event=new{type="app_mention",channel="CTEST",user="UBOB",text="<@UBOT> PUBLIC ask about the image",ts=thread,thread_ts=thread}}});
            var acknowledged=0;
            await SlackSocket.ProcessEnvelope(payload,options,store,online,(_,_)=>{acknowledged++;return Task.CompletedTask;},CancellationToken.None);
            var second=store.History(session).Single(x=>x.EventId=="EV-FOLLOW-B");
            Check(acknowledged==1&&second.User=="UBOB","B Socket Mode event accepted with actual actor");
            Check(second.SessionId==first.SessionId&&second.FileIds.Length==0,"same thread reuses session without reattaching image");
            Ack(store);Check(store.ClaimNext() is null,"B waits for A OCR turn");
            store.SaveAttachment(first.Id,"FPUBLIC",".png",[1,2,3]);
            store.BindOcr(first.Id,"FPUBLIC","public-ocr-fixture");
            store.SaveOcrResult(first.Id,"FPUBLIC",JsonSerializer.Serialize(new{text="PUBLIC IMAGE AMOUNT 120"}));
            store.OcrReady(first.Id);Check(store.ClaimNext()?.Id==first.Id,"A OCR turn resumes first");
            store.Finish(first.Id,"PUBLIC A result",true,true);Ack(store);
            Check(store.ClaimNext()?.Id==second.Id,"B becomes claimable after A finishes");
            var history=store.History(session).Where(x=>x.Phase=="complete").ToArray();
            var packed=SlackConversation.Context(store,store.GetSession(session),history,second.Text,8000)!;
            using(var context=JsonDocument.Parse(packed)){
                Check(context.RootElement.GetProperty("history")[0].GetProperty("user_id").GetString()=="UALICE","context attributes original image turn to A");
                Check(context.RootElement.GetProperty("latest_ocr")[0].GetProperty("ocr_text").GetString()=="PUBLIC IMAGE AMOUNT 120","B receives retained A OCR without another OCR call");
            }
            store.Finish(second.Id,"PUBLIC answer to B",true,false);
            var reply=store.ClaimReply(now)!;
            Check(reply.Text.Contains("<@UBOB>")&&!reply.Text.Contains("<@UALICE>")&&reply.Thread==thread,"completion mentions B in the original thread");store.ReplySent(reply.Id,"1700000000.000002");
            Check(store.Accept(Mention("EV-FOLLOW-B","UBOB","PUBLIC repeated"),online,now).Status=="duplicate","B retry does not enqueue a second job");
            Check(store.GetSession(session).Owner=="UALICE","creator metadata retained without granting exclusive access");
            var third=store.Accept(Mention("EV-FOLLOW-C","UCAROL","PUBLIC another question"),online,DateTimeOffset.UtcNow);
            Check(third.Status=="queued"&&store.GetJob(third.JobId!).SessionId==session,"third participant shares the thread");Ack(store);
            Check(store.ClaimNext()?.Id==third.JobId,"third participant preserves FIFO");store.Finish(third.JobId!,"PUBLIC C result",true,false);Ack(store);
            var records=SlackConversation.Records(store,store.History(session).ToArray()).Select(x=>JsonSerializer.Deserialize<JsonElement>(x)).ToArray();
            Check(records.Where(x=>x.GetProperty("kind").GetString()=="user").Select(x=>x.GetProperty("user_id").GetString()).SequenceEqual(new[]{"UALICE","UBOB","UCAROL"}),"summary records retain all three authors");
            var summarySource="";
            Check(await SlackConversation.Compress(store,session,4096,(source,_,_)=>{summarySource+=source;return Task.FromResult("PUBLIC summary of A image, B and C questions");},()=>true,CancellationToken.None),"multi-user summary completes locally");
            Check(summarySource.Contains("UALICE")&&summarySource.Contains("UBOB")&&summarySource.Contains("UCAROL")&&summarySource.Contains("PUBLIC IMAGE AMOUNT 120"),"compression sees authors and same-thread OCR");
            var other=store.Accept(Mention("EV-OTHER-THREAD","UBOB","PUBLIC separate topic",otherThread:now.ToUnixTimeSeconds()+".000001"),online,now);
            var otherJob=store.GetJob(other.JobId!);
            Check(otherJob.SessionId!=session,"another thread has a separate session");
            var isolated=SlackConversation.Context(store,store.GetSession(otherJob.SessionId),[],otherJob.Text,8000);
            Check(isolated=="","other thread cannot inherit A image or thread history");
            Check(store.Accept(Mention("EV-OTHER-CHANNEL","UBOB","PUBLIC") with{ChannelId="COTHER"},online,now).Status=="offline_or_foreign","channel boundary retained");
            Check(store.Accept(Mention("EV-OTHER-TEAM","UBOB","PUBLIC") with{TeamId="TOTHER"},online,now).Status=="offline_or_foreign","workspace boundary retained");
            var proposal=store.Propose(session,"knowledge","PUBLIC fact","PUBLIC source","PUBLIC review",now);
            try{store.Authorize(proposal.Id,proposal.Hash,"UBOB",false,"approve",now);throw new Exception("Member gained approval rights");}catch(UnauthorizedAccessException){checks++;}
        }
        using(var store=new SlackStore(options)){
            var accepted=store.Accept(Mention("EV-REOPEN-B","UBOB","PUBLIC continue after restart"),online,now);
            Check(accepted.Status=="queued"&&store.GetJob(accepted.JobId!).SessionId==session,"existing thread accepts B after reopening without a migration");
        }
        Console.WriteLine(JsonSerializer.Serialize(new{status="PASS",slack_shared_thread_checks=checks,real_slack=false,real_model=false}));
    }
}
