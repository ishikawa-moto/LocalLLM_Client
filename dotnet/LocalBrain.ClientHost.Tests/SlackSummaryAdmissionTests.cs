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
        var history=store.History(session.Id).Where(j=>j.Phase=="complete").ToArray();
        var rawContext=SlackConversation.Context(store,session,history,original.Text,8000);
        Check(rawContext is not null&&SlackConversation.Bytes(rawContext)>8000&&SlackConversation.Fits(rawContext,original.Text),"soft threshold cannot block a request within hard limit");
        Check(store.ResumeReadySummaryWaiters(8000)==1&&store.GetJob(job).Phase=="queued","older unnecessary summary wait resumes same request");
        Check(store.ClaimNext()?.Id==job&&store.ClaimNext() is null,"original request resumes once without model call");
        Check(store.HistoryHash(session.Id)==historyHash,"original completed turns remain intact");
        store.Finish(job,"PUBLIC RESUMED",true,false);Ack();
        var large=Accept("EV-LARGE-SEED",2);Ack();store.ClaimNext();store.Finish(large,new string('x',25000),true,false);Ack();
        var hard=Accept("EV-HARD-WAIT",2);Ack();store.ClaimNext();store.WaitForSummary(hard);var beforeHard=store.HistoryHash(session.Id);
        Check(store.ResumeReadySummaryWaiters(8000)==0&&store.GetJob(hard).Phase=="waiting_summary","hard-limit waiter never bypasses budget");
        var calls=0;
        Check(await SlackConversation.Compress(store,session.Id,4096,(source,budget,t)=>{calls++;return Task.FromResult("PUBLIC SUMMARY OF SEED");},()=>true,CancellationToken.None),"hard-limit waiter resumes under normal summary contract");
        Check(calls>0&&store.GetJob(hard).Phase=="queued","real summary wakes same request");
        Check(store.HistoryHash(session.Id)==beforeHard,"summary never deletes original history");
        var ocr=new OcrConfig(true,Path.Combine(root,"ocr"),Path.Combine(root,"assets"),Path.Combine(root,"assets","python.exe"),Path.Combine(root,"assets","worker.py"),Path.Combine(root,"assets","models"),[root],new(),["AUTO"],[],1024L*1024*1024,1024L*1024*1024,700L*1024*1024,25,new string('a',64));
        var priority=SlackPriority.ForFixture(ocr,()=>new(long.MaxValue,long.MaxValue,0));
        var yield=false;using var started=new SemaphoreSlim(0);var running=priority.Run(async t=>{started.Release();await Task.Delay(TimeSpan.FromSeconds(20),t);return true;},25,CancellationToken.None,()=>yield);
        await started.WaitAsync();yield=true;
        try{await running;throw new Exception("summary ignored pending user work");}catch(OperationCanceledException){checks++;}
        try{store.SummaryState(session.Id,"PRIVATE CONTENT");throw new Exception("unrestricted diagnostic code accepted");}catch(ArgumentException){checks++;}
        Console.WriteLine(JsonSerializer.Serialize(new{slack_summary_admission_checks=checks,status="PASS",model_calls=0,real_slack=false,private_data=false}));
    }
}