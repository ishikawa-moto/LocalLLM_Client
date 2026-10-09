using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalBrain.ClientHost.AgentV2;

internal static class ExternalReferenceCorpusTests
{
    internal static void Run(string proofRoot,string bundle)
    {
        proofRoot=Path.GetFullPath(proofRoot);Directory.CreateDirectory(proofRoot);
        var repo=Path.Combine(proofRoot,"repo");Directory.CreateDirectory(repo);
        using var store=new CanonicalStore(Path.Combine(proofRoot,"state"));
        store.RegisterWorkspace(new("repo_reference","ws_reference",repo,repo,"main","base"));
        var request="{}";store.CreateTask("task_reference","ws_reference",CanonicalStore.Hash(Encoding.UTF8.GetBytes(request)),request);
        store.Checkpoint(new("task_reference","ws_reference","fixture","initial","task_checkpoint","execution_evidence","host_observed",[],
            JsonSerializer.SerializeToElement(new{phase="existing_task"})),"{\"phase\":\"existing_task\",\"false_verified\":false}",3,5,1,2);
        var before=store.GetTask("task_reference")!;
        var raw=File.ReadAllBytes(Path.Combine(bundle,"original.pdf"));var text=File.ReadAllBytes(Path.Combine(bundle,"extracted.md"));
        var manifest=File.ReadAllBytes(Path.Combine(bundle,"manifest.json"));
        var first=ExternalReferenceCorpus.Import(store,"task_reference",raw,text,manifest);
        if(first.Duplicate||first.ClaimsVerified||first.KnowledgeAdopted)throw new Exception("Reference import claimed verified adoption");
        var evt=store.ReadTaskEvent("task_reference",first.EventId);
        if(evt.Input.EventType!="external_reference_imported"||evt.Input.VerificationStatus!="unverified"||evt.Input.EvidenceRefs.Length!=3)
            throw new Exception("Reference event authority or evidence differs");
        if(!store.ReadEvidence(first.OriginalHash).SequenceEqual(raw)||!store.ReadEvidence(first.TextHash).SequenceEqual(text))throw new Exception("Original and derived evidence differ");
        var altered=JsonNode.Parse(manifest)!.AsObject();altered["source_uri"]="fixture://alternate/same.pdf";altered["retrieved_at"]="2026-10-09T00:00:00Z";
        var duplicate=ExternalReferenceCorpus.Import(store,"task_reference",raw,text,Encoding.UTF8.GetBytes(altered.ToJsonString()));
        if(!duplicate.Duplicate||duplicate.EventId!=first.EventId||store.EventIds("task_reference").Length!=2)throw new Exception("Alternate URL created independent source/event");
        var after=store.GetTask("task_reference")!;
        if(after.StateJson!=before.StateJson||after.ActorTurns!=3||after.ToolCalls!=5||after.Handoffs!=1||after.ExternalReviews!=2)
            throw new Exception("Reference import reset existing task/counters");
        var checks=new List<string>{"reference_event_retains_unverified_claim_authority","raw_and_derived_exact_evidence_retained","alternate_URL_same_document_idempotent","existing_task_state_and_counters_preserved"};
        void Reject(byte[] r,byte[] t,JsonObject m,string label){
            try{ExternalReferenceCorpus.Import(store,"task_reference",r,t,Encoding.UTF8.GetBytes(m.ToJsonString()));}
            catch(InvalidDataException){checks.Add(label);return;}
            throw new Exception("Invalid reference accepted: "+label);
        }
        var forged=JsonNode.Parse(manifest)!.AsObject();forged["claims_are_verified"]=true;Reject(raw,text,forged,"reference_cannot_claim_verified_truth");
        var wrongPage=JsonNode.Parse(manifest)!.AsObject();wrongPage["page_map"]![0]!["page"]=99;Reject(raw,text,wrongPage,"page_identity_checked");
        var other=raw.ToArray();other[^1]^=1;Reject(other,text,JsonNode.Parse(manifest)!.AsObject(),"different_original_rejected");
        var naive=JsonNode.Parse(manifest)!.AsObject();naive["retrieved_at"]="2026-10-07T17:00:00";Reject(raw,text,naive,"timezone_not_inferred_from_Host");
        var invalid=JsonNode.Parse(manifest)!.AsObject();invalid["retrieved_at"]="invalidTtimestamp+09:00";Reject(raw,text,invalid,"invalid_retrieval_timestamp_rejected");
        foreach(var date in new[]{"2026-10-07T17:00:00Z","2026-10-07T17:00:00+09:00"}){
            var zoned=JsonNode.Parse(manifest)!.AsObject();zoned["retrieved_at"]=date;
            if(!ExternalReferenceCorpus.Import(store,"task_reference",raw,text,Encoding.UTF8.GetBytes(zoned.ToJsonString())).Duplicate)throw new Exception("Zoned duplicate differs");
        }
        checks.Add("explicit_Z_and_offset_accepted");
        if(store.EventIds("task_reference").Length!=2)throw new Exception("Rejected input appended an event");
        checks.Add("invalid_input_does_not_append_reference_event");
        var result=new{status="PASS_CANONICAL_HOST_REFERENCE_IMPORT_ONLY",task_id="task_reference",canonical_root=store.Root,checks,
            first,duplicate,model_calls=0,knowledge_adoptions=0,production_deployed=false,external_claim_verification_implemented=false};
        File.WriteAllText(Path.Combine(proofRoot,"proof.json"),JsonSerializer.Serialize(result));Console.WriteLine(JsonSerializer.Serialize(result));
    }
}
