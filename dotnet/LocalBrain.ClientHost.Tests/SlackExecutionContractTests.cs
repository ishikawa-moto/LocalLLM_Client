using System.Net;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.Slack;
using Microsoft.Data.Sqlite;

// Public in-memory Gateway only; no Slack, production configuration or model.
internal static class SlackExecutionContractTests
{
    internal static async Task RunAsync()
    {
        var checks=0;void Check(bool ok,string name){if(!ok)throw new Exception("SLACK_EXECUTION_CONTRACT_TEST:"+name);checks++;}
        using var restricted=RestrictedExecution.EnterSlack();
        foreach(var state in new[]{"running","unresolved"}){
            var root=Directory.CreateTempSubdirectory("lb-nonterminal-public-").FullName;
            var options=new SlackOptions(true,root,"TTEST","CTEST","UBOT",Path.Combine(root,"app"),Path.Combine(root,"bot"));
            using var store=new SlackStore(options);var now=DateTimeOffset.UtcNow;var stamp=now.ToUnixTimeSeconds()+".000001";
            var id=store.Accept(new("EV-NONTERMINAL","TTEST","CTEST","UALICE",stamp,stamp,"PUBLIC",[],now),now.AddSeconds(-1),now).JobId!;
            var session=store.GetJob(id).SessionId;var posts=0;var probes=0;var reviews=0;var complete=false;string? frozen=null;
            using var gateway=new GatewayClient(new Uri("https://public-fixture.invalid"),new Handler(async (request,token)=>{
                var path=request.RequestUri!.AbsolutePath;
                if(path=="/v1/slack/chat"){posts++;frozen=await request.Content!.ReadAsStringAsync(token);return Reply(new{status=state,retry_safe=false});}
                if(path=="/v1/slack/status"){
                    probes++;var body=JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
                    Check(body.GetProperty("request_id").GetString()==id&&body.GetProperty("session_id").GetString()==session&&body.GetProperty("request_hash").GetString()==SlackStore.ChatHash(frozen!),"status binding is immutable");
                    return complete?Reply(new{state="complete",request_hash=SlackStore.ChatHash(frozen!),result=new{status="complete",text="PUBLIC recovered"}}):Reply(new{state,request_hash=SlackStore.ChatHash(frozen!),retry_safe=false});
                }
                if(path=="/v1/slack/review"){reviews++;return Reply(new{status=state,retry_safe=false});}
                throw new Exception("Unexpected fixture route");
            }));
            var inference=new SlackLocalInference(gateway,store);
            async Task Deferred(){try{await inference.Chat(id,session,"PUBLIC","fixture",64,120,CancellationToken.None);throw new Exception("Nonterminal result accepted");}catch(SlackDeferred){checks++;}}
            void Due(){using var db=new SqliteConnection("Data Source="+Path.Combine(root,"slack.db"));db.Open();using var c=db.CreateCommand();c.CommandText="UPDATE inference_progress SET next_check=0 WHERE root_id=$id";c.Parameters.AddWithValue("$id",id);c.ExecuteNonQuery();}
            await Deferred();var plan=store.ChatProgress(id,session,frozen!,true);
            Check(plan.State=="probing"&&plan.Attempt==0&&plan.Cause=="server_nonterminal","direct response is a nonterminal fence");
            await Deferred();Check(posts==1&&probes==0,"no repeat chat before status deadline");
            Due();await Deferred();Check(posts==1&&probes==1&&store.ChatProgress(id,session,frozen!,true).Attempt==0,"nonterminal probe never creates a successor");
            Due();complete=true;Check(await inference.Chat(id,session,"PUBLIC","fixture",64,120,CancellationToken.None)=="PUBLIC recovered","terminal result recovered through exact status");
            Check(posts==1&&probes==2,"one generation transport and two status probes only");
            Check(await inference.Chat(id,session,"PUBLIC","fixture",64,120,CancellationToken.None)=="PUBLIC recovered"&&posts==1&&probes==2,"persisted result replay has no transport");
            try{await inference.Candidate(session,"PUBLIC review",CancellationToken.None);throw new Exception("Nonterminal review accepted");}catch(SlackInterrupted){checks++;}
            Check(reviews==1,"review does not invent a retry identity");
        }
        Console.WriteLine(JsonSerializer.Serialize(new{slack_execution_contract_checks=checks,status="PASS",real_model=false,real_slack=false}));
    }
    private static HttpResponseMessage Reply(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value))};
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> action):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>action(request,token);}
}