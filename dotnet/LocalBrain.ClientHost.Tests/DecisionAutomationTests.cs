using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class DecisionAutomationTests
{
    internal static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"localbrain-decision-auto-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"repo"));
        using var store=new CanonicalStore(Path.Combine(root,"host"));
        var repo=Path.Combine(root,"repo");var request=AgentTaskRequest.Parse("""{"requirement":"Administrative fixture","acceptance_criteria":["No real adoption"],"risk":"LOW","approved_high_risk":false,"allowed_files":["fixture.txt"],"required_tests":[{"kind":"git_diff_check"}]}""");
        using var journal=new HostTaskJournal(store,repo,"automatic_fixture_task",request,new string('a',40));
        var payload=JsonSerializer.SerializeToElement(new{schema=DecisionProtocol.ProposalSchema,proposal_id="automatic_fixture_id",kind="decision",project="demo",title="Synthetic",body="Synthetic review fixture",references=new[]{new{path="raw/fixture.md",expected_hash=new string('a',64),expected_metadata_hash=new string('b',64),relevant_location="L1"}}});
        var hash=DecisionProtocol.Digest(payload);
        var receipt=JsonSerializer.Serialize(new{proposal_id="automatic_fixture_id",proposal_hash=hash,schema=DecisionProtocol.ProposalSchema,status="draft",payload,content=payload.GetProperty("body"),path="drafts/pending-review/automatic_fixture_id.md",file_hashes=new[]{CanonicalStore.Hash(Encoding.UTF8.GetBytes("# Synthetic\n\nSynthetic review fixture\n")),new string('b',64),hash}});
        var adapter=new DecisionAdoption(store,journal.TaskId);var intent=adapter.PrepareProposal(receipt,"automatic_fixture_id");
        JsonElement Response(string mode,string state,int passes=2){
            var record=JsonSerializer.SerializeToElement(new{schema="localbrain.decision-review.v1",policy=AutomaticDecisionPolicy.Name,model="local-qwen38",proposal_id="automatic_fixture_id",proposal_hash=hash,request_mode=mode,host_policy_hash=new string('c',64),task_id=journal.TaskId,workspace_id=HostTaskJournal.WorkspaceId(repo),client_certificate_sha256=new string('d',64),state,review_passes=passes,passes=new[]{new[]{new{id=1,source_path="raw/fixture.md",source_quote="Synthetic supporting statement"}},new[]{new{id=1,source_path="raw/fixture.md",source_quote="Synthetic supporting statement"}}},issues=state=="returned"?new[]{new{code="reviewer_issues",message="Insufficient evidence"}}:[],return_count=state=="returned"?1:0});
            return JsonSerializer.SerializeToElement(new{schema="localbrain.decision-review.v1",proposal_id="automatic_fixture_id",proposal_hash=hash,state,review_receipt=record,review_receipt_hash=DecisionProtocol.Digest(record)});
        }
        var approved=Response("proposal","approved");var exact=adapter.RequireIntent(intent);DecisionReviewAuthorization.Validate(exact,journal.TaskId,new string('c',64),approved,null);
        Reject(()=>DecisionReviewAuthorization.Validate(exact,journal.TaskId,new string('e',64),approved,null),"Wrong policy accepted");
        Reject(()=>DecisionReviewAuthorization.Validate(exact,"wrong_fixture_task",new string('c',64),approved,null),"Wrong owner accepted");
        Reject(()=>DecisionReviewAuthorization.Validate(exact,journal.TaskId,new string('c',64),Response("proposal","returned"),new string('e',64)),"Model suggestion self-overrode rejection");
        Reject(()=>DecisionReviewAuthorization.Validate(exact,journal.TaskId,new string('c',64),Response("user_request","returned"),null),"Returned user request became Reviewer PASS");
        Reject(()=>DecisionReviewAuthorization.Validate(exact,journal.TaskId,new string('c',64),Response("user_request","approved",1),null),"One pass gained automatic permission");
        var responseHash=store.PutEvidence(DecisionProtocol.Utf8.GetBytes(approved.GetRawText()));
        var proof=store.Append(new(journal.TaskId,HostTaskJournal.WorkspaceId(repo),"windows_host","auto_receipt_fixture","decision_review_receipt_received","server_review_receipt","authenticated_server_response",[responseHash],JsonSerializer.SerializeToElement(new{review_response_hash=responseHash,policy_hash=new string('c',64)})));
        var approval=adapter.RecordReviewerApproved(intent,new string('c',64),proof.EventId,null);
        var reservation=adapter.Reserve(approval);var sent=DecisionProtocol.Parse(reservation.Json).GetProperty("approval");
        Check(sent.GetProperty("version").GetInt32()==3&&sent.GetProperty("provenance").GetString()=="reviewer-approved-model-proposal","Automation fabricated human review");
        Check(sent.GetProperty("reaffirmation_receipt_hash").ValueKind==JsonValueKind.Null,"Automation fabricated human reaffirmation");
        var calls=0;
        await adapter.DispatchAsync(approval,new string('d',64),(r,_)=>{calls++;return Task.FromResult((409,JsonSerializer.Serialize(new{status="rejected",reason_code="stale_target",reason="fixture",request_id="automatic_fixture_id"})));});
        await adapter.DispatchAsync(approval,new string('d',64),(_,_)=>throw new Exception("Terminal receipt re-dispatched"));Check(calls==1,"Duplicate dispatch");
        var returned=Response("user_request","returned");var returnedHash=store.PutEvidence(DecisionProtocol.Utf8.GetBytes(returned.GetRawText()));
        var returnedEvent=store.Append(new(journal.TaskId,HostTaskJournal.WorkspaceId(repo),"windows_host","returned_receipt_fixture","decision_review_receipt_received","server_review_receipt","authenticated_server_response",[returnedHash],JsonSerializer.SerializeToElement(new{policy_hash=new string('c',64)})));
        var nativeBytes=JsonSerializer.SerializeToUtf8Bytes(new{confirmed=true,source="windows_native_dialog",intent_hash=intent,review_receipt_hash=returned.GetProperty("review_receipt_hash"),fixture_scope="DISPOSABLE_ONLY_NO_REAL_HUMAN_CONFIRMATION"});
        var nativeHash=store.PutEvidence(nativeBytes);
        var modelNative=store.Append(new(journal.TaskId,HostTaskJournal.WorkspaceId(repo),"qwen","fake_native_fixture","decision_native_reaffirmation_confirmed","model_claim","human_explicit",[nativeHash],JsonSerializer.SerializeToElement(new{fake=true})));
        Reject(()=>adapter.RecordReviewerApproved(intent,new string('c',64),returnedEvent.EventId,modelNative.EventId),"Model-produced boolean forged a human reaffirmation");
        var nativeEvent=store.Append(new(journal.TaskId,HostTaskJournal.WorkspaceId(repo),"windows_host","synthetic_native_fixture","decision_native_reaffirmation_confirmed","human_statement","human_explicit",[nativeHash],JsonSerializer.SerializeToElement(new{fixture_scope="DISPOSABLE_ONLY"})));
        var reaffirmed=adapter.RecordReviewerApproved(intent,new string('c',64),returnedEvent.EventId,nativeEvent.EventId);
        var reaffirmedPayload=DecisionProtocol.Parse(adapter.Reserve(reaffirmed).Json).GetProperty("approval");
        Check(reaffirmedPayload.GetProperty("provenance").GetString()=="human-reaffirmed-returned-decision"&&reaffirmedPayload.GetProperty("reaffirmation_receipt_hash").GetString()==nativeHash,"Human reaffirmation mislabeled Reviewer approval");
        Check(store.GetTask(journal.TaskId)!.ActorTurns==0&&!Directory.GetFiles(Path.Combine(root,"repo")).Any(),"Actor or repository mutated");
        Console.WriteLine("Automatic decision policy: two-pass binding, returned explicit request, no model self-reaffirmation, no fabricated human approval and terminal duplicate retention PASS; fixture transport only.");
    }
    private static void Check(bool yes,string text){if(!yes)throw new Exception(text);}
    private static void Reject(Action f,string text){try{f();}catch(UnauthorizedAccessException){return;}catch(InvalidDataException){return;}throw new Exception(text);}
}
