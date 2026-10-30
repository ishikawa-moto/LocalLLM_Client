using System.Net;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.AgentV2;
using LocalBrain.ClientHost.Slack;

internal static class SlackTests
{
    private static int checks;
    private static void Check(bool value,string name){if(!value)throw new Exception("SLACK_TEST_FAILED: "+name);checks++;}
    private static void Denied(Action action,string name){try{action();}catch(UnauthorizedAccessException){checks++;return;}throw new Exception("SLACK_TEST_FAILED: "+name);}
    private static SlackOptions Options(string root,int queue=100)=>new(true,root,"TTEST","CTEST","UBOT",Path.Combine(root,"app.token"),Path.Combine(root,"bot.token"),MaxQueue:queue);
    private static SlackMention Mention(string id,DateTimeOffset now,string thread="",string user="UALICE",string[]? files=null)=>new(id,"TTEST","CTEST",user,now.ToUnixTimeSeconds()+".000000",thread==""?now.ToUnixTimeSeconds()+".000000":thread,"<@UBOT> public synthetic fixture",files??[],now);
    private static void Ack(SlackStore s){while(s.ClaimReply(DateTimeOffset.UtcNow.AddDays(365)) is {} p)s.ReplySent(p.Id,"1700000000.000001");}
    internal static async Task RunAsync()
    {
        var root=Directory.CreateTempSubdirectory("localbrain-slack-public-").FullName;
        var now=DateTimeOffset.UtcNow;var online=now.AddMinutes(-1);
        using(var s=new SlackStore(Options(root))){
            var first=Mention("EV-FIRST",now);
            Check(s.Accept(first,online,now).Status=="queued","accept");
            Check(s.ClaimNext() is null,"work waits for acceptance delivery");
            var reply=s.ClaimReply(now)!;
            Check(reply.Text=="受け付けました。"&&reply.Thread==first.RootTs&&reply.Kind=="accepted","exact immediate thread receipt without mention");
            Check(s.Accept(first,online,now).Status=="duplicate"&&s.ClaimReply(now) is null,"dedup before receipt");
            s.ReplySent(reply.Id,"1700000000.000001");var j=s.ClaimNext()!;
            Check(j is not null,"claim after acceptance");
            Check(s.Accept(Mention("EV-OTHER",now.AddSeconds(1),first.RootTs,"UBOB") with{ChannelId="COTHER"},online,now.AddSeconds(1)).Status=="offline_or_foreign","channel isolation");
            s.Recover();Check(s.GetJob(j!.Id).Phase=="queued","accepted job recovery");
            Check(s.Accept(first,now.AddSeconds(2),now.AddSeconds(3)).Status=="duplicate","persisted job survives offline filter");
            Check(s.Accept(Mention("EV-OFFLINE",now.AddHours(-1)),online,now).Status=="offline_or_foreign","reject shutdown delayed event");
            s.ClaimNext();s.Finish(j.Id,"synthetic private OCR text",true,true);var done=s.ClaimReply(now)!;
            Check(done.Text.Contains("<@UALICE>")&&!done.Text.Contains("synthetic private")&&done.Thread==first.RootTs,"OCR full text stays local");
            s.ReplyUncertain(done.Id);s.Recover();Check(s.ClaimReply(now) is null,"unknown send never blindly replayed");s.ReplySent(done.Id,"1700000000.000002");
            Check(s.Accept(Mention("EV-CONTINUE",now.AddSeconds(4),first.RootTs),online,now.AddSeconds(4)).Status=="queued","continue");Ack(s);var cont=s.ClaimNext()!;s.Finish(cont.Id,"public <!channel> answer <@UOTHER>",true,false);var safe=s.ClaimReply(now)!;
            Check(!safe.Text.Contains("<!channel>")&&!safe.Text.Contains("<@UOTHER>"),"no broadcast or arbitrary user mention");s.ReplySent(safe.Id,"1700000000.000003");
            var hash=s.HistoryHash(j.SessionId);Check(s.SaveSummary(j.SessionId,"public summary",hash),"bound summary");Check(!s.SaveSummary(j.SessionId,"stale",new string('0',64)),"summary drift denied");
            var p=s.Propose(j.SessionId,"knowledge","public fact","public source","two local passes",now);
            Check(p==s.Propose(j.SessionId,"knowledge","public fact","public source","two local passes",now),"proposal duplicate");
            Denied(()=>s.Authorize(p.Id,p.Hash,"member",false,"approve",now),"member cannot approve");
            Denied(()=>s.Authorize(p.Id,new string('0',64),"admin",true,"approve",now),"hash mismatch");
            Check(s.Authorize(p.Id,p.Hash,"admin",true,"approve",now).State=="approved","human approval recorded, not adoption");
            Denied(()=>s.Authorize(p.Id,p.Hash,"admin",true,"approve",now),"approval replay");
            Check(s.GetJob(j.Id).Result=="synthetic private OCR text","local source retained");
        }
        using(var s=new SlackStore(Options(Path.Combine(root,"reopen")))){
            var m=Mention("EV-PERSIST",now);var j=s.Accept(m,online,now).JobId!;var r=s.ClaimReply(now)!;s.ReplyRetry(r.Id,now.AddMinutes(1));
            Check(s.ClaimReply(now) is null,"retry-after respected");Check(s.ClaimReply(now.AddMinutes(2)) is not null,"known rate limit retry");s.Recover();Check(s.ClaimReply(now.AddMinutes(2)) is null,"crash during send held");
        }
        using(var s=new SlackStore(Options(Path.Combine(root,"queue"),1))){
            s.Accept(Mention("EV-Q1",now),online,now);
            Check(s.Accept(Mention("EV-Q2",now.AddSeconds(1)),online,now.AddSeconds(1)).Status=="queue_full","queue capacity");
            Check(s.Accept(Mention("EV-Q3",now.AddSeconds(2)),online,now.AddSeconds(2)).Status=="queue_full","queue refusal coalesced");
            var r=s.ClaimReply(now)!;s.ReplySent(r.Id,"1700000000.000001");var error=s.ClaimReply(now)!;
            Check(error.Kind=="error"&&!error.Text.Contains("受け付けました"),"no false acceptance");
            s.Accept(Mention("EV-Q4",now.AddSeconds(3)),online,now.AddSeconds(3));
            s.ReplyUncertain(error.Id);
            Check(s.UncertainReplies().Any(x=>x.Id==error.Id),"uncertain refusal retained");
            Check(s.ClaimReply(now) is null,"uncertain refusal is not replayed");
            s.ReplySent(error.Id,"1700000000.000002");
            s.Accept(Mention("EV-Q5",now.AddSeconds(4)),online,now.AddSeconds(4));
            var manual=s.ClaimReply(now);Check(manual?.Kind=="error","delivered refusal row cleaned for next notice");
            s.ReplyUncertain(manual!.Id);
            s.ReconcileReply(manual.Id,SlackStore.ReplyHash(manual),"1700000000.000003","public-admin");
            using var rows=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"queue","slack.db"),Mode=Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,Pooling=false}.ToString());rows.Open();
            using var count=rows.CreateCommand();count.CommandText="SELECT count(*) FROM replies WHERE kind='error' AND job_id='';";
            Check(Convert.ToInt32(count.ExecuteScalar())==0,"human-confirmed refusal is erased in the confirmation transaction");
            Check(s.UncertainReplies().Length==0,"human-confirmed refusal is no longer uncertain");
        }
        var summaryOptions=Options(Path.Combine(root,"summary-wakeup"));string summaryJob;
        using(var s=new SlackStore(summaryOptions)){
            summaryJob=s.Accept(Mention("EV-SUMMARY-WAKE",now),online,now).JobId!;Ack(s);s.ClaimNext();var session=s.GetJob(summaryJob).SessionId;var hash=s.HistoryHash(session);
            Check(s.SaveSummary(session,"public summary",hash),"summary finishes before waiter transition");s.WaitForSummary(summaryJob);
            Check(s.GetJob(summaryJob).Phase=="queued","completed summary cannot lose its wake-up");
            s.DropSummaryCache();s.ClaimNext();s.WaitForSummary(summaryJob);Check(s.GetJob(summaryJob).Phase=="waiting_summary","summary waiter fixture");
            Check(s.SaveSummary(session,"public summary",hash),"summary saves and wakes atomically without separate notification");
        }
        using(var s=new SlackStore(summaryOptions))Check(s.GetJob(summaryJob).Phase=="queued","summary wake-up persists across reopening");
        using(var s=new SlackStore(Options(Path.Combine(root,"retention")))){
            var dates=new[]{now.AddDays(-31),now.AddDays(-10),now.AddDays(-6)};var ids=new List<string>();
            for(var i=0;i<dates.Length;i++){var m=Mention("EV-AGE"+i,dates[i]);var id=s.Accept(m,dates[i].AddSeconds(-1),dates[i]).JobId!;Ack(s);var j=s.ClaimNext()!;s.Finish(id,"public",true,false);Ack(s);ids.Add(j.SessionId);}
            Check(s.Cleanup(now,false)==1,"30 day expiration");var pressured=true;Check(s.Cleanup(now,true,()=>{var p=pressured;pressured=false;return p;})==1,"oldest over7 pressure cleanup stops after recovery");Check(s.GetSession(ids[2]).Id==ids[2],"7 day protection");
            var running=s.Accept(Mention("EV-RUN",now.AddDays(-40)),now.AddDays(-41),now.AddDays(-40)).JobId!;Ack(s);s.ClaimNext();Check(s.Cleanup(now,false)==0,"active job cannot expire");
        }
        using(var s=new SlackStore(Options(Path.Combine(root,"ordering")))){
            var first=s.Accept(Mention("EV-ORDER-1",now,"ORDER_THREAD"),online,now).JobId!;
            var follower=s.Accept(Mention("EV-ORDER-2",now.AddSeconds(1),"ORDER_THREAD",files:["FORDER"]),online,now.AddSeconds(1)).JobId!;
            Ack(s);var head=s.ClaimNext()!;Check(head.Id==first,"same-session turns retain arrival order over file priority");
            s.WaitForSummary(head.Id);Check(!s.HasPendingJobs()&&s.ClaimNext() is null,"blocked follower does not block summary maintenance");
            s.SummaryReady(head.SessionId);Check(s.ClaimNext()!.Id==first,"summary wake-up resumes session head");
            s.Finish(first,"public",true,false);Check(s.ClaimNext()!.Id==follower,"follower becomes claimable after predecessor completes");
        }
        using(var s=new SlackStore(Options(Path.Combine(root,"ocr-ordering")))){
            var image=s.Accept(Mention("EV-OCR-ORDER-1",now,"OCR_THREAD",files:["FOCR"]),online,now).JobId!;
            var follower=s.Accept(Mention("EV-OCR-ORDER-2",now.AddSeconds(1),"OCR_THREAD"),online,now.AddSeconds(1)).JobId!;
            Ack(s);var head=s.ClaimNext()!;Check(head.Id==image,"image turn starts before same-session follow-up");
            s.WaitForOcr(head.Id);Check(s.ClaimNext() is null&&s.HasPendingJobs(),"waiting OCR turn blocks same-session follow-up");
            s.OcrReady(head.Id);Check(s.ClaimNext()!.Id==image,"OCR completion resumes session head");
            s.Finish(image,"public OCR",true,true);Check(s.ClaimNext()!.Id==follower,"OCR follow-up waits for image turn completion");
        }
        using(var s=new SlackStore(Options(Path.Combine(root,"cross-session-priority")))){
            var text=s.Accept(Mention("EV-PRIORITY-TEXT",now,"TEXT_THREAD"),online,now).JobId!;
            var file=s.Accept(Mention("EV-PRIORITY-FILE",now.AddSeconds(1),"FILE_THREAD",files:["FPRIORITY"]),online,now.AddSeconds(1)).JobId!;
            Ack(s);Check(s.ClaimNext()!.Id==file&&s.GetJob(text).Phase=="queued","file priority remains across independent sessions");
        }
        var opts=Options(root);
        JsonElement Event(string type="app_mention",string text="<@UBOT> hello",string channel="CTEST",string user="UALICE",string team="TTEST")=>JsonSerializer.SerializeToElement(new{type="event_callback",event_id="EV-PARSE",team_id=team,@event=new{type,text,channel,user,ts=now.ToUnixTimeSeconds()+".000000"}});
        Check(SlackMention.Parse(Event(),opts,now) is not null,"real user id mention");
        foreach(var invalid in new[]{Event(text:"@LocalBrain hello"),Event(channel:"COTHER"),Event(type:"message"),Event(user:"UBOT"),Event(team:"TOTHER"),Event(text:"<@UBOT> <!here>")})Check(SlackMention.Parse(invalid,opts,now) is null,"invalid input ignored");
        using(RestrictedExecution.EnterSlack()){
            Denied(RestrictedExecution.DenyExternal,"egress boundary");Denied(RestrictedExecution.DenyAgentTools,"tools boundary");
            try{await CopilotReviewer.ReviewFinalAsync("public fixture",CancellationToken.None);throw new Exception("external review started");}catch(UnauthorizedAccessException){checks++;}
            try{await SolReviewer.HealthAsync(CancellationToken.None);throw new Exception("Sol started");}catch(UnauthorizedAccessException){checks++;}
            try{await new ReviewRunner(null!,null!,null!).RunAsync(["status"]);throw new Exception("review runner started");}catch(UnauthorizedAccessException){checks++;}
            Denied(()=>new HostEgress(null!).Prepare("public_task",[],[],[],[]),"Host export denied before packet construction");
            var tasks=Enumerable.Range(0,3).Select(_=>Task.Run(()=>Denied(RestrictedExecution.DenyExternal,"derived task inherits")));await Task.WhenAll(tasks);
        }
        RestrictedExecution.DenyExternal();Check(!RestrictedExecution.Active,"ordinary work unaffected");
        await SlackSharedThreadTests.RunAsync();
        await ApiAsync(opts);
        Console.WriteLine(JsonSerializer.Serialize(new{slack_public_checks=checks,status="PASS",real_slack=false,real_model=false}));
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> reply):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(reply(request));}
    private static async Task ApiAsync(SlackOptions o)
    {
        var calls=0;using var api=new SlackApi(new Handler(r=>{
            calls++;Check(r.RequestUri!.Host=="slack.com"&&r.Headers.Authorization!.Scheme=="Bearer","official authenticated API");
            if(r.RequestUri.AbsolutePath.EndsWith("auth.test"))return new(HttpStatusCode.OK){Content=new StringContent("{\"ok\":true,\"team_id\":\"TTEST\",\"user_id\":\"UBOT\"}")};
            Check(r.Method==HttpMethod.Get&&r.Content is null,"read API uses GET without JSON POST");
            Check(r.RequestUri.Query==(r.RequestUri.AbsolutePath.EndsWith("files.info")?"?file=FTEST":"?channel=CTEST"),"read argument stays separate from bearer token");
            return new(HttpStatusCode.OK){Content=new StringContent("{\"ok\":true,\"channel\":{\"id\":\"CTEST\",\"is_member\":true,\"is_private\":false,\"is_shared\":false,\"is_archived\":false}}")};
        }),"xapp-public-fixture","xoxb-public-fixture");
        await api.ValidateIdentity(o,CancellationToken.None);Check(calls==2,"live identity channel validation");
        await api.Call("files.info",new{file="FTEST"},false,CancellationToken.None);Check(calls==3,"file metadata uses documented read transport");
        using var incomplete=new SlackApi(new Handler(r=>new(HttpStatusCode.OK){Content=new StringContent(r.RequestUri!.AbsolutePath.EndsWith("auth.test")?"{\"ok\":true,\"team_id\":\"TTEST\",\"user_id\":\"UBOT\"}":"{\"ok\":true,\"channel\":{\"id\":\"CTEST\",\"is_member\":true}}")}),"a","b");
        try{await incomplete.ValidateIdentity(o,CancellationToken.None);throw new Exception("missing channel safety flags accepted");}catch(UnauthorizedAccessException){checks++;}
        using var limited=new SlackApi(new Handler(_=>{var r=new HttpResponseMessage(HttpStatusCode.TooManyRequests);r.Headers.RetryAfter=new(TimeSpan.FromSeconds(17));return r;}),"a","b");
        try{await limited.Open(CancellationToken.None);throw new Exception("rate limit ignored");}catch(SlackRateLimit r){Check(r.Seconds==17,"Slack retry-after");}
        using var bad=new SlackApi(new Handler(_=>new(HttpStatusCode.OK){Content=new StringContent("{\"ok\":true,\"url\":\"wss://attacker.invalid/socket\"}")}),"a","b");
        try{await bad.Open(CancellationToken.None);throw new Exception("untrusted socket accepted");}catch(UnauthorizedAccessException){checks++;}
    }
}
