using System.Net;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.Ocr;
using LocalBrain.ClientHost.Slack;

// Public in-memory transport only. Crosses the former 35-second Client cutoff.
internal static class SlackDeadlineTests
{
    internal static async Task RunAsync()
    {
        var checks=0;void Check(bool ok,string name){if(!ok)throw new Exception("SLACK_DEADLINE_TEST: "+name);checks++;}
        var root=Directory.CreateTempSubdirectory("lb-slack-deadline-public-").FullName;
        var options=new SlackOptions(true,Path.Combine(root,"store"),"TTEST","CTEST","UBOT",Path.Combine(root,"app"),Path.Combine(root,"bot"));
        options.Validate();Check(options.InferenceSeconds==120,"new default permits the bounded Server contract");
        (options with{InferenceSeconds=30}).Validate();Check((options with{InferenceSeconds=30}).InferenceSeconds==30,"explicit older cap is retained until configuration migration");
        foreach(var bad in new[]{4,121}){try{(options with{InferenceSeconds=bad}).Validate();throw new Exception("unbounded configuration accepted");}catch(InvalidDataException){checks++;}}
        using var store=new SlackStore(options);var now=DateTimeOffset.UtcNow;
        var stamp=now.ToUnixTimeSeconds()+".000001";var id=store.Accept(new("EV-DEADLINE","TTEST","CTEST","UALICE",stamp,stamp,"PUBLIC",[],now),now.AddSeconds(-1),now).JobId!;
        var session=store.GetJob(id).SessionId;
        var ocr=new OcrConfig(true,Path.Combine(root,"ocr"),Path.Combine(root,"assets"),Path.Combine(root,"assets","python.exe"),Path.Combine(root,"assets","worker.py"),Path.Combine(root,"assets","models"),[root],new(),["AUTO"],[],1024L<<20,1024L<<20,700L<<20,25,new string('a',64));
        var priority=SlackPriority.ForFixture(ocr,()=>new(long.MaxValue,long.MaxValue,0));
        using var restricted=RestrictedExecution.EnterSlack();var calls=0;
        using(var gateway=new GatewayClient(new Uri("https://public-fixture.invalid"),new Handler(async (r,t)=>{
            calls++;var input=JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync(t));
            Check(r.RequestUri!.AbsolutePath=="/v1/slack/chat"&&input.GetProperty("timeout_seconds").GetInt32()==120,"authenticated chat sends the bounded caller allowance");
            Check(input.GetProperty("restriction").GetProperty("externalAi").GetString()=="deny","longer deadline does not permit cloud fallback");
            await Task.Delay(TimeSpan.FromSeconds(36),t);
            return new(HttpStatusCode.OK){Content=new StringContent("{\"status\":\"complete\",\"text\":\"PUBLIC AFTER 36 SECONDS\"}")};
        }))){
            var result=await priority.Run(t=>new SlackLocalInference(gateway,store).Chat(id,session,new string('x',10840),"PUBLIC",64,options.InferenceSeconds,t),options.InferenceSeconds+5,CancellationToken.None);
            Check(result=="PUBLIC AFTER 36 SECONDS"&&calls==1,"one original operation survives former 35-second cancellation");
        }
        var cancelCalls=0;using var entered=new SemaphoreSlim(0);var yield=false;
        using(var gateway=new GatewayClient(new Uri("https://public-fixture.invalid"),new Handler(async (r,t)=>{
            if(r.RequestUri!.AbsolutePath=="/v1/slack/cancel"){cancelCalls++;return new(HttpStatusCode.OK){Content=new StringContent("{\"cancelled\":true}")};}
            entered.Release();await Task.Delay(Timeout.Infinite,t);throw new Exception("uncancelled transport");
        }))){
            var task=priority.Run(t=>new SlackLocalInference(gateway,store).Chat("PUBLIC-CANCEL-DEADLINE",session,"","PUBLIC",64,120,t),125,CancellationToken.None,()=>yield);
            await entered.WaitAsync(TimeSpan.FromSeconds(2));var timer=System.Diagnostics.Stopwatch.StartNew();yield=true;
            try{await task;throw new Exception("foreground yield ignored");}catch(OperationCanceledException){Check(timer.Elapsed<TimeSpan.FromSeconds(2)&&cancelCalls==1,"long allowance still cancels promptly once without replay");}
        }
        Console.WriteLine(JsonSerializer.Serialize(new{slack_deadline_checks=checks,status="PASS",real_model=false,real_slack=false,public_delay_seconds=36}));
    }
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> action):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>action(request,token);}
}