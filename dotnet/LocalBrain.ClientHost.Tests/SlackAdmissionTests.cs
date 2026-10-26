using System.Net;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.Slack;
using Microsoft.Data.Sqlite;

internal static class SlackAdmissionTests
{
 internal static async Task RunAsync(){
  var checks=0;void Check(bool value,string name){if(!value)throw new Exception("SLACK_ADMISSION_TEST:"+name);checks++;}
  void Restriction(JsonElement body){var r=body.GetProperty("restriction");Check(r.EnumerateObject().Select(x=>x.Name).Order().SequenceEqual(new[]{"cloudFallback","dataClass","externalAi","origin"}),"exact Server wire keys");Check(r.GetProperty("origin").GetString()=="slack"&&r.GetProperty("dataClass").GetString()=="slack_restricted"&&r.GetProperty("externalAi").GetString()=="deny"&&r.GetProperty("cloudFallback").ValueKind==JsonValueKind.False,"unchanged deny-cloud boundary");}
  using var restricted=RestrictedExecution.EnterSlack();
  {
   var root=Directory.CreateTempSubdirectory("lb-admission-legacy-hash-public-").FullName;using var store=new SlackStore(Options(root));var now=DateTimeOffset.UtcNow;var ts=now.ToUnixTimeSeconds()+".000001";
   var id=store.Accept(new("EV-HASH","TTEST","CTEST","UALICE",ts,ts,"PUBLIC",[],now),now.AddSeconds(-1),now).JobId!;var session=store.GetJob(id).SessionId;
   var p=store.Propose(session,"knowledge","PUBLIC","PUBLIC evidence","PUBLIC review",now);
   var legacy=JsonSerializer.Serialize(new{session,kind="knowledge",body="PUBLIC",evidence="PUBLIC evidence",review="PUBLIC review",restriction=new{Origin="slack",DataClass="slack_restricted",ExternalAi="deny",CloudFallback=false}});
   Check(p.Hash==SlackHash.Of(legacy),"persisted proposal hash contract unchanged");
   Check(store.Propose(session,"knowledge","PUBLIC","PUBLIC evidence","PUBLIC review",now).Id==p.Id,"existing proposal deduplicates");
   Check(store.Authorize(p.Id,p.Hash,"PUBLIC-ADMIN",true,"approve",now).Hash==p.Hash,"existing approval hash stays valid");
  }
  foreach(var held in new[]{false,true}){
   var root=Directory.CreateTempSubdirectory("lb-admission-wire-public-").FullName;var options=Options(root);using var store=new SlackStore(options);var now=DateTimeOffset.UtcNow;var ts=now.ToUnixTimeSeconds()+".000001";
   var id=store.Accept(new("EV-WIRE","TTEST","CTEST","UALICE",ts,ts,"PUBLIC",[],now),now.AddSeconds(-1),now).JobId!;var session=store.GetJob(id).SessionId;
   string? frozen=null;var chats=0;var probes=0;
   using var gateway=new GatewayClient(new Uri("https://public-fixture.invalid"),new Handler(async(request,token)=>{
    var text=await request.Content!.ReadAsStringAsync(token);using var d=JsonDocument.Parse(text);Restriction(d.RootElement);
    if(request.RequestUri!.AbsolutePath=="/v1/slack/chat"){chats++;frozen=text;return Reply(new{status="running",retry_safe=false});}
    Check(request.RequestUri.AbsolutePath=="/v1/slack/status","status-only recovery");probes++;
    Check(d.RootElement.GetProperty("request_id").GetString()==id&&d.RootElement.GetProperty("session_id").GetString()==session&&d.RootElement.GetProperty("request_hash").GetString()==SlackStore.ChatHash(frozen!),"exact frozen status identity");
    return Reply(new{state="complete",request_hash=SlackStore.ChatHash(frozen!),result=new{status="complete",text="PUBLIC recovered"}});
   }));
   var inference=new SlackLocalInference(gateway,store);
   try{await inference.Chat(id,session,"PUBLIC","fixture",64,120,CancellationToken.None);throw new Exception("nonterminal accepted");}catch(SlackDeferred){checks++;}
   using(var db=new SqliteConnection("Data Source="+Path.Combine(root,"slack.db"))){db.Open();using var c=db.CreateCommand();c.CommandText=held?"UPDATE inference_progress SET state='held',checks=20,next_check=0 WHERE root_id=$id":"UPDATE inference_progress SET next_check=0 WHERE root_id=$id";c.Parameters.AddWithValue("$id",id);c.ExecuteNonQuery();}
   Check(await inference.Chat(id,session,"PUBLIC","fixture",64,120,CancellationToken.None)=="PUBLIC recovered","valid status recovers saved result");Check(chats==1&&probes==1,"no generation replay");
  }
  using var contract=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","slack-admission-v1.json")));
  foreach(var reason in contract.RootElement.GetProperty("reasons").EnumerateArray().Select(x=>x.GetString()!).Append("SECRET_fixture_prompt\n")){
   var root=Directory.CreateTempSubdirectory("lb-admission-reason-public-").FullName;var options=Options(root);string id,session,frozen;var now=DateTimeOffset.UtcNow;var ts=now.ToUnixTimeSeconds()+".000001";
   using(var store=new SlackStore(options)){
    id=store.Accept(new("EV-REASON","TTEST","CTEST","UALICE",ts,ts,"PUBLIC",[],now),now.AddSeconds(-1),now).JobId!;session=store.GetJob(id).SessionId;
    frozen=store.FreezeInference("/v1/slack/chat",id,session,JsonSerializer.Serialize(new{request_id=id,session_id=session,context="PUBLIC",prompt="fixture",max_tokens=64,timeout_seconds=120,restriction=SlackRestriction.Instance}));
    var plan=store.ChatProgress(id,session,frozen,false);for(var n=0;n<25;n++)store.ChatDeferred(plan,reason);
    if(reason=="foreground_busy"){
     var priorityId=SlackHash.Id();var priorityFrozen=store.FreezeInference("/v1/slack/chat",priorityId,session,JsonSerializer.Serialize(new{request_id=priorityId,session_id=session,context="PUBLIC",prompt="fixture",max_tokens=64,timeout_seconds=120,restriction=SlackRestriction.Instance}));
     var priorityPlan=store.ChatProgress(priorityId,session,priorityFrozen,false);store.SuspendChat(priorityId,"priority_yield_lease");store.ChatDeferred(priorityPlan,"model_starting");var prioritySaved=store.ChatProgress(priorityId,session,priorityFrozen,true);
     Check(prioritySaved.Cause=="priority_yield_lease","admission does not overwrite priority authority");Check(prioritySaved.Hash==priorityPlan.Hash&&prioritySaved.Json==priorityPlan.Json&&prioritySaved.Attempt==0,"deferral preserves frozen retry identity");
     var sendId=SlackHash.Id();var sendFrozen=store.FreezeInference("/v1/slack/chat",sendId,session,JsonSerializer.Serialize(new{request_id=sendId,session_id=session,context="PUBLIC",prompt="fixture",max_tokens=64,timeout_seconds=120,restriction=SlackRestriction.Instance}));
     store.ChatDeferred(store.ChatProgress(sendId,session,sendFrozen,false),"base_starting");
     using(var sendDb=new SqliteConnection("Data Source="+Path.Combine(root,"slack.db"))){sendDb.Open();using var due=sendDb.CreateCommand();due.CommandText="UPDATE inference_progress SET next_check=0 WHERE root_id=$id";due.Parameters.AddWithValue("$id",sendId);due.ExecuteNonQuery();}
     store.MarkChatSending(store.ChatProgress(sendId,session,sendFrozen,true));var sent=store.ChatProgress(sendId,session,sendFrozen,true);
     Check(sent.State=="probing"&&sent.Cause=="none","new dispatch clears stale not-started admission classification");Check(sent.Json==sendFrozen&&sent.Attempt==0,"dispatch fence never rewrites request payload");
    }
    var safe=reason.StartsWith("SECRET")?"admission_unavailable":reason;var saved=store.ChatProgress(id,session,frozen,true);
    Check(saved.State=="abandoned"&&saved.Cause=="admission_"+safe&&saved.Attempt==0,"bounded admission reason retained");Check(store.GetJob(id).Result=="INFERENCE_ADMISSION_EXHAUSTED","admission distinguished from unconfirmed execution");
    var completed=0;while(store.ClaimReply(DateTimeOffset.UtcNow) is {} reply){if(reply.Kind=="completed"){completed++;Check(reply.Text.Contains("推論を開始できませんでした")&&reply.Text.Contains("理由：")&&!reply.Text.Contains("SECRET"),"safe localized admission failure");}store.ReplySent(reply.Id,ts);}Check(completed==1,"one completion notification");
    using var db=new SqliteConnection("Data Source="+Path.Combine(root,"slack.db"));db.Open();using var c=db.CreateCommand();c.CommandText="SELECT count(*) FROM audit WHERE code=$code";c.Parameters.AddWithValue("$code","inference_admission_"+safe);Check((long)c.ExecuteScalar()! ==20,"one fixed audit code per actual deferral");
   }
   using(var reopened=new SlackStore(options)){Check(reopened.ChatProgress(id,session,frozen,true).State=="abandoned"&&reopened.GetJob(id).Result=="INFERENCE_ADMISSION_EXHAUSTED","restart retains cause and terminal state");}
  }
  Console.WriteLine(JsonSerializer.Serialize(new{slack_admission_checks=checks,status="PASS",real_model=false,real_slack=false}));
 }
 static SlackOptions Options(string root)=>new(true,root,"TTEST","CTEST","UBOT",Path.Combine(root,"app"),Path.Combine(root,"bot"));
 static HttpResponseMessage Reply(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value))};
 sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> run):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>run(request,token);}
}
