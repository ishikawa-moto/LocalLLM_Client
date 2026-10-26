using System.Net;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.Slack;
using Microsoft.Data.Sqlite;

// Public SQLite/HTTP fixtures only: no production data, network, model or Slack.
internal static class SlackHeldRecoveryTests
{
    internal static async Task RunAsync()
    {
        var checks=0;
        void Check(bool value,string name){if(!value)throw new Exception("SLACK_HELD_RECOVERY_TEST:"+name);checks++;}
        using var restricted=RestrictedExecution.EnterSlack();
        foreach(var outcome in new[]{"complete","interrupted","explicit_cancel","unknown","unclassified_crash","running","unresolved","transport","cancelled_probe","wrong_hash"}){
            var root=Directory.CreateTempSubdirectory("lb-held-public-").FullName;
            var options=new SlackOptions(true,root,"TTEST","CTEST","UBOT",Path.Combine(root,"app"),Path.Combine(root,"bot"));
            using var store=new SlackStore(options);
            var now=DateTimeOffset.UtcNow;var ts=now.ToUnixTimeSeconds()+".000001";
            var id=store.Accept(new("EV-HELD","TTEST","CTEST","UALICE",ts,ts,"PUBLIC",[],now),now.AddSeconds(-1),now).JobId!;
            var session=store.GetJob(id).SessionId;
            while(store.ClaimReply(now) is {} accepted)store.ReplySent(accepted.Id,ts);
            var frozen=store.FreezeInference("/v1/slack/chat",id,session,JsonSerializer.Serialize(new{request_id=id,session_id=session,context="PUBLIC",prompt="fixture",max_tokens=64,timeout_seconds=120,restriction=SlackRestriction.Instance}));
            var plan=store.ChatProgress(id,session,frozen,false);store.MarkChatSending(plan);
            if(outcome!="unclassified_crash")store.ChatAwaiting(id,outcome=="interrupted"?"cancellation_pending":"transport_disconnected");
            if(outcome=="interrupted")store.SuspendChat(id,"priority_yield_lease");
            for(var i=0;i<20;i++)store.ChatProbeFailed(store.ChatProgress(id,session,frozen,true));
            plan=store.ChatProgress(id,session,frozen,true);
            Check(plan.State=="held"&&plan.NextCheck>now.AddMinutes(4).ToUnixTimeMilliseconds(),"persisted five-minute held interval");
            var statusCalls=0;var retryCalls=0;
            using var gateway=new GatewayClient(new Uri("https://fixture.invalid"),new Handler(async(request,token)=>{
                var route=request.RequestUri!.AbsolutePath;
                if(route=="/v1/slack/status"){
                    statusCalls++;
                    using var sent=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                    Check(sent.RootElement.GetProperty("request_id").GetString()==id&&sent.RootElement.GetProperty("request_hash").GetString()==plan.Hash,"probe binds old immutable identity");
                    if(outcome=="transport")throw new HttpRequestException("PUBLIC fixture transport");
                    if(outcome=="cancelled_probe")throw new OperationCanceledException("PUBLIC fixture probe");
                    if(outcome=="complete")return Reply(new{state="complete",request_hash=plan.Hash,result=new{status="complete",text="PUBLIC saved result"}});
                    if(outcome is "interrupted" or "explicit_cancel")return Reply(new{state="interrupted",request_hash=plan.Hash,reason="explicit_cancel",recovery_contract="quiescent-attempt-v1"});
                    if(outcome is "unknown" or "unclassified_crash")return Reply(new{state="unknown",retry_safe=false});
                    return Reply(new{state=outcome=="wrong_hash"?"complete":outcome,request_hash=outcome=="wrong_hash"?new string('0',64):plan.Hash,result=new{status="complete",text="PUBLIC wrong binding"}});
                }
                if(route=="/v1/slack/retry"){
                    retryCalls++;using var sent=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));var value=sent.RootElement;
                    Check(value.GetProperty("previous_request_id").GetString()==id&&value.GetProperty("request").GetProperty("request_id").GetString()==SlackStore.AttemptId(id,1)&&value.GetProperty("request").GetProperty("timeout_seconds").GetInt32()==120,"only existing predecessor-bound retry contract");
                    return Reply(new{status="complete",text="PUBLIC retry result"});
                }
                throw new Exception("GENERATION_OR_CANCEL_ROUTE_NOT_ALLOWED:"+route);
            }));
            var inference=new SlackLocalInference(gateway,store);
            async Task Deferred(){try{await inference.Chat(id,session,"PUBLIC","fixture",64,120,CancellationToken.None);throw new Exception("expected deferred");}catch(SlackDeferred){checks++;}}
            void Due(){using var db=new SqliteConnection("Data Source="+Path.Combine(root,"slack.db"));db.Open();using var command=db.CreateCommand();command.CommandText="UPDATE inference_progress SET next_check=0 WHERE root_id=$id";command.Parameters.AddWithValue("$id",id);command.ExecuteNonQuery();}
            await Deferred();Check(statusCalls==0&&retryCalls==0,"not due means no network");
            Due();
            if(outcome=="complete"){
                Check(await inference.Chat(id,session,"PUBLIC","fixture",64,120,CancellationToken.None)=="PUBLIC saved result","held result recovered without generation");
                Check(await inference.Chat(id,session,"PUBLIC","fixture",64,120,CancellationToken.None)=="PUBLIC saved result"&&statusCalls==1&&retryCalls==0,"durable completion replay has zero new transport");
            }else if(outcome=="interrupted"){
                await Deferred();var next=store.ChatProgress(id,session,frozen,true);
                Check(next.State=="ready"&&next.Attempt==1&&statusCalls==1&&retryCalls==0,"known priority yield reserves one bounded successor");
                await Deferred();Check(retryCalls==0,"successor persisted delay enforced");Due();
                Check(await inference.Chat(id,session,"PUBLIC","fixture",64,120,CancellationToken.None)=="PUBLIC retry result"&&retryCalls==1,"successor uses Server-gated retry only");
            }else if(outcome=="wrong_hash"){
                try{await inference.Chat(id,session,"PUBLIC","fixture",64,120,CancellationToken.None);throw new Exception("wrong hash accepted");}catch(InvalidDataException){checks++;}
                Check(retryCalls==0,"binding failure cannot dispatch generation");
            }else{
                for(var i=0;i<SlackStore.HeldProbeLimit;i++){
                    Due();await Deferred();Check(retryCalls==0,"unproven status cannot dispatch generation");
                    Check(store.ChatProgress(id,session,frozen,true).Attempt==0,"observation never resets/increments inference attempt");
                }
                Check(statusCalls==SlackStore.HeldProbeLimit,"slow observations are bounded");
                Due();await Deferred();Check(statusCalls==SlackStore.HeldProbeLimit,"exhausted observation cannot restart itself");
                var notices=0;while(store.ClaimReply(DateTimeOffset.UtcNow) is {} reply){if(reply.Id==id+"-recovery-review-required")notices++;store.ReplySent(reply.Id,ts);}
                store.HeldProbeUnresolved(store.ChatProgress(id,session,frozen,true));
                Check(notices==1&&store.ClaimReply(DateTimeOffset.UtcNow) is null,"one exhaustion notification, not one per loop");
                using var reopened=new SlackStore(options);
                var saved=reopened.ChatProgress(id,session,frozen,true);
                Check(!reopened.BeginHeldProbe(saved,DateTimeOffset.UtcNow.AddYears(1)),"Host restart cannot reset exhausted observation budget");
            }
            // Waiting/held work in one session cannot monopolize the global queue.
            var otherTs=ts+"1";
            var other=store.Accept(new("EV-OTHER","TTEST","CTEST","UBOB",otherTs,otherTs,"PUBLIC other",[],now.AddSeconds(1)),now.AddSeconds(-1),now.AddSeconds(1)).JobId!;
            while(store.ClaimReply(DateTimeOffset.UtcNow.AddSeconds(2)) is {} reply)store.ReplySent(reply.Id,otherTs);
            if(store.ChatProgress(id,session,frozen,true).State=="held")Check(store.ClaimNext()?.Id==other,"parked job does not starve a different session");
        }
        // Last reservation survives a crash before a response/notification. Also
        // keep legacy missing-progress requests permanently non-replayable.
        var crashRoot=Directory.CreateTempSubdirectory("lb-held-crash-public-").FullName;
        var crashOptions=new SlackOptions(true,crashRoot,"TTEST","CTEST","UBOT",Path.Combine(crashRoot,"app"),Path.Combine(crashRoot,"bot"));
        string crashId,crashSession,crashFrozen;
        var clock=DateTimeOffset.UtcNow;
        using(var store=new SlackStore(crashOptions)){
            var ts=clock.ToUnixTimeSeconds()+".000001";crashId=store.Accept(new("EV-CRASH","TTEST","CTEST","UALICE",ts,ts,"PUBLIC",[],clock),clock.AddSeconds(-1),clock).JobId!;crashSession=store.GetJob(crashId).SessionId;
            crashFrozen=store.FreezeInference("/v1/slack/chat",crashId,crashSession,JsonSerializer.Serialize(new{request_id=crashId,session_id=crashSession,context="PUBLIC",prompt="fixture",max_tokens=64,timeout_seconds=120,restriction=SlackRestriction.Instance}));
            var plan=store.ChatProgress(crashId,crashSession,crashFrozen,false);store.MarkChatSending(plan);store.ChatAwaiting(crashId,"cancellation_pending");
            for(var i=0;i<20;i++)store.ChatProbeFailed(store.ChatProgress(crashId,crashSession,crashFrozen,true));
            clock=clock.AddMinutes(6);
            for(var i=0;i<12;i++){Check(store.BeginHeldProbe(store.ChatProgress(crashId,crashSession,crashFrozen,true),clock),"durable observation reserved before simulated crash");clock=clock.AddMinutes(6);}
        }
        using(var store=new SlackStore(crashOptions)){
            store.NotifyExhaustedHeldProbes(clock);
            store.NotifyExhaustedHeldProbes(clock.AddMinutes(1));
            var notices=0;while(store.ClaimReply(clock) is {} reply){if(reply.Id==crashId+"-recovery-review-required")notices++;store.ReplySent(reply.Id,"PUBLIC");}
            Check(notices==1,"last-reservation crash notification recovered exactly once");
            Check(store.ChatProgress(crashId,crashSession,crashFrozen,true).Attempt==0,"crash recovery never generates a new attempt");
            using(var db=new SqliteConnection("Data Source="+Path.Combine(crashRoot,"slack.db"))){db.Open();using var c=db.CreateCommand();c.CommandText="DELETE FROM inference_progress WHERE root_id=$id";c.Parameters.AddWithValue("$id",crashId);c.ExecuteNonQuery();}
            var legacy=store.ChatProgress(crashId,crashSession,crashFrozen,true);
            Check(legacy.State=="legacy_hold"&&!store.BeginHeldProbe(legacy,clock),"legacy freeze cannot acquire new recovery authority");
        }
        Console.WriteLine(JsonSerializer.Serialize(new{slack_held_recovery_checks=checks,status="PASS",real_model=false,real_slack=false,real_network=false}));
    }
    private static HttpResponseMessage Reply(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value))};
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> action):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>action(request,token);}
}
