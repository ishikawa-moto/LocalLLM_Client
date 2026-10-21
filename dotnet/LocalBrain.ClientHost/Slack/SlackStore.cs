using System.Text.Json;
using Microsoft.Data.Sqlite;
using LocalBrain.ClientHost.AgentV2;

namespace LocalBrain.ClientHost.Slack;

// Single Host owner; separate SQLite authority prevents restricted rows entering ordinary knowledge/export packets.
internal sealed partial class SlackStore : IDisposable
{
    private readonly object gate = new();
    private readonly SqliteConnection db;
    private readonly SlackOptions options;
    internal sealed record Session(string Id,string Team,string Channel,string Thread,string Owner,long LastMention,string? Summary,string? SummaryHash){public SlackRestriction Restriction=>SlackRestriction.Instance;}
    internal sealed record Job(string Id,string EventId,string SessionId,string User,string Text,string[] FileIds,string Phase,string? Result,long Created){public SlackRestriction Restriction=>SlackRestriction.Instance;}
    internal sealed record Reply(string Id,string JobId,string Channel,string Thread,string Text,string Kind,string State,int Attempts,long RetryAfter);
    internal sealed record Intake(string Status,string? JobId);
    internal sealed record Proposal(string Id,string SessionId,string Kind,string Body,string Evidence,string Review,string Hash,long Expires,string State){public SlackRestriction Restriction=>SlackRestriction.Instance;}
    internal sealed record Attachment(string JobId,string FileId,string Hash,string Extension,byte[] Bytes,string? OcrJob,string? Result){public SlackRestriction Restriction=>SlackRestriction.Instance;}
    internal SlackStore(SlackOptions options)
    {
        this.options=options;options.Validate();CanonicalStore.GuardPath(options.Root);Directory.CreateDirectory(options.Root);
        var path=Path.Combine(options.Root,"slack.db");CanonicalStore.GuardPath(path);
        db=new(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());db.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=1000;"+Schema+AdoptionSchema+ResultFileSchema+SummaryCheckpointSchema);
        Execute("DELETE FROM replies WHERE kind='error' AND job_id='' AND state='sent';");
    }
    private int Execute(string sql,SqliteTransaction? tx=null,params (string,object?)[] args) {using var c=Command(sql,tx,args);return c.ExecuteNonQuery();}
    private SqliteCommand Command(string sql,SqliteTransaction? tx=null,params (string,object?)[] args) {var c=db.CreateCommand();c.CommandText=sql;c.Transaction=tx;foreach(var (k,v) in args)c.Parameters.AddWithValue(k,v??DBNull.Value);return c;}
    private object? Scalar(string sql,SqliteTransaction? tx=null,params (string,object?)[] args) {using var c=Command(sql,tx,args);return c.ExecuteScalar();}
    private static long Ms(DateTimeOffset time)=>time.ToUnixTimeMilliseconds();
    internal void BeginOnline(string epoch,DateTimeOffset at) {lock(gate){Execute("INSERT INTO epochs VALUES($id,$at,NULL);",null,("$id",epoch),("$at",Ms(at)));}}
    internal void EndOnline(string epoch,DateTimeOffset at){lock(gate){Execute("UPDATE epochs SET ended=$at WHERE id=$id AND ended IS NULL;",null,("$at",Ms(at)),("$id",epoch));}}
    internal Intake Accept(SlackMention m,DateTimeOffset onlineSince,DateTimeOffset now)
    {
        lock(gate){using var tx=db.BeginTransaction();
            // Previously persisted jobs are never rejected as an offline resend; they simply are not enqueued twice.
            var known=Scalar("SELECT id FROM jobs WHERE event_id=$event;",tx,("$event",m.EventId));
            if(known is string old){tx.Commit();return new("duplicate",old);}
            var eventAt=SlackMention.Timestamp(m.Ts);
            if(m.TeamId!=options.TeamId||m.ChannelId!=options.ChannelId||eventAt<onlineSince||eventAt>now.AddSeconds(10)||m.ReceivedAt<onlineSince){tx.Commit();return new("offline_or_foreign",null);}
            var session=FindSession(m.TeamId,m.ChannelId,m.RootTs,tx);
            if(session is not null && session.LastMention<=Ms(now.AddDays(-30))){
                if(HasActive(session.Id,tx)){tx.Commit();return new("session_busy",null);}
                DeleteSession(session.Id,"expired",now,tx);
                if(FindSession(m.TeamId,m.ChannelId,m.RootTs,tx) is not null){tx.Commit();return new("session_pending_approval",null);}session=null;
            }
            if(session is not null&&session.Owner!=m.UserId){tx.Commit();return new("foreign_owner",null);}
            var count=Convert.ToInt32(Scalar("SELECT count(*) FROM jobs WHERE phase NOT IN ('complete','failed');",tx));
            if(count>=options.MaxQueue){
                var refusalPending=Convert.ToInt32(Scalar("SELECT count(*) FROM replies WHERE kind='error' AND job_id='' AND state IN ('pending','sending','uncertain');",tx))>0;
                if(!refusalPending)QueueReply("refused-"+m.EventId,"",m.ChannelId,m.RootTs,"現在は受付できません。時間をおいて再度メンションしてください。","error",tx);
                tx.Commit();return new("queue_full",null);
            }
            var id=SlackHash.Id();
            if(session is null){session=new(SlackHash.Id(),m.TeamId,m.ChannelId,m.RootTs,m.UserId,Ms(eventAt),null,null);
                Execute("INSERT INTO sessions VALUES($id,$team,$channel,$thread,$owner,$at,NULL,NULL);",tx,("$id",session.Id),("$team",session.Team),("$channel",session.Channel),("$thread",session.Thread),("$owner",session.Owner),("$at",session.LastMention));}
            else Execute("UPDATE sessions SET last_mention=max(last_mention,$at) WHERE id=$id;",tx,("$at",Ms(eventAt)),("$id",session.Id));
            Execute("INSERT INTO jobs VALUES($id,$event,$session,$user,$text,$files,'queued',NULL,$at);",tx,("$id",id),("$event",m.EventId),("$session",session.Id),("$user",m.UserId),("$text",m.Text),("$files",JsonSerializer.Serialize(m.FileIds)),("$at",Ms(now)));
            QueueReply(id+"-accepted",id,m.ChannelId,m.RootTs,"受け付けました。","accepted",tx);
            Audit("registered",id,now,tx);tx.Commit();return new("queued",id);
        }
    }
    private Session? FindSession(string team,string channel,string thread,SqliteTransaction? tx=null){using var c=Command("SELECT * FROM sessions WHERE team=$t AND channel=$c AND thread=$r;",tx,("$t",team),("$c",channel),("$r",thread));using var r=c.ExecuteReader();return r.Read()?ReadSession(r):null;}
    private static Session ReadSession(SqliteDataReader r)=>new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetInt64(5),r.IsDBNull(6)?null:r.GetString(6),r.IsDBNull(7)?null:r.GetString(7));
    internal Session GetSession(string id,SqliteTransaction? tx=null){lock(gate){using var c=Command("SELECT * FROM sessions WHERE id=$id;",tx,("$id",id));using var r=c.ExecuteReader();return r.Read()?ReadSession(r):throw new KeyNotFoundException();}}
    private static Job ReadJob(SqliteDataReader r)=>new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),JsonSerializer.Deserialize<string[]>(r.GetString(5))!,r.GetString(6),r.IsDBNull(7)?null:r.GetString(7),r.GetInt64(8));
    internal Job GetJob(string id,SqliteTransaction? tx=null){lock(gate){using var c=Command("SELECT * FROM jobs WHERE id=$id;",tx,("$id",id));using var r=c.ExecuteReader();return r.Read()?ReadJob(r):throw new KeyNotFoundException();}}
    internal Job? ClaimNext(){lock(gate){using var tx=db.BeginTransaction();
        using var c=Command("SELECT j.* FROM jobs j WHERE j.phase='queued' AND EXISTS(SELECT 1 FROM replies r WHERE r.job_id=j.id AND r.kind='accepted' AND r.state='sent') AND NOT EXISTS(SELECT 1 FROM jobs p WHERE p.session_id=j.session_id AND p.phase NOT IN('complete','failed') AND (p.created<j.created OR (p.created=j.created AND p.rowid<j.rowid))) ORDER BY CASE WHEN j.files='[]' THEN 4 ELSE 3 END,j.created,j.rowid LIMIT 1;",tx);
        Job? j;using(var r=c.ExecuteReader())j=r.Read()?ReadJob(r):null;
        if(j is not null){Execute("UPDATE jobs SET phase='running' WHERE id=$id AND phase='queued';",tx,("$id",j.Id));Audit("started",j.Id,DateTimeOffset.UtcNow,tx);}tx.Commit();return j;
    }}
    internal void Recover(){lock(gate){using var tx=db.BeginTransaction();Execute("UPDATE jobs SET phase='queued' WHERE phase='running'; UPDATE replies SET state='uncertain' WHERE state='sending'; UPDATE proposals SET state='apply_uncertain' WHERE state='applying'; UPDATE result_files SET state='uncertain' WHERE state='sending';",tx);tx.Commit();}}
    internal void Defer(string id){lock(gate)Execute("UPDATE jobs SET phase='queued' WHERE id=$id AND phase='running';",null,("$id",id));}
    internal void WaitForSummary(string id,int maxBytes=SlackConversation.SummaryBytes){lock(gate){
        var job=GetJob(id);var session=GetSession(job.SessionId);
        var phase=session.Summary is not null&&session.SummaryHash==HistoryHash(session.Id)&&SlackConversation.EncodedBytes(session.Summary)<=maxBytes?"queued":"waiting_summary";
        Execute("UPDATE jobs SET phase=$phase WHERE id=$id AND phase='running';",null,("$phase",phase),("$id",id));
    }}
    internal void SummaryReady(string session){lock(gate)Execute("UPDATE jobs SET phase='queued' WHERE session_id=$id AND phase='waiting_summary';",null,("$id",session));}
    internal int FailWaitingSummary(string session){lock(gate){using var tx=db.BeginTransaction();var waiting=new List<Job>();using(var c=Command("SELECT * FROM jobs WHERE session_id=$id AND phase='waiting_summary';",tx,("$id",session)))using(var r=c.ExecuteReader())while(r.Read())waiting.Add(ReadJob(r));var failed=0;
        foreach(var job in waiting){if(Execute("UPDATE jobs SET phase='failed',result='LOCAL_COMPRESSION_UNAVAILABLE' WHERE id=$id AND phase='waiting_summary';",tx,("$id",job.Id))==0)continue;
            var s=GetSession(job.SessionId,tx);QueueReply(job.Id+"-completed",job.Id,s.Channel,s.Thread,$"<@{job.User}> 処理を完了できませんでした。処理ID：{job.Id}。管理者に確認してください。","completed",tx);Audit("failed",job.Id,DateTimeOffset.UtcNow,tx);failed++;}
        tx.Commit();return failed;
    }}
    internal void WaitForOcr(string id){lock(gate)Execute("UPDATE jobs SET phase='waiting_ocr' WHERE id=$id AND phase='running';",null,("$id",id));}
    internal Job[] WaitingForOcr(){lock(gate){using var c=Command("SELECT * FROM jobs WHERE phase='waiting_ocr';");using var r=c.ExecuteReader();var list=new List<Job>();while(r.Read())list.Add(ReadJob(r));return list.ToArray();}}
    internal void OcrReady(string id){lock(gate)Execute("UPDATE jobs SET phase='queued' WHERE id=$id AND phase='waiting_ocr';",null,("$id",id));}
    internal void Finish(string id,string localResult,bool success,bool sensitive){lock(gate){using var tx=db.BeginTransaction();var j=GetJob(id,tx);if(j.Phase is "complete" or "failed"){tx.Commit();return;}
        if(localResult.Length>64000)throw new InvalidDataException("SLACK_RESULT_SIZE");
        Execute("UPDATE jobs SET phase=$phase,result=$result WHERE id=$id;",tx,("$phase",success?"complete":"failed"),("$result",localResult),("$id",id));
        var s=GetSession(j.SessionId,tx);
        if(success&&sensitive&&options.ReturnFileResults)QueueSuccessfulFileResults(j,localResult,tx);
        else{
            var text=success?(sensitive?$"<@{j.User}> 処理が完了しました。結果はローカルに保存しました。処理ID：{id}":$"<@{j.User}> "+SafeText(localResult)):$"<@{j.User}> 処理を完了できませんでした。処理ID：{id}。管理者に確認してください。";
            QueueReply(id+"-completed",id,s.Channel,s.Thread,text,"completed",tx);
        }
        Audit(success?"completed":"failed",id,DateTimeOffset.UtcNow,tx);tx.Commit();
    }}
    internal static string SafeText(string text)=>text.Replace("<!","&lt;!",StringComparison.Ordinal).Replace("<@","&lt;@",StringComparison.Ordinal)[..Math.Min(text.Length,3000)];
    private void QueueReply(string id,string job,string channel,string thread,string text,string kind,SqliteTransaction tx)=>Execute("INSERT INTO replies VALUES($id,$job,$channel,$thread,$text,$kind,'pending',0,0,NULL) ON CONFLICT(id) DO NOTHING;",tx,("$id",id),("$job",job),("$channel",channel),("$thread",thread),("$text",text),("$kind",kind));
    internal Reply? ClaimReply(DateTimeOffset now){lock(gate){using var tx=db.BeginTransaction();using var c=Command("SELECT id,job_id,channel,thread,text,kind,state,attempts,retry_after FROM replies WHERE state='pending' AND retry_after<=$now ORDER BY CASE kind WHEN 'accepted' THEN 0 ELSE 1 END,rowid LIMIT 1;",tx,("$now",Ms(now)));Reply? p;
        using(var r=c.ExecuteReader())p=r.Read()?new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetInt32(7),r.GetInt64(8)):null;
        if(p is not null)Execute("UPDATE replies SET state='sending',attempts=attempts+1 WHERE id=$id;",tx,("$id",p.Id));tx.Commit();return p;
    }}
    private void DeleteConfirmedRefusal(string id,SqliteTransaction tx)=>Execute("DELETE FROM replies WHERE id=$id AND kind='error' AND job_id='' AND state='sent';",tx,("$id",id));
    internal void ReplySent(string id,string ts){lock(gate){using var tx=db.BeginTransaction();Execute("UPDATE replies SET state='sent',remote_ts=$ts WHERE id=$id AND state IN ('sending','uncertain');",tx,("$ts",ts),("$id",id));DeleteConfirmedRefusal(id,tx);tx.Commit();}}
    internal void ReplyRetry(string id,DateTimeOffset at){lock(gate)Execute("UPDATE replies SET state='pending',retry_after=$at WHERE id=$id AND state='sending';",null,("$at",Ms(at)),("$id",id));}
    internal void ReplyUncertain(string id){lock(gate)Execute("UPDATE replies SET state='uncertain' WHERE id=$id AND state='sending';",null,("$id",id));}
    internal Reply[] UncertainReplies(){lock(gate){using var c=Command("SELECT id,job_id,channel,thread,text,kind,state,attempts,retry_after FROM replies WHERE state='uncertain' ORDER BY rowid LIMIT 100;");using var r=c.ExecuteReader();var list=new List<Reply>();while(r.Read())list.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetInt32(7),r.GetInt64(8)));return list.ToArray();}}
    internal static string ReplyHash(Reply r)=>SlackHash.Of(JsonSerializer.Serialize(new{r.Id,r.JobId,r.Channel,r.Thread,r.Text,r.Kind}));
    internal void ReconcileReply(string id,string hash,string remoteTs,string actor){lock(gate){var r=UncertainReplies().Single(r=>r.Id==id);if(ReplyHash(r)!=hash||!SlackMention.ValidTs(remoteTs)||string.IsNullOrWhiteSpace(actor))throw new UnauthorizedAccessException("SLACK_REPLY_RECONCILIATION_DENIED");using var tx=db.BeginTransaction();Execute("UPDATE replies SET state='sent',remote_ts=$ts WHERE id=$id AND state='uncertain';",tx,("$ts",remoteTs),("$id",id));DeleteConfirmedRefusal(id,tx);Audit("human_delivery_confirmed",id+actor,DateTimeOffset.UtcNow,tx);tx.Commit();}}
    internal IReadOnlyList<Job> History(string session){lock(gate){using var c=Command("SELECT * FROM jobs WHERE session_id=$id ORDER BY created,rowid;",null,("$id",session));using var r=c.ExecuteReader();var list=new List<Job>();while(r.Read())list.Add(ReadJob(r));return list;}}
    internal (Job[] Jobs,string Hash) Snapshot(string id){lock(gate){
        var jobs=History(id).ToArray();
        var sources=jobs.Where(x=>x.Phase=="complete").Select(x=>new{x.Id,x.EventId,x.Text,x.Result,x.FileIds,
            attachments=x.FileIds.Select(f=>GetAttachment(x.Id,f)).Select(a=>a is null?null:new{a.FileId,a.Hash,result_hash=a.Result is null?null:SlackHash.Of(a.Result)})});
        return(jobs,SlackHash.Of("slack-summary-v2:"+JsonSerializer.Serialize(sources)));
    }}
    internal string HistoryHash(string id)=>Snapshot(id).Hash;
    internal string FreezeInference(string route,string request,string session,string json){lock(gate){
        if(!RestrictedExecution.Active||route is not("/v1/slack/chat" or "/v1/slack/review"))throw new UnauthorizedAccessException("SLACK_INFERENCE_OWNER_REQUIRED");
        GetSession(session);var value=DecisionProtocol.Parse(json);
        if(value.GetProperty("request_id").GetString()!=request||value.GetProperty("session_id").GetString()!=session)throw new InvalidDataException("SLACK_INFERENCE_IDENTITY");
        using var tx=db.BeginTransaction();using var c=Command("SELECT route,session_id,json,hash FROM inference WHERE request_id=$id;",tx,("$id",request));
        string? old=null;using(var r=c.ExecuteReader()){if(r.Read()){if(r.GetString(0)!=route||r.GetString(1)!=session||DecisionProtocol.Digest(DecisionProtocol.Parse(r.GetString(2)))!=r.GetString(3))throw new InvalidDataException("SLACK_INFERENCE_RESERVATION_DRIFT");old=r.GetString(2);}}
        if(old is not null){tx.Commit();return old;}
        var canonical=DecisionProtocol.Utf8.GetString(DecisionProtocol.Canonical(value));Execute("INSERT INTO inference VALUES($id,$route,$session,$json,$hash);",tx,("$id",request),("$route",route),("$session",session),("$json",canonical),("$hash",DecisionProtocol.Digest(value)));tx.Commit();return canonical;
    }}
    internal bool SaveSummary(string id,string summary,string expectedHistory){lock(gate){if(summary.Length>16000||HistoryHash(id)!=expectedHistory)return false;using var tx=db.BeginTransaction();Execute("UPDATE sessions SET summary=$s,summary_hash=$h WHERE id=$id; UPDATE jobs SET phase='queued' WHERE session_id=$id AND phase='waiting_summary';",tx,("$s",summary),("$h",expectedHistory),("$id",id));tx.Commit();return true;}}
    private bool HasActive(string id,SqliteTransaction? tx)=>Convert.ToInt32(Scalar("SELECT count(*) FROM result_files WHERE job_id IN(SELECT id FROM jobs WHERE session_id=$id) AND state!='sent';",tx,("$id",id)))>0 || Convert.ToInt32(Scalar("SELECT count(*) FROM applications WHERE session_id=$id AND state IN ('reserved','sending','uncertain','unresolved');",tx,("$id",id)))>0 || Convert.ToInt32(Scalar("SELECT count(*) FROM jobs WHERE session_id=$id AND phase NOT IN ('complete','failed');",tx,("$id",id)))>0 || Convert.ToInt32(Scalar("SELECT count(*) FROM replies WHERE job_id IN(SELECT id FROM jobs WHERE session_id=$id) AND state!='sent';",tx,("$id",id)))>0;
    private void DeleteSession(string id,string reason,DateTimeOffset now,SqliteTransaction tx){
        Execute("INSERT OR IGNORE INTO retirements VALUES($session,$request,'pending');",tx,("$session",id),("$request",SlackHash.Id()));
        var bytes=Convert.ToInt64(Scalar("SELECT coalesce(sum(length(CAST(text AS BLOB))+length(CAST(coalesce(result,'') AS BLOB))),0) FROM jobs WHERE session_id=$id;",tx,("$id",id)));
        // Formal knowledge is owned by ServerPC. Erase expired local candidate content; retain hash-only audit.
        bytes+=Convert.ToInt64(Scalar("SELECT coalesce(sum(length(bytes)+length(CAST(coalesce(result,'') AS BLOB))),0) FROM attachments WHERE job_id IN(SELECT id FROM jobs WHERE session_id=$id);",tx,("$id",id)));
        Execute("DELETE FROM result_files WHERE job_id IN(SELECT id FROM jobs WHERE session_id=$id); DELETE FROM replies WHERE job_id IN(SELECT id FROM jobs WHERE session_id=$id); DELETE FROM attachments WHERE job_id IN(SELECT id FROM jobs WHERE session_id=$id); DELETE FROM jobs WHERE session_id=$id; DELETE FROM analysis WHERE session_id=$id; DELETE FROM inference WHERE session_id=$id; DELETE FROM remote_proposals WHERE id IN(SELECT id FROM proposals WHERE session_id=$id); DELETE FROM applications WHERE session_id=$id; DELETE FROM proposals WHERE session_id=$id; DELETE FROM summary_checkpoints WHERE session_id=$id; DELETE FROM sessions WHERE id=$id;",tx,("$id",id));
        Execute("INSERT INTO audit(at,code,object_hash,bytes) VALUES($at,$reason,$hash,$bytes);",tx,("$at",Ms(now)),("$reason",reason),("$hash",SlackHash.Of(id)),("$bytes",bytes));
    }
    internal int Cleanup(DateTimeOffset now,bool sessionStoragePressure,Func<bool>? stillPressured=null){lock(gate){var candidates=new List<Session>();using(var c=Command("SELECT * FROM sessions ORDER BY last_mention;"))using(var r=c.ExecuteReader())while(r.Read())candidates.Add(ReadSession(r));var removed=0;
        foreach(var s in candidates){var expired=s.LastMention<=Ms(now.AddDays(-30));var early=sessionStoragePressure&&s.LastMention<Ms(now.AddDays(-7))&&(stillPressured?.Invoke()??false);if(!expired&&!early)continue;
            using var tx=db.BeginTransaction();if(!HasActive(s.Id,tx)){DeleteSession(s.Id,expired?"expired":"storage_pressure",now,tx);if(Convert.ToInt32(Scalar("SELECT count(*) FROM sessions WHERE id=$id;",tx,("$id",s.Id)))==0)removed++;}tx.Commit();
        }return removed;
    }}
    internal Proposal Propose(string session,string kind,string body,string evidence,string review,DateTimeOffset now){
        if(kind is not("knowledge" or "rule")||body.Length is <1 or >16000||evidence.Length>32000||review.Length>16000)throw new InvalidDataException("SLACK_PROPOSAL_INVALID");
        lock(gate){GetSession(session);var hash=SlackHash.Of(JsonSerializer.Serialize(new{session,kind,body,evidence,review,restriction=SlackRestriction.Instance}));using(var c=Command("SELECT json FROM proposals WHERE hash=$hash;",null,("$hash",hash)))if(c.ExecuteScalar() is string found)return JsonSerializer.Deserialize<Proposal>(found)!;
            var p=new Proposal(SlackHash.Id(),session,kind,body,evidence,review,hash,Ms(now.AddDays(7)),"pending");Execute("INSERT INTO proposals VALUES($id,$session,$hash,$state,$json);",null,("$id",p.Id),("$session",session),("$hash",hash),("$state",p.State),("$json",JsonSerializer.Serialize(p)));return p;
        }
    }
    internal Proposal[] Proposals(SqliteTransaction? tx=null){lock(gate){using var c=Command("SELECT json,state FROM proposals ORDER BY rowid DESC LIMIT 100;",tx);using var r=c.ExecuteReader();var list=new List<Proposal>();while(r.Read())list.Add(JsonSerializer.Deserialize<Proposal>(r.GetString(0))! with{State=r.GetString(1)});return list.ToArray();}}
    internal Proposal[] ProposalPage(long offset,int limit){if(offset<0||limit is <1 or >100)throw new ArgumentOutOfRangeException();lock(gate){using var c=Command("SELECT json,state FROM proposals ORDER BY rowid DESC LIMIT $limit OFFSET $offset;",null,("$limit",limit),("$offset",offset));using var r=c.ExecuteReader();var list=new List<Proposal>();while(r.Read())list.Add(JsonSerializer.Deserialize<Proposal>(r.GetString(0))! with{State=r.GetString(1)});return list.ToArray();}}
    internal int ProposalCount(){lock(gate)return Convert.ToInt32(Scalar("SELECT count(*) FROM proposals;"));}
    internal Proposal Authorize(string id,string hash,string actor,bool admin,string action,DateTimeOffset now,string? hostCertificateHash=null){lock(gate){using var tx=db.BeginTransaction();Proposal p;using(var c=Command("SELECT json,state FROM proposals WHERE id=$id;",tx,("$id",id)))using(var r=c.ExecuteReader()){if(!r.Read())throw new UnauthorizedAccessException("SLACK_APPROVAL_DENIED");p=JsonSerializer.Deserialize<Proposal>(r.GetString(0))! with{State=r.GetString(1)};}if(!admin||string.IsNullOrWhiteSpace(actor)||p.Hash!=hash||p.Expires<=Ms(now)||p.State is not("pending" or "held")||action is not("approve" or "reject" or "hold"))throw new UnauthorizedAccessException("SLACK_APPROVAL_DENIED");
        if(action=="approve"&&Scalar("SELECT hash FROM remote_proposals WHERE id=$id;",tx,("$id",id)) is string){if(hostCertificateHash is null)throw new UnauthorizedAccessException("SLACK_HOST_CERTIFICATE_REQUIRED");ReserveApplication(p,actor,hostCertificateHash,now,tx);}
        var state=action=="approve"?"approved":action=="reject"?"rejected":"held";Execute("UPDATE proposals SET state=$state WHERE id=$id AND state=$old;",tx,("$state",state),("$id",id),("$old",p.State));Audit("proposal_"+state,id+actor,now,tx);tx.Commit();return p with{State=state};
    }}
    internal Attachment? GetAttachment(string job,string file,SqliteTransaction? tx=null){lock(gate){using var c=Command("SELECT hash,extension,bytes,ocr_job,result FROM attachments WHERE job_id=$j AND file_id=$f;",tx,("$j",job),("$f",file));using var r=c.ExecuteReader();return r.Read()?new(job,file,r.GetString(0),r.GetString(1),(byte[])r[2],r.IsDBNull(3)?null:r.GetString(3),r.IsDBNull(4)?null:r.GetString(4)):null;}}
    internal Attachment SaveAttachment(string job,string file,string ext,byte[] bytes){lock(gate){
        var j=GetJob(job);if(!j.FileIds.Contains(file)||bytes.Length>options.MaxFileBytes||ext is not(".png" or ".jpg" or ".webp" or ".pdf"))throw new UnauthorizedAccessException("SLACK_ATTACHMENT_BINDING");
        var old=GetAttachment(job,file);var hash=SlackHash.Of(bytes);if(old is not null){if(old.Hash!=hash||old.Extension!=ext)throw new InvalidDataException("SLACK_ATTACHMENT_CHANGED");return old;}
        if(StoredBytes()+bytes.Length>512L*1024*1024)throw new IOException("SLACK_STORAGE_ADMISSION");
        Execute("INSERT INTO attachments VALUES($j,$f,$h,$e,$b,NULL,NULL);",null,("$j",job),("$f",file),("$h",hash),("$e",ext),("$b",bytes));return GetAttachment(job,file)!;
    }}
    internal void BindOcr(string job,string file,string ocr){lock(gate){var a=GetAttachment(job,file)??throw new KeyNotFoundException();if(a.OcrJob is not null&&a.OcrJob!=ocr)throw new InvalidDataException("SLACK_OCR_CHANGED");Execute("UPDATE attachments SET ocr_job=$o WHERE job_id=$j AND file_id=$f;",null,("$o",ocr),("$j",job),("$f",file));}}
    internal void SaveOcrResult(string job,string file,string result){lock(gate){if(result.Length>8*1024*1024)throw new InvalidDataException("SLACK_OCR_RESULT_SIZE");var a=GetAttachment(job,file)??throw new KeyNotFoundException();if(a.Result is not null&&a.Result!=result)throw new InvalidDataException("SLACK_OCR_RESULT_CHANGED");Execute("UPDATE attachments SET result=$r WHERE job_id=$j AND file_id=$f;",null,("$r",result),("$j",job),("$f",file));}}
    internal long StoredBytes(SqliteTransaction? tx=null){lock(gate)return Convert.ToInt64(Scalar("SELECT coalesce(sum(length(CAST(summary AS BLOB))),0) FROM summary_checkpoints;",tx))+Convert.ToInt64(Scalar("SELECT coalesce(sum(length(CAST(summary AS BLOB))),0) FROM sessions;",tx))+Convert.ToInt64(Scalar("SELECT coalesce(sum(length(bytes)),0) FROM result_files;",tx))+ Convert.ToInt64(Scalar("SELECT coalesce(sum(length(bytes)+length(CAST(coalesce(result,'') AS BLOB))),0) FROM attachments;",tx))+Convert.ToInt64(Scalar("SELECT coalesce(sum(length(CAST(text AS BLOB))+length(CAST(coalesce(result,'') AS BLOB))),0) FROM jobs;",tx))+Convert.ToInt64(Scalar("SELECT coalesce(sum(length(CAST(json AS BLOB))),0) FROM inference;",tx))+Convert.ToInt64(Scalar("SELECT coalesce(sum(length(CAST(json AS BLOB))),0) FROM proposals;",tx));}
    internal bool HasPendingJobs(){lock(gate)return Convert.ToInt32(Scalar("SELECT count(*) FROM jobs j WHERE j.phase IN('queued','running','waiting_ocr') AND NOT EXISTS(SELECT 1 FROM jobs p WHERE p.session_id=j.session_id AND p.phase NOT IN('complete','failed') AND (p.created<j.created OR (p.created=j.created AND p.rowid<j.rowid)));"))>0;}
    internal Session[] Sessions(){lock(gate){using var c=Command("SELECT * FROM sessions ORDER BY last_mention;");using var r=c.ExecuteReader();var list=new List<Session>();while(r.Read())list.Add(ReadSession(r));return list.ToArray();}}
    internal bool ExtractionDone(string session,string hash){lock(gate)return Convert.ToInt32(Scalar("SELECT count(*) FROM analysis WHERE session_id=$s AND source_hash=$h;",null,("$s",session),("$h",hash)))>0;}
    internal void ExtractionComplete(string session,string hash){lock(gate)Execute("INSERT OR IGNORE INTO analysis VALUES($s,$h);",null,("$s",session),("$h",hash));}
    internal void DropSummaryCache(){lock(gate)Execute("UPDATE sessions SET summary=NULL,summary_hash=NULL WHERE summary IS NOT NULL; DELETE FROM summary_checkpoints;");}
    internal (string Session,string Request)[] PendingRetirements(){lock(gate){using var c=Command("SELECT session_id,request_id FROM retirements WHERE state='pending' ORDER BY rowid LIMIT 5;");using var r=c.ExecuteReader();var rows=new List<(string,string)>();while(r.Read())rows.Add((r.GetString(0),r.GetString(1)));return rows.ToArray();}}
    internal void RetirementComplete(string session,string request){lock(gate)Execute("UPDATE retirements SET state='retired' WHERE session_id=$s AND request_id=$r AND state='pending';",null,("$s",session),("$r",request));}
    private void Audit(string code,string id,DateTimeOffset now,SqliteTransaction tx)=>Execute("INSERT INTO audit(at,code,object_hash,bytes) VALUES($at,$code,$hash,0);",tx,("$at",Ms(now)),("$code",code),("$hash",SlackHash.Of(id)));
    public void Dispose()=>db.Dispose();
    internal sealed record Checkpoint(int Count,string Hash,string Summary);
    private const string SummaryCheckpointSchema="CREATE TABLE IF NOT EXISTS summary_checkpoints(session_id TEXT PRIMARY KEY,count INTEGER NOT NULL,prefix_hash TEXT NOT NULL,summary TEXT NOT NULL);";
    internal Checkpoint? SummaryCheckpoint(string session){lock(gate){using var c=Command("SELECT count,prefix_hash,summary FROM summary_checkpoints WHERE session_id=$id;",null,("$id",session));using var r=c.ExecuteReader();return r.Read()?new(r.GetInt32(0),r.GetString(1),r.GetString(2)):null;}}
    internal bool SaveSummaryCheckpoint(string session,int count,string hash,string summary,string history){lock(gate){if(HistoryHash(session)!=history)return false;Execute("INSERT INTO summary_checkpoints VALUES($id,$n,$h,$s) ON CONFLICT(session_id) DO UPDATE SET count=$n,prefix_hash=$h,summary=$s;",null,("$id",session),("$n",count),("$h",hash),("$s",summary));return true;}}
    internal bool HasSummaryWaiter(string session){lock(gate)return Convert.ToInt32(Scalar("SELECT count(*) FROM jobs WHERE session_id=$id AND phase='waiting_summary';",null,("$id",session)))>0;}
    internal int WaitingSummaryBudget(string session){lock(gate){using var c=Command("SELECT text FROM jobs WHERE session_id=$id AND phase='waiting_summary';",null,("$id",session));using var r=c.ExecuteReader();var budget=SlackConversation.SummaryBytes;while(r.Read())budget=Math.Min(budget,SlackConversation.Budget(r.GetString(0)));return budget;}}
    private const string Schema="""
        CREATE TABLE IF NOT EXISTS epochs(id TEXT PRIMARY KEY,started INTEGER NOT NULL,ended INTEGER);
        CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY,team TEXT,channel TEXT,thread TEXT,owner TEXT,last_mention INTEGER,summary TEXT,summary_hash TEXT,UNIQUE(team,channel,thread));
        CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY,event_id TEXT UNIQUE,session_id TEXT REFERENCES sessions,user_id TEXT,text TEXT,files TEXT,phase TEXT,result TEXT,created INTEGER);
        CREATE TABLE IF NOT EXISTS replies(id TEXT PRIMARY KEY,job_id TEXT,channel TEXT,thread TEXT,text TEXT,kind TEXT,state TEXT,attempts INTEGER,retry_after INTEGER,remote_ts TEXT);
        CREATE TABLE IF NOT EXISTS proposals(id TEXT PRIMARY KEY,session_id TEXT,hash TEXT UNIQUE,state TEXT,json TEXT);
        CREATE TABLE IF NOT EXISTS attachments(job_id TEXT REFERENCES jobs,file_id TEXT,hash TEXT,extension TEXT,bytes BLOB,ocr_job TEXT,result TEXT,PRIMARY KEY(job_id,file_id));
        CREATE TABLE IF NOT EXISTS analysis(session_id TEXT,source_hash TEXT,PRIMARY KEY(session_id,source_hash));
        CREATE TABLE IF NOT EXISTS inference(request_id TEXT PRIMARY KEY,route TEXT,session_id TEXT,json TEXT,hash TEXT);
        CREATE TABLE IF NOT EXISTS retirements(session_id TEXT PRIMARY KEY,request_id TEXT UNIQUE,state TEXT);
        CREATE TABLE IF NOT EXISTS audit(at INTEGER,code TEXT,object_hash TEXT,bytes INTEGER);
        CREATE INDEX IF NOT EXISTS jobs_phase ON jobs(phase,created);
        """;
}
