using System.Text.Json;
using System.Text.Json.Nodes;
using LocalBrain.ClientHost.AgentV2;

namespace LocalBrain.ClientHost.Slack;

internal sealed partial class SlackStore
{
    internal sealed record Application(string ProposalId,string RequestId,string SessionId,string RequestHash,string Json,string State,long Expires);
    internal bool HasServerBinding(string id){lock(gate)return Scalar("SELECT hash FROM remote_proposals WHERE id=$id;",null,("$id",id)) is string;}
    internal Proposal BindServerProposal(string session,string responseJson,string reviewJson,DateTimeOffset now)
    {
        var response=DecisionProtocol.Parse(responseJson);
        if(response.GetProperty("status").GetString()!="proposed_restricted")throw new InvalidDataException("SLACK_PROPOSAL_NOT_READY");
        var payload=response.GetProperty("payload");var hash=DecisionProtocol.HashText(response.GetProperty("proposal_hash").GetString()!);
        if(DecisionProtocol.Digest(payload)!=hash||payload.GetProperty("session_id").GetString()!=session)throw new InvalidDataException("SLACK_PROPOSAL_BINDING");
        if(payload.GetProperty("schema").GetString()!="localbrain.slack-proposal.v1")throw new InvalidDataException("SLACK_PROPOSAL_SCHEMA");
        RequireClassification(payload.GetProperty("classification"));
        var reviewed=DecisionProtocol.Parse(reviewJson);var review=reviewed.GetProperty("review");
        RequireClassification(review.GetProperty("classification"));
        if(reviewed.GetProperty("status").GetString()!="complete"||review.GetProperty("schema").GetString()!="localbrain.slack-review.v1"||
            review.GetProperty("session_id").GetString()!=session||DecisionProtocol.Digest(review)!=payload.GetProperty("review_hash").GetString()||
            reviewed.GetProperty("review_hash").GetString()!=payload.GetProperty("review_hash").GetString()||
            DecisionProtocol.Digest(reviewed.GetProperty("candidate"))!=DecisionProtocol.Digest(payload.GetProperty("candidate")))throw new InvalidDataException("SLACK_REVIEW_BINDING");
        var id=DecisionProtocol.Id(payload.GetProperty("proposal_id").GetString()!);_ = DecisionProtocol.HashText(payload.GetProperty("review_hash").GetString()!);
        var candidate=payload.GetProperty("candidate");var kind=candidate.GetProperty("kind").GetString()!;
        if(kind is not("knowledge" or "rule"))throw new InvalidDataException("SLACK_PROPOSAL_KIND");
        if(payload.GetProperty("target").GetString()!=(kind=="rule"?"slack-response-policy":"restricted-knowledge"))throw new UnauthorizedAccessException("SLACK_PROPOSAL_TARGET_DENIED");
        _=DecisionProtocol.HashText(payload.GetProperty("new_hash").GetString()!);
        if(kind=="rule")_ = DecisionProtocol.HashText(payload.GetProperty("base_hash").GetString()!);
        else if(payload.GetProperty("base_hash").GetString()!=""||DecisionProtocol.Digest(candidate)!=payload.GetProperty("new_hash").GetString())throw new InvalidDataException("SLACK_KNOWLEDGE_HASH");
        var evidence=JsonSerializer.Serialize(new{target=payload.GetProperty("target"),base_hash=payload.GetProperty("base_hash"),new_hash=payload.GetProperty("new_hash"),payload=payload,restriction=SlackRestriction.Instance},SlackOptions.Json);
        lock(gate){GetSession(session);using var tx=db.BeginTransaction();
            using(var existingCommand=Command("SELECT hash,json,state FROM proposals WHERE id=$id;",tx,("$id",id)))
            using(var reader=existingCommand.ExecuteReader()){
                if(reader.Read()){
                    if(reader.GetString(0)!=hash)throw new InvalidDataException("SLACK_PROPOSAL_CHANGED");
                    var existingProposal=JsonSerializer.Deserialize<Proposal>(reader.GetString(1))! with{State=reader.GetString(2)};
                    reader.Close();tx.Commit();return existingProposal;
                }
            }
            var p=new Proposal(id,session,kind,candidate.GetRawText(),evidence,reviewJson,hash,now.AddDays(7).ToUnixTimeMilliseconds(),"pending");
            Execute("INSERT INTO proposals VALUES($id,$session,$hash,$state,$json);",tx,("$id",p.Id),("$session",p.SessionId),("$hash",hash),("$state",p.State),("$json",JsonSerializer.Serialize(p)));
            Execute("INSERT INTO remote_proposals VALUES($id,$hash,$payload);",tx,("$id",id),("$hash",hash),("$payload",payload.GetRawText()));
            Audit("server_proposal_bound",id,now,tx);tx.Commit();return p;
        }
    }
    // Called only within the same transaction that records the authenticated human's exact content approval.
    private void ReserveApplication(Proposal p,string actor,string certificateHash,DateTimeOffset now,Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        var raw=Scalar("SELECT payload FROM remote_proposals WHERE id=$id AND hash=$hash;",tx,("$id",p.Id),("$hash",p.Hash)) as string;
        if(raw is null)return;
        var payload=DecisionProtocol.Parse(raw);if(DecisionProtocol.Digest(payload)!=p.Hash)throw new InvalidDataException("SLACK_STORED_PROPOSAL_DRIFT");
        DecisionProtocol.HashText(certificateHash);var issued=now.ToUnixTimeSeconds();var expires=issued+300;
        var approval=JsonSerializer.SerializeToElement(new{
            schema="localbrain.slack-host-approval.v1",approval_id=SlackHash.Id(),nonce=SlackHash.Id(),approver_id=actor,action="accept_restricted",
            proposal_id=p.Id,proposal_hash=p.Hash,review_hash=payload.GetProperty("review_hash"),session_id=p.SessionId,issued_at=issued,expires_at=expires,
            host_owner=DecisionProtocol.Owner,host_certificate_sha256=certificateHash,target=payload.GetProperty("target"),base_hash=payload.GetProperty("base_hash"),new_hash=payload.GetProperty("new_hash")});
        var node=JsonNode.Parse(approval.GetRawText())!.AsObject();node["receipt_hash"]=DecisionProtocol.Digest(approval);
        var requestId=SlackHash.Id();var outer=JsonSerializer.SerializeToElement(new{request_id=requestId,proposal_id=p.Id,proposal_hash=p.Hash,session_id=p.SessionId,approval=node,restriction=SlackRestriction.Instance},SlackOptions.Json);
        var json=DecisionProtocol.Utf8.GetString(DecisionProtocol.Canonical(outer));var requestHash=DecisionProtocol.Digest(outer);
        Execute("INSERT INTO applications VALUES($id,$request,$session,$hash,$json,'reserved',$expires,NULL);",tx,("$id",p.Id),("$request",requestId),("$session",p.SessionId),("$hash",requestHash),("$json",json),("$expires",expires));
    }
    internal Application? ClaimApplication(DateTimeOffset now){lock(gate){using var tx=db.BeginTransaction();
        Execute("UPDATE proposals SET state='approval_expired' WHERE id IN(SELECT proposal_id FROM applications WHERE state='reserved' AND expires<=$now); UPDATE applications SET state='approval_expired' WHERE state='reserved' AND expires<=$now;",tx,("$now",now.ToUnixTimeSeconds()));
        using var command=Command("SELECT proposal_id,request_id,session_id,request_hash,json,state,expires FROM applications WHERE state='reserved' ORDER BY rowid LIMIT 1;",tx);Application? a;
        using(var r=command.ExecuteReader())a=r.Read()?new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt64(6)):null;
        if(a is not null){if(DecisionProtocol.Digest(DecisionProtocol.Parse(a.Json))!=a.RequestHash)throw new InvalidDataException("SLACK_RESERVED_REQUEST_CHANGED");
            if(Execute("UPDATE applications SET state='sending' WHERE proposal_id=$id AND state='reserved'; UPDATE proposals SET state='applying' WHERE id=$id AND state='approved';",tx,("$id",a.ProposalId))!=2)throw new InvalidDataException("SLACK_APPLICATION_STATE_DRIFT");Audit("application_reserved_dispatch",a.RequestId,now,tx);
        }tx.Commit();return a;
    }}
    internal void ApplicationUncertain(Application a){lock(gate){using var tx=db.BeginTransaction();Execute("UPDATE applications SET state='uncertain' WHERE proposal_id=$id AND state='sending'; UPDATE proposals SET state='apply_uncertain' WHERE id=$id;",tx,("$id",a.ProposalId));tx.Commit();}}
    internal void ApplicationComplete(Application a,string resultJson){lock(gate){
        var result=DecisionProtocol.Parse(resultJson);
        var sent=DecisionProtocol.Parse(a.Json);var approval=sent.GetProperty("approval");RequireClassification(result.GetProperty("classification"));
        if(result.GetProperty("status").GetString()!="applied_restricted"||result.GetProperty("proposal_id").GetString()!=a.ProposalId||
            result.GetProperty("proposal_hash").GetString()!=sent.GetProperty("proposal_hash").GetString()||
            result.GetProperty("target").GetString()!=approval.GetProperty("target").GetString()||result.GetProperty("new_hash").GetString()!=approval.GetProperty("new_hash").GetString())throw new InvalidDataException("SLACK_APPLY_RECEIPT_BINDING");
        using var tx=db.BeginTransaction();
        if(Execute("UPDATE applications SET state='applied',result=$r WHERE proposal_id=$id AND request_id=$request AND request_hash=$hash AND state IN('sending','uncertain');",tx,("$r",resultJson),("$id",a.ProposalId),("$request",a.RequestId),("$hash",a.RequestHash))!=1)throw new InvalidDataException("SLACK_APPLY_RESERVATION_MISMATCH");
        Execute("UPDATE proposals SET state='applied' WHERE id=$id;",tx,("$id",a.ProposalId));Audit("application_receipt",a.RequestId,DateTimeOffset.UtcNow,tx);tx.Commit();
    }}
    internal void ApplicationHold(Application a){lock(gate){using var tx=db.BeginTransaction();Execute("UPDATE applications SET state='unresolved' WHERE proposal_id=$id AND request_hash=$hash AND state IN('sending','uncertain'); UPDATE proposals SET state='apply_unresolved' WHERE id=$id;",tx,("$id",a.ProposalId),("$hash",a.RequestHash));tx.Commit();}}
    internal static void RequireClassification(JsonElement c){DecisionProtocol.Keys(c,"origin","data_class","external_ai","cloud_fallback");if(c.GetProperty("origin").GetString()!="slack"||c.GetProperty("data_class").GetString()!="slack_restricted"||c.GetProperty("external_ai").GetString()!="deny"||c.GetProperty("cloud_fallback").ValueKind!=JsonValueKind.False)throw new UnauthorizedAccessException("SLACK_CLASSIFICATION_CHANGED");}
    internal Application[] UncertainApplications(){lock(gate){using var c=Command("SELECT proposal_id,request_id,session_id,request_hash,json,state,expires FROM applications WHERE state IN('sending','uncertain');");using var r=c.ExecuteReader();var list=new List<Application>();while(r.Read())list.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt64(6)));return list.ToArray();}}
    private const string AdoptionSchema="""
        CREATE TABLE IF NOT EXISTS remote_proposals(id TEXT PRIMARY KEY,hash TEXT,payload TEXT);
        CREATE TABLE IF NOT EXISTS applications(proposal_id TEXT PRIMARY KEY,request_id TEXT UNIQUE,session_id TEXT,request_hash TEXT,json TEXT,state TEXT,expires INTEGER,result TEXT);
        """;
}
