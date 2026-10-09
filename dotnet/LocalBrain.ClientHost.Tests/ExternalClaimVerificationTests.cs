using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class ExternalClaimVerificationTests
{
    internal static async Task RunAsync(string proofRoot,string bundle)
    {
        Directory.CreateDirectory(proofRoot);var repo=Path.Combine(Path.GetFullPath(proofRoot),"repo");Directory.CreateDirectory(repo);
        await AttemptStartingState.GitAsync(repo,["init"]);
        await AttemptStartingState.GitAsync(repo,["-c","user.name=Public Fixture","-c","user.email=fixture@invalid.example","commit","--allow-empty","-m","Public reference fixture"]);
        var content="A public fixture repository contains configured context 65536.\n";var file=Path.Combine(repo,"configuration.txt");File.WriteAllText(file,content,new UTF8Encoding(false));
        var bytes=File.ReadAllBytes(file);var hash=CanonicalStore.Hash(bytes);
        using var store=new CanonicalStore(Path.Combine(proofRoot,"state"));
        store.RegisterWorkspace(new("repo_reference","ws_reference",repo,repo,"main","base"));
        var json="{}";store.CreateTask("task_reference","ws_reference",CanonicalStore.Hash(Encoding.UTF8.GetBytes(json)),json);
        store.Checkpoint(new("task_reference","ws_reference","fixture","initial","task_checkpoint","execution_evidence","host_observed",[],JsonSerializer.SerializeToElement(new{phase="existing_task"})),"{\"phase\":\"existing_task\"}",3,5,1,2);
        var before=store.GetTask("task_reference")!;
        var original=File.ReadAllBytes(Path.Combine(bundle,"original.pdf"));var text=File.ReadAllBytes(Path.Combine(bundle,"extracted.md"));var manifest=File.ReadAllBytes(Path.Combine(bundle,"manifest.json"));
        var reference=ExternalReferenceCorpus.Import(store,"task_reference",original,text,manifest);
        using var m=JsonDocument.Parse(manifest);var p=m.RootElement.GetProperty("page_map")[0];
        var quote=Encoding.UTF8.GetString(text.AsSpan(p.GetProperty("start_byte").GetInt32(),p.GetProperty("end_byte").GetInt32()-p.GetProperty("start_byte").GetInt32())).Trim();
        var request=new ExternalClaimVerification.Request(1,reference.EventId,1,quote,"configuration.txt",hash,"context 65536");
        var first=await ExternalClaimVerification.CheckAsync(store,"task_reference",repo,request);
        if(!first.RepositoryQuoteFound||first.Duplicate||first.SemanticClaimsVerified||first.KnowledgeAdopted)throw new Exception("Literal observation wrongly granted truth/adoption");
        var duplicate=await ExternalClaimVerification.CheckAsync(store,"task_reference",repo,request);
        if(!duplicate.Duplicate||duplicate.EventId!=first.EventId)throw new Exception("Exact request duplicate differs");
        var negative=await ExternalClaimVerification.CheckAsync(store,"task_reference",repo,request with{RepositoryQuote="context 128000"});
        if(negative.RepositoryQuoteFound)throw new Exception("Unsupported repository quotation accepted");
        var checks=new List<string>{"literal_repository_observation_not_semantic_truth","exact_request_idempotent","contradicting_quote_recorded_unsupported"};
        async Task Reject(ExternalClaimVerification.Request r,string label){var count=store.EventIds("task_reference").Length;
            try{await ExternalClaimVerification.CheckAsync(store,"task_reference",repo,r);}catch(Exception ex)when(ex is InvalidDataException or UnauthorizedAccessException){if(store.EventIds("task_reference").Length!=count)throw new Exception("Rejected request wrote event");checks.Add(label);return;}throw new Exception("Invalid request accepted: "+label);}
        await Reject(request with{Page=2},"quotation_must_match_selected_original_page");
        await Reject(request with{ExpectedRepositoryHash=new string('0',64)},"stale_expected_repository_hash_rejected");
        await Reject(request with{RepositoryPath="../configuration.txt"},"repository_scope_preserved");
        File.AppendAllText(file,"Current repository changed.\n");await Reject(request,"old_request_rejected_after_repository_change");
        File.WriteAllBytes(file,bytes);
        await AttemptStartingState.GitAsync(repo,["-c","user.name=Public Fixture","-c","user.email=fixture@invalid.example","commit","--allow-empty","-m","HEAD-only advance"]);
        var advanced=await ExternalClaimVerification.CheckAsync(store,"task_reference",repo,request);
        if(advanced.Duplicate||advanced.EventId==first.EventId||advanced.Head==first.Head)throw new Exception("Current HEAD observation reused historical event");
        checks.Add("same_request_after_HEAD_advance_gets_new_observation");
        await AttemptStartingState.GitAsync(repo,["add","--","configuration.txt"]);
        var tracked=await ExternalClaimVerification.CheckAsync(store,"task_reference",repo,request);
        if(tracked.Duplicate||tracked.EventId==advanced.EventId||tracked.Head!=advanced.Head||tracked.RepositoryHash!=advanced.RepositoryHash)throw new Exception("Changed diff identity reused historical event");
        checks.Add("same_bytes_HEAD_changed_diff_gets_new_observation");
        store.CreateTask("other_task","ws_reference",CanonicalStore.Hash(Encoding.UTF8.GetBytes(json)),json);
        var owner=store.ResolveExternalReferenceOwner("ws_reference",request.ReferenceEventId);
        if(owner.TaskId!="task_reference"||!(await ExternalClaimVerification.CheckAsync(store,owner.TaskId,repo,request)).Duplicate)throw new Exception("New latest task displaced explicit reference owner");
        checks.Add("explicit_reference_owner_survives_new_latest_task");
        try{store.ResolveExternalReferenceOwner("another_workspace",request.ReferenceEventId);throw new Exception("Other workspace reference accepted");}catch(UnauthorizedAccessException){checks.Add("cross_workspace_reference_owner_rejected");}
        try{await ExternalClaimVerification.CheckAsync(store,"other_task",repo,request);throw new Exception("Other task reused reference");}catch(UnauthorizedAccessException){checks.Add("cross_task_reference_rejected");}
        var after=store.GetTask("task_reference")!;if(after!=before)throw new Exception("Reference observation changed existing state/counters");
        if(!File.ReadAllBytes(file).SequenceEqual(bytes))throw new Exception("Repository content changed by check");
        checks.Add("read_only_repository_and_existing_task_state_preserved");
        var result=new{status="PASS_EXPLICIT_HOST_REPOSITORY_REFERENCE_OBSERVATION_ONLY",task_id="task_reference",checks,reference,first,duplicate,negative,advanced,tracked,
            source_content=content,canonical_root=store.Root,model_calls=0,knowledge_adoptions=0,semantic_claims_verified=false,production_deployed=false};
        File.WriteAllText(Path.Combine(proofRoot,"proof.json"),JsonSerializer.Serialize(result));Console.WriteLine(JsonSerializer.Serialize(result));
    }
}
