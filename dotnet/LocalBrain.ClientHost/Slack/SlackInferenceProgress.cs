using System.Text.Json;
using Microsoft.Data.Sqlite;
using LocalBrain.ClientHost.AgentV2;
namespace LocalBrain.ClientHost.Slack;
internal sealed partial class SlackStore {
 private const string InferenceProgressSchema="""
 CREATE TABLE IF NOT EXISTS inference_progress(root_id TEXT PRIMARY KEY,session_id TEXT NOT NULL,state TEXT NOT NULL,attempt INTEGER NOT NULL,json TEXT NOT NULL,request_hash TEXT NOT NULL,cause TEXT NOT NULL,result TEXT,next_check INTEGER NOT NULL,checks INTEGER NOT NULL DEFAULT 0);
 """;
 internal sealed record ChatPlan(string Root,string Session,string State,int Attempt,string Json,string Hash,string Cause,string? Result,long NextCheck){
  internal string Request=>DecisionProtocol.Parse(Json).GetProperty("request_id").GetString()!;
 }
 internal bool HasFrozenInference(string root){lock(gate)return Scalar("SELECT 1 FROM inference WHERE request_id=$id;",null,("$id",root)) is not null;}
 internal static string AttemptId(string root,int attempt)=>attempt==0?root:SlackHash.Of("slack-attempt-v1:"+root+":"+attempt)[..32];
 internal static string ChatHash(string json)=>DecisionProtocol.Digest(JsonSerializer.SerializeToElement(new{action="chat",value=DecisionProtocol.Parse(json)}));
 internal ChatPlan ChatProgress(string root,string session,string frozen,bool existed){lock(gate){
  if(Scalar("SELECT 1 FROM inference_progress WHERE root_id=$id;",null,("$id",root)) is null&&StoredBytes()+System.Text.Encoding.UTF8.GetByteCount(frozen)>512L*1024*1024)throw new IOException("SLACK_STORAGE_ADMISSION");
  Execute("INSERT OR IGNORE INTO inference_progress VALUES($id,$session,$state,0,$json,$hash,'none',NULL,0,0);",null,("$id",root),("$session",session),("$state",existed?"legacy_hold":"ready"),("$json",frozen),("$hash",ChatHash(frozen)));
  using var c=Command("SELECT session_id,state,attempt,json,request_hash,cause,result,next_check FROM inference_progress WHERE root_id=$id;",null,("$id",root));using var r=c.ExecuteReader();if(!r.Read())throw new InvalidDataException("SLACK_ATTEMPT_MISSING");
  var value=new ChatPlan(root,r.GetString(0),r.GetString(1),r.GetInt32(2),r.GetString(3),r.GetString(4),r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.GetInt64(7));
  if(value.Session!=session||ChatHash(value.Json)!=value.Hash||value.Attempt is <0 or >2||value.Request!=AttemptId(root,value.Attempt))throw new InvalidDataException("SLACK_ATTEMPT_DRIFT");return value;
 }}
 internal ChatPlan MarkChatSending(ChatPlan plan){lock(gate){
  if(plan.State!="ready"||plan.NextCheck>Ms(DateTimeOffset.UtcNow))throw new SlackDeferred();
  if(Execute("UPDATE inference_progress SET state='probing',next_check=$next WHERE root_id=$id AND state='ready' AND attempt=$attempt;",null,("$next",Ms(DateTimeOffset.UtcNow.AddSeconds(5))),("$id",plan.Root),("$attempt",plan.Attempt))!=1)throw new SlackDeferred();return plan;
 }}
 internal void ChatAwaiting(string root,string cause){lock(gate){
  var safe=cause is "transport_disconnected" or "cancellation_pending" or "server_interrupted" or "server_nonterminal"?cause:"cancellation_pending";
  Execute("UPDATE inference_progress SET state='probing',cause=CASE WHEN cause LIKE 'priority_yield_%' THEN cause ELSE $cause END,next_check=$next WHERE root_id=$id AND state NOT IN('complete','legacy_hold','held');",null,("$cause",safe),("$next",Ms(DateTimeOffset.UtcNow.AddSeconds(5))),("$id",root));
 }}
 internal void SuspendChat(string root,string cause){
  if(!YieldCodes.Contains(cause))throw new InvalidDataException("SLACK_DEFERRAL_CODE_INVALID");
  lock(gate){Execute("UPDATE inference_progress SET cause=$cause WHERE root_id=$id AND state NOT IN('complete','legacy_hold','held');",null,("$cause",cause),("$id",root));}
 }
 private void HoldChat(string root,SqliteTransaction tx){
  if(Execute("UPDATE inference_progress SET state='held' WHERE root_id=$id AND state!='held';",tx,("$id",root))!=1)return;
  Audit("inference_recovery_held",root,DateTimeOffset.UtcNow,tx);
  if(Scalar("SELECT 1 FROM jobs WHERE id=$id;",tx,("$id",root)) is not null){var job=GetJob(root,tx);var session=GetSession(job.SessionId,tx);QueueReply(root+"-recovery-held",root,session.Channel,session.Thread,"処理は安全な復旧確認待ちです。自動再送を停止しています。処理ID："+root,"progress",tx);}
 }
 internal void ChatProbeFailed(ChatPlan plan){lock(gate){using var tx=db.BeginTransaction();
  Execute("UPDATE inference_progress SET checks=checks+1,next_check=$next WHERE root_id=$id AND state='probing';",tx,("$id",plan.Root),("$next",Ms(DateTimeOffset.UtcNow.AddSeconds(30))));
  if(Convert.ToInt32(Scalar("SELECT checks FROM inference_progress WHERE root_id=$id;",tx,("$id",plan.Root)))>=20)HoldChat(plan.Root,tx);tx.Commit();
 }}
 internal void ChatDeferred(ChatPlan plan){lock(gate){using var tx=db.BeginTransaction();
  Execute("UPDATE inference_progress SET state='ready',checks=checks+1,next_check=$next WHERE root_id=$id AND attempt=$attempt AND state IN('ready','probing');",tx,("$id",plan.Root),("$attempt",plan.Attempt),("$next",Ms(DateTimeOffset.UtcNow.AddSeconds(30))));
  if(Convert.ToInt32(Scalar("SELECT checks FROM inference_progress WHERE root_id=$id;",tx,("$id",plan.Root)))>=20)HoldChat(plan.Root,tx);tx.Commit();
 }}
 internal void ChatCompleted(ChatPlan plan,string result){lock(gate){
  var v=DecisionProtocol.Parse(result);if(v.GetProperty("status").GetString()!="complete")throw new InvalidDataException("SLACK_ATTEMPT_RESULT");
  var prior=Scalar("SELECT result FROM inference_progress WHERE root_id=$id;",null,("$id",plan.Root)) as string;
  if(prior is null&&StoredBytes()+System.Text.Encoding.UTF8.GetByteCount(result)>512L*1024*1024)throw new IOException("SLACK_STORAGE_ADMISSION");
  if(prior is not null&&DecisionProtocol.Digest(DecisionProtocol.Parse(prior))!=DecisionProtocol.Digest(v))throw new InvalidDataException("SLACK_ATTEMPT_RESULT_CHANGED");
  Execute("UPDATE inference_progress SET state='complete',result=$result WHERE root_id=$id AND attempt=$attempt AND state NOT IN('held','legacy_hold');",null,("$id",plan.Root),("$attempt",plan.Attempt),("$result",result));
 }}
 internal void ObserveChat(ChatPlan plan,JsonElement status){lock(gate){
  var state=status.GetProperty("state").GetString();
  if(state!="unknown"&&(!status.TryGetProperty("request_hash",out var hash)||hash.GetString()!=plan.Hash))throw new InvalidDataException("SLACK_ATTEMPT_STATUS_BINDING");
  if(state=="complete"){ChatCompleted(plan,status.GetProperty("result").GetRawText());return;}
  if(state is "running" or "unknown" or "unresolved"){ChatProbeFailed(plan);return;}
  // An interrupted logical run is not authority to create arbitrary new Request IDs.
  // Only a new, tracked priority yield can advance to the authenticated gated retry route.
  var eligible=status.TryGetProperty("recovery_contract",out var contract)&&contract.GetString()=="quiescent-attempt-v1"&&state=="interrupted"&&plan.Attempt<2&&plan.Cause.StartsWith("priority_yield_",StringComparison.Ordinal)&&YieldCodes.Contains(plan.Cause)&&status.TryGetProperty("reason",out var reason)&&reason.GetString() is "explicit_cancel" or "foreground_preempted" or "background_preempted";
  if(!eligible){using var hold=db.BeginTransaction();HoldChat(plan.Root,hold);hold.Commit();return;}
  var next=plan.Attempt+1;var values=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(plan.Json)!;values["request_id"]=JsonSerializer.SerializeToElement(AttemptId(plan.Root,next));var json=DecisionProtocol.Utf8.GetString(DecisionProtocol.Canonical(JsonSerializer.SerializeToElement(values)));
  using var tx=db.BeginTransaction();Execute("UPDATE inference_progress SET state='ready',attempt=$attempt,json=$json,request_hash=$hash,checks=0,next_check=$next WHERE root_id=$id AND attempt=$old AND state='probing';",tx,("$attempt",next),("$json",json),("$hash",ChatHash(json)),("$next",Ms(DateTimeOffset.UtcNow.AddSeconds(5))),("$id",plan.Root),("$old",plan.Attempt));Audit("inference_retry_reserved",plan.Root,DateTimeOffset.UtcNow,tx);tx.Commit();
 }}
 internal string RetryBody(ChatPlan plan){
  var request=DecisionProtocol.Parse(plan.Json);var previous=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(plan.Json)!;previous["request_id"]=JsonSerializer.SerializeToElement(AttemptId(plan.Root,plan.Attempt-1));var old=JsonSerializer.Serialize(previous);
  return JsonSerializer.Serialize(new{logical_id=plan.Root,previous_request_id=AttemptId(plan.Root,plan.Attempt-1),previous_request_hash=ChatHash(old),request,cause=plan.Cause,restriction=SlackRestriction.Instance},SlackOptions.Json);
 }
}
