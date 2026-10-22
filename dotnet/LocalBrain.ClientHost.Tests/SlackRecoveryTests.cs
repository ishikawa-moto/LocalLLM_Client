using System.Text.Json;
using LocalBrain.ClientHost.Slack;
internal static class SlackRecoveryTests {
 internal static void Run(){
  var checks=0;void Check(bool v,string n){if(!v)throw new Exception("SLACK_RECOVERY_TEST:"+n);checks++;}
  var root=Directory.CreateTempSubdirectory("lb-recovery-public-").FullName;
  var options=new SlackOptions(true,root,"TTEST","CTEST","UBOT",Path.Combine(root,"app"),Path.Combine(root,"bot"));
  string session;var id="PUBLIC_RECOVERY_001";var now=DateTimeOffset.UtcNow;var ts=now.ToUnixTimeSeconds()+".000001";
  using var restricted=RestrictedExecution.EnterSlack();
  using(var store=new SlackStore(options)){
   var job=store.Accept(new("EV-RECOVERY","TTEST","CTEST","UALICE",ts,ts,"PUBLIC",[],now),now.AddSeconds(-1),now).JobId!;session=store.GetJob(job).SessionId;
   string Freeze(string request)=>store.FreezeInference("/v1/slack/chat",request,session,JsonSerializer.Serialize(new{request_id=request,session_id=session,context="PUBLIC",prompt="fixture",max_tokens=64,timeout_seconds=120,restriction=SlackRestriction.Instance}));
   var frozen=Freeze(id);var plan=store.ChatProgress(id,session,frozen,false);Check(plan.State=="ready"&&plan.Attempt==0,"new request only");store.MarkChatSending(plan);
   plan=store.ChatProgress(id,session,frozen,true);Check(plan.State=="probing","durable send fence before transport");
   store.SuspendChat(id,"priority_yield_lease");plan=store.ChatProgress(id,session,frozen,true);
   store.ObserveChat(plan,JsonSerializer.SerializeToElement(new{state="running",request_hash=plan.Hash}));Check(store.ChatProgress(id,session,frozen,true).Attempt==0,"running predecessor cannot create next attempt");
   try{store.ObserveChat(plan,JsonSerializer.SerializeToElement(new{state="interrupted",request_hash=new string('0',64),reason="explicit_cancel",recovery_contract="quiescent-attempt-v1"}));throw new Exception("hash mismatch accepted");}catch(InvalidDataException){checks++;}
   for(var attempt=1;attempt<=2;attempt++){
    plan=store.ChatProgress(id,session,frozen,true);store.ObserveChat(plan,JsonSerializer.SerializeToElement(new{state="interrupted",request_hash=plan.Hash,reason="explicit_cancel",recovery_contract="quiescent-attempt-v1"}));
    plan=store.ChatProgress(id,session,frozen,true);Check(plan.Attempt==attempt&&plan.State=="ready","bounded durable attempt");
    using var body=JsonDocument.Parse(store.RetryBody(plan));var r=body.RootElement;Check(r.GetProperty("previous_request_id").GetString()==SlackStore.AttemptId(id,attempt-1)&&r.GetProperty("request").GetProperty("timeout_seconds").GetInt32()==120,"exact predecessor and frozen timeout");
    store.ChatAwaiting(id,"server_interrupted");
   }
   plan=store.ChatProgress(id,session,frozen,true);store.ObserveChat(plan,JsonSerializer.SerializeToElement(new{state="interrupted",request_hash=plan.Hash,reason="explicit_cancel",recovery_contract="quiescent-attempt-v1"}));Check(store.ChatProgress(id,session,frozen,true).State=="held","three total attempts then hold");
   var legacy="PUBLIC_LEGACY_001";var old=Freeze(legacy);Check(store.ChatProgress(legacy,session,old,true).State=="legacy_hold","old frozen request never replayed");
   var unknown="PUBLIC_UNKNOWN_001";var raw=Freeze(unknown);var u=store.ChatProgress(unknown,session,raw,false);store.MarkChatSending(u);for(var check=0;check<20;check++)store.ObserveChat(u,JsonSerializer.SerializeToElement(new{state="unknown",retry_safe=false}));Check(store.ChatProgress(unknown,session,raw,true).State=="held","unknown state never inferred safe");
   var recovered="PUBLIC_COMPLETE_001";var text=Freeze(recovered);var p=store.ChatProgress(recovered,session,text,false);store.MarkChatSending(p);store.ObserveChat(p,JsonSerializer.SerializeToElement(new{state="complete",request_hash=p.Hash,result=new{status="complete",text="PUBLIC recovered result"}}));Check(store.ChatProgress(recovered,session,text,true).Result!.Contains("PUBLIC recovered result"),"lost response recovered by exact status");
   try{store.Defer(job,"priority_arbitrary_secret");throw new Exception("unfixed audit code accepted");}catch(InvalidDataException){checks++;}
  }
  using(var reopened=new SlackStore(options)){Check(reopened.ChatProgress(id,session,"{}",true).State=="held","restart preserves held state and attempt count");}
  Console.WriteLine(JsonSerializer.Serialize(new{slack_recovery_checks=checks,status="PASS",real_model=false,real_slack=false}));
 }
}
