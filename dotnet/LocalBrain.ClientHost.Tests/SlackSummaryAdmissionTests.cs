using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;
using LocalBrain.ClientHost.Ocr;
using LocalBrain.ClientHost.Slack;

internal static class SlackSummaryAdmissionTests
{
    internal static async Task RunAsync()
    {
        var checks=0;void Check(bool ok,string name){if(!ok)throw new Exception("SLACK_SUMMARY_ADMISSION_TEST: "+name);checks++;}
        var cpu=new OcrResources.CpuWindow();
        Check(cpu.Observe(100,1000,0)==100,"cold measurement refuses until a baseline exists");
        Check(cpu.Observe(1000,2000,1000)==10,"established CPU window");
        Check(cpu.Observe(1000,2000,1000)==10,"zero delta cannot become 100 percent");
        Check(cpu.Observe(1001,2001,1001)==10,"single busy/idle tick cannot swing decision");
        for(var i=2;i<100;i++)Check(cpu.Observe(1000+i,2000+i,1000+i)==10,"nested admission retains bounded CPU sample");
        Check(cpu.Observe(1000,2250,1250)==100,"sustained high CPU still refuses");
        Check(cpu.Observe(1250,2500,1500)==0,"new idle window reopens admission");
        Check(cpu.Observe(10,20,100)==100,"counter/clock reset fails closed");
        Check(TaskMemoryQ8.SlackSummaryOperationSeconds>240&&TaskMemoryQ8.SlackSummaryOperationSeconds<=900,"summary deadline allows cold startup but remains bounded");
        var root=Directory.CreateTempSubdirectory("lb-summary-admission-public-").FullName;
        var options=new SlackOptions(true,Path.Combine(root,"store"),"TTEST","CTEST","UBOT",Path.Combine(root,"app"),Path.Combine(root,"bot"));
        using var store=new SlackStore(options);var now=DateTimeOffset.UtcNow;
        string Accept(string id,int thread)=>store.Accept(new(id,"TTEST","CTEST","UALICE",now.ToUnixTimeSeconds()+"."+thread.ToString("D6"),now.ToUnixTimeSeconds()+"."+thread.ToString("D6"),"PUBLIC "+id,[],now),now.AddSeconds(-1),now).JobId!;
        void Ack(){while(store.ClaimReply(now.AddMinutes(1)) is{} r)store.ReplySent(r.Id,"1700000000.000001");}
        var old=Accept("EV-OLD",1);Ack();store.ClaimNext();store.Finish(old,"PUBLIC OLD",true,false);Ack();
        var seed=Accept("EV-SEED",2);Ack();store.ClaimNext();store.Finish(seed,new string('x',8500),true,false);Ack();
        var job=Accept("EV-WAITER",2);Ack();Check(store.ClaimNext()?.Id==job,"accepted original request claimed once");store.WaitForSummary(job);
        var original=store.GetJob(job);var session=store.GetSession(original.SessionId);var historyHash=store.HistoryHash(session.Id);
        Check(!store.HasPendingJobs()&&store.HasSummaryWaiter(session.Id),"summary waiter is not foreground deadlock");
        Check(SlackProcessor.MaintenanceOrder(store)[0].Id==session.Id,"reply-blocking summary precedes optional candidate work");
        store.SummaryState(session.Id,"summary_started");store.SummaryState(session.Id,"summary_started");
        var progress=store.ClaimReply(now.AddMinutes(1));Check(progress is{Kind:"progress"}&&progress.JobId==job&&progress.Text.Contains("ローカルで要約"),"local summary progress targets original requester");store.ReplySent(progress!.Id,"1700000000.000002");
        Check(store.ClaimReply(now.AddMinutes(1)) is null,"progress notification does not duplicate across attempts");
        Check(store.HistoryHash(session.Id)==historyHash&&store.GetJob(job)==original,"progress metadata does not alter original request or history");
        var calls=0;
        Check(await SlackConversation.Compress(store,session.Id,4096,(source,budget,t)=>{calls++;return Task.FromResult("PUBLIC SUMMARY OF SEED");},()=>true,CancellationToken.None),"existing summary waiter resumes under normal contract");
        Check(calls==1&&store.GetJob(job).Phase=="queued","summary wakes same request without resubmission");
        Check(store.ClaimNext()?.Id==job&&store.ClaimNext() is null,"original request resumes once");
        var context=SlackConversation.Context(store,store.GetSession(session.Id),store.History(session.Id).Where(j=>j.Phase=="complete").ToArray(),original.Text,8000);
        Check(context is not null&&SlackConversation.Fits(context,original.Text),"bound summary produces valid context");
        Check(store.HistoryHash(session.Id)==historyHash,"original completed turns remain intact");
        try{store.SummaryState(session.Id,"PRIVATE CONTENT");throw new Exception("unrestricted diagnostic code accepted");}catch(ArgumentException){checks++;}
        Console.WriteLine(JsonSerializer.Serialize(new{slack_summary_admission_checks=checks,status="PASS",model_calls=0,real_slack=false,private_data=false}));
    }
}