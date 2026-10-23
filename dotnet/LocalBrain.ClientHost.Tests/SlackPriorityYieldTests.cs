using System.Net;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.Ocr;
using LocalBrain.ClientHost.Slack;
using Microsoft.Data.Sqlite;

// Public in-memory fixtures only: no Slack, model, OCR worker or production configuration.
internal static class SlackPriorityYieldTests
{
    internal static async Task RunAsync()
    {
        var checks=0;void Check(bool ok,string name){if(!ok)throw new Exception("SLACK_PRIORITY_YIELD_TEST: "+name);checks++;}
        var root=Directory.CreateTempSubdirectory("lb-slack-yield-public-").FullName;
        var options=new SlackOptions(true,Path.Combine(root,"store"),"TTEST","CTEST","UBOT",Path.Combine(root,"app"),Path.Combine(root,"bot"));
        var ocr=new OcrConfig(true,Path.Combine(root,"ocr"),Path.Combine(root,"assets"),Path.Combine(root,"assets","python.exe"),Path.Combine(root,"assets","worker.py"),Path.Combine(root,"assets","models"),[root],new(),["AUTO"],[],1024L<<20,1024L<<20,700L<<20,25,new string('a',64));
        OcrResources.Sample Idle()=>new(long.MaxValue,long.MaxValue,0);

        // A single transient probe failure during generation sends one cancel and is identified by a fixed code.
        using(var store=new SlackStore(options)){
            using var restricted=RestrictedExecution.EnterSlack();
            var now=DateTimeOffset.UtcNow;var stamp=now.ToUnixTimeSeconds()+".000001";
            var session=store.GetJob(store.Accept(new("EV-YIELD-PROBE","TTEST","CTEST","UALICE",stamp,stamp,"PUBLIC",[],now),now.AddSeconds(-1),now).JobId!).SessionId;
            string? transient=null;var priority=SlackPriority.ForFixture(ocr,Idle,()=>transient);
            var cancelCalls=0;using var entered=new SemaphoreSlim(0);
            using var gateway=new GatewayClient(new Uri("https://public-fixture.invalid"),new Handler(async (r,t)=>{
                if(r.RequestUri!.AbsolutePath=="/v1/slack/cancel"){cancelCalls++;return new(HttpStatusCode.OK){Content=new StringContent("{\"cancelled\":true}")};}
                entered.Release();await Task.Delay(Timeout.Infinite,t);throw new Exception("uncancelled transport");
            }));
            var task=priority.Run(t=>new SlackLocalInference(gateway,store).Chat("PUBLIC-YIELD-PROBE",session,"","PUBLIC",64,120,t),125,CancellationToken.None);
            if(task.IsFaulted)await task;
            Check(await entered.WaitAsync(TimeSpan.FromSeconds(5)),"chat reached fixture transport");transient="probe_failed";
            try{await task;throw new Exception("transient yield ignored");}
            catch(SlackPriorityYield y){Check(y.Code=="priority_yield_probe_failed"&&cancelCalls==1,"transient probe failure yields with fixed code and one cancel");}
        }
        var caller=SlackPriority.ForFixture(ocr,Idle);var yieldNow=false;
        var callerTask=caller.Run(async t=>{await Task.Delay(Timeout.Infinite,t);return 0;},30,CancellationToken.None,()=>yieldNow);
        await Task.Delay(120);yieldNow=true;
        try{await callerTask;throw new Exception("caller yield ignored");}catch(SlackPriorityYield y){Check(y.Code=="priority_yield_caller","caller yield code");}
        try{await caller.Run(async t=>{await Task.Delay(Timeout.Infinite,t);return 0;},1,CancellationToken.None);throw new Exception("outer deadline ignored");}
        catch(SlackPriorityYield y){Check(y.Code=="priority_outer_deadline","outer allowance code");}
        using(var host=new CancellationTokenSource(150)){
            try{await caller.Run(async t=>{await Task.Delay(Timeout.Infinite,t);return 0;},30,host.Token);throw new Exception("host stop ignored");}
            catch(OperationCanceledException e){Check(e is not SlackPriorityYield,"host stop remains a plain cancellation");}
        }
        Check(caller.ForegroundReason() is null,"idle fixture has no foreground reason");
        Check(SlackPriority.ForFixture(ocr,Idle,()=>throw new UnauthorizedAccessException("PUBLIC")).ForegroundReason()=="probe_failed","probe exception has fixed fail-closed reason");

        // Processor: the yield is deferred (not failed) and audited with only the fixed code and job hash.
        var processorOptions=options with{Root=Path.Combine(root,"processor")};string id;
        using(var store=new SlackStore(processorOptions)){
            var now=DateTimeOffset.UtcNow;var stamp=now.ToUnixTimeSeconds()+".000002";
            id=store.Accept(new("EV-YIELD","TTEST","CTEST","UALICE",stamp,stamp,"PUBLIC yield fixture",[],now),now.AddSeconds(-1),now).JobId!;
            var receipt=store.ClaimReply(now)!;store.ReplySent(receipt.Id,"1700000000.000002");
            string? transient=null;var inference=new BlockingInference();
            using var api=new SlackApi(new Handler((_,_)=>throw new Exception("Unexpected Slack call")),"xapp-public-fixture","xoxb-public-fixture");
            var processor=new SlackProcessor(processorOptions,store,api,inference,ocr,SlackPriority.ForFixture(ocr,Idle,()=>transient));
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var run=processor.Run(stop.Token);
            Check(await inference.Entered.WaitAsync(TimeSpan.FromSeconds(10)),"processor started public chat");
            transient="legacy_unreadable";await inference.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(300);stop.Cancel();
            try{await run;}catch(OperationCanceledException)when(stop.IsCancellationRequested){}
            Check(store.GetJob(id).Phase=="queued"&&inference.Calls==1,"yield defers without failing or re-entering chat while foreground persists");
        }
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(processorOptions.Root,"slack.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString())){
            db.Open();using var c=db.CreateCommand();c.CommandText="SELECT code,object_hash,bytes FROM audit WHERE code LIKE 'priority_%';";
            var rows=new List<(string Code,string Hash,long Bytes)>();using(var r=c.ExecuteReader())while(r.Read())rows.Add((r.GetString(0),r.GetString(1),r.GetInt64(2)));
            Check(rows.Count==1&&rows[0].Code=="priority_yield_legacy_unreadable"&&rows[0].Hash==SlackHash.Of(id)&&rows[0].Bytes==0,"one content-free yield audit");
        }
        using(var store=new SlackStore(options with{Root=Path.Combine(root,"code")})){
            try{store.Defer("PUBLIC","free text");throw new Exception("arbitrary audit code accepted");}catch(InvalidDataException){checks++;}
        }
        Console.WriteLine(JsonSerializer.Serialize(new{slack_priority_yield_checks=checks,status="PASS",real_model=false,real_slack=false}));
    }
    private sealed class BlockingInference:ISlackLocalInference
    {
        internal readonly SemaphoreSlim Entered=new(0);internal readonly TaskCompletionSource Cancelled=new(TaskCreationOptions.RunContinuationsAsynchronously);internal int Calls;
        public async Task<string> Chat(string requestId,string session,string context,string prompt,int outputTokens,int seconds,CancellationToken token)
        {Interlocked.Increment(ref Calls);Entered.Release();try{await Task.Delay(Timeout.Infinite,token);}finally{Cancelled.TrySetResult();}throw new Exception("unreachable");}
        public Task<SlackReviewedCandidate?> Candidate(string session,string source,CancellationToken token)=>throw new Exception("Unexpected review");
        public Task<string> RegisterProposal(string id,string reviewRequestId,string session,CancellationToken token)=>throw new Exception("Unexpected proposal");
    }
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> action):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>action(request,token);}
}
