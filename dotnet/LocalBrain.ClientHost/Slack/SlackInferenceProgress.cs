using System.Text.Json;
using Microsoft.Data.Sqlite;
using LocalBrain.ClientHost.AgentV2;
namespace LocalBrain.ClientHost.Slack;
internal sealed partial class SlackStore {
 internal const int FastProbeLimit=20, HeldProbeLimit=12;
 internal static readonly TimeSpan HeldProbeInterval=TimeSpan.FromMinutes(5);
 internal static readonly TimeSpan MaxHeldAge=TimeSpan.FromHours(1);
 private long nextHeldDeadlineSweep;
 private static readonly Dictionary<string,string> AdmissionReasons=new(StringComparer.Ordinal){
  ["resource_limit_exceeded"]="リソースの空きが不足しています",
  ["resource_probe_failed"]="リソース状態を確認できません",
  ["foreground_busy"]="優先処理の完了待ちです",
  ["background_busy"]="先行処理の完了待ちです",
  ["base_starting"]="基盤サービスの起動待ちです",
  ["base_services_unavailable"]="基盤サービスを利用できません",
  ["base_start_failed"]="基盤サービスを起動できません",
  ["model_starting"]="推論モデルの起動待ちです",
  ["model_start_failed"]="推論モデルを起動できません",
  ["model_not_ready"]="推論モデルの準備が完了していません",
  ["model_start_disabled"]="モデルの自動起動が無効です",
  ["activation_pending"]="Serverの切替確認待ちです",
  ["inference_busy"]="推論処理の空き待ちです",
  ["admission_unavailable"]="Serverが推論を受け付けられません"
 };
 // SQL literal allowlist only; never derived from message/model text. Unknown status
 // may be observed again, but is never authority to replay a generation request.
 internal const string HeldProbeCauses="'none','transport_disconnected','cancellation_pending','server_interrupted','server_nonterminal','priority_yield_lease','priority_yield_probe_failed','priority_yield_legacy_blocked','priority_yield_legacy_unreadable','priority_yield_caller','priority_outer_deadline'";
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
  // A new transport can execute. A prior safe deferral is no longer proof that
  // this dispatch was unstarted if the Host dies before observing its outcome.
  if(Execute("UPDATE inference_progress SET state='probing',next_check=$next,cause=CASE WHEN cause LIKE 'admission_%' THEN 'none' ELSE cause END WHERE root_id=$id AND state='ready' AND attempt=$attempt;",null,("$next",Ms(DateTimeOffset.UtcNow.AddSeconds(5))),("$id",plan.Root),("$attempt",plan.Attempt))!=1)throw new SlackDeferred();return plan;
 }}
 internal void ChatAwaiting(string root,string cause){lock(gate){
  var safe=cause is "transport_disconnected" or "cancellation_pending" or "server_interrupted" or "server_nonterminal"?cause:"cancellation_pending";
  Execute("UPDATE inference_progress SET state='probing',cause=CASE WHEN cause LIKE 'priority_yield_%' THEN cause ELSE $cause END,next_check=$next WHERE root_id=$id AND state NOT IN('complete','legacy_hold','held','abandoned');",null,("$cause",safe),("$next",Ms(DateTimeOffset.UtcNow.AddSeconds(5))),("$id",root));
 }}
 internal void SuspendChat(string root,string cause){
  if(!YieldCodes.Contains(cause))throw new InvalidDataException("SLACK_DEFERRAL_CODE_INVALID");
  lock(gate){Execute("UPDATE inference_progress SET cause=$cause WHERE root_id=$id AND state NOT IN('complete','legacy_hold','held','abandoned');",null,("$cause",cause),("$id",root));}
 }
 private void HoldChat(string root,SqliteTransaction tx,bool recheck=false){
  if(Execute("UPDATE inference_progress SET state='held',next_check=$next WHERE root_id=$id AND state IN('ready','probing');",tx,("$id",root),("$next",recheck?Ms(DateTimeOffset.UtcNow+HeldProbeInterval):long.MaxValue))!=1)return;
  Audit("inference_recovery_held",root,DateTimeOffset.UtcNow,tx);
  if(!recheck){AbandonChat(root,tx);return;}
  if(Scalar("SELECT 1 FROM jobs WHERE id=$id;",tx,("$id",root)) is not null){var job=GetJob(root,tx);var session=GetSession(job.SessionId,tx);QueueReply(root+"-recovery-held",root,session.Channel,session.Thread,"処理状態を5分間隔で自動確認します。安全を確認できた場合だけ再開します。処理ID："+root,"progress",tx);}
 }
 private void AbandonChat(string root,SqliteTransaction tx){
  Job? job=Scalar("SELECT 1 FROM jobs WHERE id=$id;",tx,("$id",root)) is null?null:GetJob(root,tx);
  if(job?.Phase=="complete")return;
  // End the Client job, not the unproven remote execution. Keep immutable
  // identities/receipts until normal retention so duplicates cannot revive it.
  if(Execute("UPDATE inference_progress SET state='abandoned',next_check=$next WHERE root_id=$id AND state IN('held','legacy_hold');",tx,("$id",root),("$next",long.MaxValue))!=1)return;
  Audit("inference_recovery_abandoned",root,DateTimeOffset.UtcNow,tx);
  if(job is null||job.Phase=="failed")return;
  var cause=Scalar("SELECT cause FROM inference_progress WHERE root_id=$id;",tx,("$id",root)) as string??"none";
  var reason=cause.StartsWith("admission_",StringComparison.Ordinal)?cause[10..]:"";
  var admission=AdmissionReasons.TryGetValue(reason,out var explanation);
  Execute("UPDATE jobs SET phase='failed',result=$result WHERE id=$id AND phase NOT IN('complete','failed');",tx,("$id",root),("$result",admission?"INFERENCE_ADMISSION_EXHAUSTED":"RECOVERY_UNCONFIRMED"));
  // Only never-dispatched/retry-safe progress can be removed. Preserve sent,
  // sending and uncertain receipts; do not reset an ambiguous Slack delivery.
  Execute("DELETE FROM replies WHERE job_id=$id AND kind='progress' AND state='pending';",tx,("$id",root));
  var session=GetSession(job.SessionId,tx);
  var message=admission?$"推論を開始できませんでした。受付待ちの上限に達したため、この依頼を終了しました。理由：{explanation}。後ほど再度お試しください。":
   "処理を完了できませんでした。この依頼の自動再開を終了しました。後ほど再度お試しください。";
  QueueReply(root+"-completed",root,session.Channel,session.Thread,$"<@{job.User}> {message}処理ID：{root}","completed",tx);
  Audit("failed",root,DateTimeOffset.UtcNow,tx);
 }
 internal bool BeginHeldProbe(ChatPlan plan,DateTimeOffset now){lock(gate){
  return plan.State=="held"&&Execute("UPDATE inference_progress SET checks=checks+1,next_check=$next WHERE root_id=$id AND session_id=$session AND request_hash=$hash AND state='held' AND attempt=$attempt AND checks>=$fast AND checks<$limit AND next_check<=$now AND cause IN("+HeldProbeCauses+");",null,("$next",Ms(now+HeldProbeInterval)),("$id",plan.Root),("$session",plan.Session),("$hash",plan.Hash),("$attempt",plan.Attempt),("$fast",FastProbeLimit),("$limit",FastProbeLimit+HeldProbeLimit),("$now",Ms(now)))==1;
 }}
 internal void HeldProbeUnresolved(ChatPlan plan)=>HeldProbeUnresolved(plan.Root,plan.Attempt);
 private void HeldProbeUnresolved(string root,int attempt){lock(gate){using var tx=db.BeginTransaction();
  var count=Scalar("SELECT checks FROM inference_progress WHERE root_id=$id AND state='held' AND attempt=$attempt;",tx,("$id",root),("$attempt",attempt));
  if(count is not null&&Convert.ToInt32(count)>=FastProbeLimit+HeldProbeLimit)AbandonChat(root,tx);
  tx.Commit();
 }}
 internal void FinishUnrecoverableChats(DateTimeOffset now){lock(gate){using var tx=db.BeginTransaction();
  // Also close legacy/manual-only holds and recover a Host crash after the last
  // observation reservation. None of these transitions makes a remote call.
  var roots=new List<string>();
  using(var c=Command("SELECT ip.root_id FROM inference_progress ip JOIN jobs j ON j.id=ip.root_id WHERE j.phase='queued' AND (ip.state='legacy_hold' OR (ip.state='held' AND ((ip.checks>=$limit AND ip.next_check<=$now) OR ip.checks<$fast OR ip.next_check=$terminal OR ip.cause NOT IN("+HeldProbeCauses+")))) ORDER BY ip.next_check LIMIT 10;",tx,("$limit",FastProbeLimit+HeldProbeLimit),("$fast",FastProbeLimit),("$terminal",long.MaxValue),("$now",Ms(now))))using(var r=c.ExecuteReader())while(r.Read())roots.Add(r.GetString(0));
  foreach(var root in roots)AbandonChat(root,tx);
  // Wall-clock expiry must not depend on priority admission or successful HTTP
  // probes. Reuse durable metadata-only hold audit times; no new DB schema.
  if(Ms(now)>=nextHeldDeadlineSweep){
   nextHeldDeadlineSweep=Ms(now.AddMinutes(1));
   var candidates=new List<string>();
   using(var c=Command("SELECT ip.root_id FROM inference_progress ip JOIN jobs j ON j.id=ip.root_id WHERE ip.state='held' AND j.phase='queued' ORDER BY j.created LIMIT 1000;",tx))using(var r=c.ExecuteReader())while(r.Read())candidates.Add(r.GetString(0));
   if(candidates.Count>0){
    var parameters=candidates.Select((id,index)=>(Name:"$h"+index,Hash:SlackHash.Of(id))).ToArray();
    var values=parameters.Select(p=>(p.Name,(object?)p.Hash)).ToArray();
    var heldSince=new Dictionary<string,long>(StringComparer.Ordinal);
    using(var c=Command("SELECT object_hash,min(at) FROM audit WHERE code='inference_recovery_held' AND typeof(at)='integer' AND at>=0 AND object_hash IN("+string.Join(",",parameters.Select(p=>p.Name))+") GROUP BY object_hash;",tx,values))using(var r=c.ExecuteReader())while(r.Read())heldSince[r.GetString(0)]=r.GetInt64(1);
    foreach(var id in candidates)if(!heldSince.TryGetValue(SlackHash.Of(id),out var since)||since<=Ms(now-MaxHeldAge)||since>Ms(now.AddMinutes(1)))AbandonChat(id,tx);
   }
  }
  tx.Commit();
 }}
 internal void ObserveHeldChat(ChatPlan plan,JsonElement status){lock(gate){
  if(plan.State!="held")throw new InvalidDataException("SLACK_HELD_PROBE_STATE");
  var state=status.GetProperty("state").GetString();
  if(state!="unknown"&&(!status.TryGetProperty("request_hash",out var hash)||hash.GetString()!=plan.Hash))throw new InvalidDataException("SLACK_ATTEMPT_STATUS_BINDING");
  if(state=="complete"){ChatCompleted(plan,status.GetProperty("result").GetRawText(),allowHeld:true);return;}
  if(CanRetry(plan,status)){
   if(Execute("UPDATE inference_progress SET state='probing' WHERE root_id=$id AND state='held' AND attempt=$attempt AND request_hash=$hash;",null,("$id",plan.Root),("$attempt",plan.Attempt),("$hash",plan.Hash))==1)ObserveChat(plan,status);
   return;
  }
  if(state is "unknown" or "interrupted"){using var tx=db.BeginTransaction();AbandonChat(plan.Root,tx);tx.Commit();return;}
  HeldProbeUnresolved(plan);
 }}
 internal void ChatProbeFailed(ChatPlan plan,bool missingRecord=false){lock(gate){using var tx=db.BeginTransaction();
  Execute("UPDATE inference_progress SET checks=checks+1,next_check=$next WHERE root_id=$id AND state='probing';",tx,("$id",plan.Root),("$next",Ms(DateTimeOffset.UtcNow.AddSeconds(30))));
  if(Convert.ToInt32(Scalar("SELECT checks FROM inference_progress WHERE root_id=$id;",tx,("$id",plan.Root)))>=FastProbeLimit)HoldChat(plan.Root,tx,recheck:!missingRecord);tx.Commit();
 }}
 internal void ChatDeferred(ChatPlan plan,string? reason=null){lock(gate){using var tx=db.BeginTransaction();
  var safe=reason is not null&&AdmissionReasons.ContainsKey(reason)?reason:"admission_unavailable";
  if(Execute("UPDATE inference_progress SET state='ready',checks=checks+1,next_check=$next,cause=CASE WHEN cause LIKE 'priority_yield_%' THEN cause ELSE $cause END WHERE root_id=$id AND attempt=$attempt AND state IN('ready','probing');",tx,("$id",plan.Root),("$attempt",plan.Attempt),("$next",Ms(DateTimeOffset.UtcNow.AddSeconds(30))),("$cause","admission_"+safe))!=1){tx.Commit();return;}
  Audit("inference_admission_"+safe,plan.Root,DateTimeOffset.UtcNow,tx);
  if(Convert.ToInt32(Scalar("SELECT checks FROM inference_progress WHERE root_id=$id;",tx,("$id",plan.Root)))>=FastProbeLimit)HoldChat(plan.Root,tx);tx.Commit();
 }}
 internal void ChatCompleted(ChatPlan plan,string result)=>ChatCompleted(plan,result,allowHeld:false);
 private void ChatCompleted(ChatPlan plan,string result,bool allowHeld){lock(gate){
  if(Scalar("SELECT state FROM inference_progress WHERE root_id=$id;",null,("$id",plan.Root)) as string=="abandoned")throw new InvalidDataException("SLACK_ATTEMPT_ABANDONED");
  var v=DecisionProtocol.Parse(result);if(v.GetProperty("status").GetString()!="complete")throw new InvalidDataException("SLACK_ATTEMPT_RESULT");
  var prior=Scalar("SELECT result FROM inference_progress WHERE root_id=$id;",null,("$id",plan.Root)) as string;
  if(prior is null&&StoredBytes()+System.Text.Encoding.UTF8.GetByteCount(result)>512L*1024*1024)throw new IOException("SLACK_STORAGE_ADMISSION");
  if(prior is not null&&DecisionProtocol.Digest(DecisionProtocol.Parse(prior))!=DecisionProtocol.Digest(v))throw new InvalidDataException("SLACK_ATTEMPT_RESULT_CHANGED");
  Execute("UPDATE inference_progress SET state='complete',result=$result WHERE root_id=$id AND attempt=$attempt AND request_hash=$hash AND (state IN('ready','probing','complete') OR (state='held' AND $allowHeld));",null,("$id",plan.Root),("$attempt",plan.Attempt),("$result",result),("$hash",plan.Hash),("$allowHeld",allowHeld));
 }}
 private static bool CanRetry(ChatPlan plan,JsonElement status)=>status.TryGetProperty("recovery_contract",out var contract)&&contract.GetString()=="quiescent-attempt-v1"&&status.GetProperty("state").GetString()=="interrupted"&&plan.Attempt<2&&plan.Cause.StartsWith("priority_yield_",StringComparison.Ordinal)&&YieldCodes.Contains(plan.Cause)&&status.TryGetProperty("reason",out var reason)&&reason.GetString() is "explicit_cancel" or "foreground_preempted" or "background_preempted";
 internal void ObserveChat(ChatPlan plan,JsonElement status){lock(gate){
  var state=status.GetProperty("state").GetString();
  if(state!="unknown"&&(!status.TryGetProperty("request_hash",out var hash)||hash.GetString()!=plan.Hash))throw new InvalidDataException("SLACK_ATTEMPT_STATUS_BINDING");
  if(state=="complete"){ChatCompleted(plan,status.GetProperty("result").GetRawText());return;}
  if(state is "running" or "unknown" or "unresolved"){ChatProbeFailed(plan,missingRecord:state=="unknown");return;}
  // An interrupted logical run is not authority to create arbitrary new Request IDs.
  // Only a new, tracked priority yield can advance to the authenticated gated retry route.
  var eligible=CanRetry(plan,status);
  if(!eligible){using var hold=db.BeginTransaction();HoldChat(plan.Root,hold);hold.Commit();return;}
  var next=plan.Attempt+1;var values=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(plan.Json)!;values["request_id"]=JsonSerializer.SerializeToElement(AttemptId(plan.Root,next));var json=DecisionProtocol.Utf8.GetString(DecisionProtocol.Canonical(JsonSerializer.SerializeToElement(values)));
  using var tx=db.BeginTransaction();if(Execute("UPDATE inference_progress SET state='ready',attempt=$attempt,json=$json,request_hash=$hash,checks=0,next_check=$next WHERE root_id=$id AND attempt=$old AND state='probing';",tx,("$attempt",next),("$json",json),("$hash",ChatHash(json)),("$next",Ms(DateTimeOffset.UtcNow.AddSeconds(5))),("$id",plan.Root),("$old",plan.Attempt))==1)Audit("inference_retry_reserved",plan.Root,DateTimeOffset.UtcNow,tx);tx.Commit();
 }}
 internal string RetryBody(ChatPlan plan){
  var request=DecisionProtocol.Parse(plan.Json);var previous=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(plan.Json)!;previous["request_id"]=JsonSerializer.SerializeToElement(AttemptId(plan.Root,plan.Attempt-1));var old=JsonSerializer.Serialize(previous);
  return JsonSerializer.Serialize(new{logical_id=plan.Root,previous_request_id=AttemptId(plan.Root,plan.Attempt-1),previous_request_hash=ChatHash(old),request,cause=plan.Cause,restriction=SlackRestriction.Instance},SlackOptions.Json);
 }
}
