using System.Net;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.AgentV2;

internal static class HostDecisionTransportTests
{
    private sealed class FixtureHandler:HttpMessageHandler
    {
        internal readonly List<byte[]> Bodies=[];
        internal string Mode="success";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.Method!=HttpMethod.Post || request.RequestUri!.AbsolutePath!="/v1/brain/apply-decision" ||
                !request.Headers.TryGetValues("X-LocalBrain-Approval-Owner",out var owner) || owner.Single()!=DecisionProtocol.Owner)
                throw new Exception("HTTP owner/method/route incorrect");
            var bytes=await request.Content!.ReadAsByteArrayAsync(token);
            if(request.Content.Headers.ContentLength!=bytes.LongLength || request.Content.Headers.ContentType!.MediaType!="application/json")throw new Exception("HTTP framing incorrect");
            Bodies.Add(bytes);
            if(Mode=="reply_loss"){Mode="success";throw new HttpRequestException("synthetic reply loss after dispatch");}
            if(Mode=="oversize")return new(HttpStatusCode.OK){Content=new StringContent(new string('x',250001))};
            var p=DecisionProtocol.Parse(DecisionProtocol.Utf8.GetString(bytes));var approval=p.GetProperty("approval");
            var response=new{status="applied",operation="decision",request_id=p.GetProperty("request_id"),approval_id=approval.GetProperty("approval_id"),
                task_id=approval.GetProperty("task_id"),client_certificate_sha256=new string('c',64),git_commit=new string('d',40),audit_id=Guid.NewGuid().ToString(),
                destination="decisions/pc73-"+p.GetProperty("request_id").GetString()+".md",paths=new[]{"decisions/pc73-"+p.GetProperty("request_id").GetString()+".md","decisions/pc73-"+p.GetProperty("request_id").GetString()+".md.meta.json"},
                origin_proposal_id=p.GetProperty("origin_proposal_id"),expected_proposal_hash=p.GetProperty("expected_proposal_hash"),consumption_hash=new string('e',64)};
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(response),Encoding.UTF8,"application/json")};
        }
    }
    internal static async Task RunAsync(string root)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        root=Path.GetFullPath(root);if(Directory.Exists(root))throw new IOException("Fresh transport fixture required");var repo=Path.Combine(root,"repo");Directory.CreateDirectory(repo);
        using var store=new CanonicalStore(Path.Combine(root,"host"));
        var request=new AgentTaskRequest("Synthetic HTTP adoption fixture",["exact durable Host transport"],"LOW",false,["greeting.txt"],[new("git_diff_check",null)]);
        using var journal=new HostTaskJournal(store,repo,"task_transport_fixture",request,new string('a',40));
        using var golden=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","decision-protocol-v1.json")));
        var vector=golden.RootElement.GetProperty("vectors")[1];var proposal=vector.GetProperty("proposal_payload");
        var receipt=JsonSerializer.Serialize(new{proposal_id=proposal.GetProperty("proposal_id"),proposal_hash=vector.GetProperty("proposal_hash"),schema=DecisionProtocol.ProposalSchema,
            status="draft",payload=proposal,content=proposal.GetProperty("body"),path="drafts/pending-review/"+proposal.GetProperty("proposal_id").GetString()+".md",
            file_hashes=new[]{vector.GetProperty("markdown_sha256").GetString(),new string('b',64),vector.GetProperty("proposal_hash").GetString()}});
        var adapter=new DecisionAdoption(store,journal.TaskId);var handler=new FixtureHandler();using var gateway=new GatewayClient(new Uri("https://fixture.invalid/"),handler);
        var checks=new List<string>();void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);}
        async Task Reject(Func<Task> action,string name){try{await action();}catch(Exception e)when(e is UnauthorizedAccessException or InvalidDataException or HttpRequestException){checks.Add(name);return;}throw new Exception("Unexpected transport acceptance: "+name);}
        Task<string> Dispatch(string id)=>adapter.DispatchAsync(id,new string('c',64),(r,t)=>gateway.SendOwnedDecisionAsync(journal,r,t));
        await Reject(()=>Dispatch("missing_approval"),"missing_approval_no_http");Check(handler.Bodies.Count==0,"approval_lookup_before_network");
        foreach(var route in new[]{"/v1/brain/apply-decision","/v1/brain/apply-human-decision","/v1/brain/%61pply-decision"})
            await Reject(()=>gateway.SendAsync(HttpMethod.Post,route,null,null,null,Guid.NewGuid().ToString(),default),"generic_gateway_host_route_denied_"+checks.Count);
        Check(handler.Bodies.Count==0,"model_generic_path_no_network");
        var intent=adapter.PrepareProposal(receipt,"transport_apply_001");
        var id=adapter.RecordConfirmed(intent,"synthetic-http-fixture",DateTimeOffset.UtcNow.ToUnixTimeSeconds(),900);
        var reserved=adapter.Reserve(id);
        await Reject(()=>gateway.SendOwnedDecisionAsync(journal,reserved with{Route="/v1/brain/apply-human-decision"},default),"reservation_route_mismatch_no_http");
        using(var other=new HostTaskJournal(store,repo,"task_other_transport",request,new string('a',40)))
            await Reject(()=>gateway.SendOwnedDecisionAsync(other,reserved,default),"other_task_approval_no_http");
        Check(handler.Bodies.Count==0,"all_authority_negatives_before_http");
        handler.Mode="reply_loss";await Reject(()=>Dispatch(id),"uncertain_reply_retains_reservation");
        Check(store.DecisionApproval(journal.TaskId,id).ResultHash is null,"reply_loss_not_completed");
        using(var newer=new HostTaskJournal(store,repo,"newer_workspace_task",request,new string('a',40))){}
        Check(store.LatestTask(HostTaskJournal.WorkspaceId(repo))!.TaskId=="newer_workspace_task" &&
            store.ResolveAdoptionOwner(HostTaskJournal.WorkspaceId(repo),id,true).TaskId==journal.TaskId &&
            store.ResolveAdoptionOwner(HostTaskJournal.WorkspaceId(repo),intent,false).TaskId==journal.TaskId,"old_approval_and_intent_owner_after_new_task");
        await Reject(()=>Task.FromResult(store.ResolveAdoptionOwner("wrong_workspace",id,true)),"adoption_owner_other_workspace_rejected");
        var result=await Dispatch(id);Check(handler.Bodies.Count==2 && handler.Bodies[0].SequenceEqual(handler.Bodies[1]) &&
            CanonicalStore.Hash(handler.Bodies[0])==reserved.RequestHash,"exact_retry_bytes_and_http_owner_framing");
        var cached=await Dispatch(id);Check(cached==result && handler.Bodies.Count==2,"terminal_receipt_no_second_effect");
        Check(store.ResolveAdoptionOwner(HostTaskJournal.WorkspaceId(repo),id,true).TaskId==journal.TaskId,"completed_old_owner_remains_resolvable");
        var oversizeIntent=adapter.PrepareProposal(receipt,"transport_apply_002");var oversize=adapter.RecordConfirmed(oversizeIntent,"synthetic-http-fixture",DateTimeOffset.UtcNow.ToUnixTimeSeconds(),900);
        handler.Mode="oversize";await Reject(()=>Dispatch(oversize),"response_budget_rejected");Check(store.DecisionApproval(journal.TaskId,oversize).ResultHash is null,"oversize_response_not_completed");
        var proof=new{status="PASS_SYNTHETIC_HTTP_HOST_TRANSPORT_ONLY",checks,http_calls=handler.Bodies.Count,request_hash=reserved.RequestHash,
            exact_request_bodies=handler.Bodies.Select(bytes=>CanonicalStore.Hash(bytes)).ToArray(),canonical_root=store.Root,task_id=journal.TaskId,
            existing_protocol_used=true,real_mtls=false,real_human_confirmation=false,production_changed=false};
        File.WriteAllText(Path.Combine(root,"proof.json"),JsonSerializer.Serialize(proof));Console.WriteLine(JsonSerializer.Serialize(proof));
    }
}
