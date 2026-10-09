using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class HostValidationRescueTests
{
    private static void Check(bool ok,string reason){if(!ok)throw new Exception(reason);}
    private static async Task Reject(Func<Task> action,string name,List<string> checks){
        try{await action();}catch(Exception e)when(e is UnauthorizedAccessException or InvalidDataException or IOException){checks.Add(name);return;}
        throw new Exception("Unexpected Rescue validation acceptance: "+name);
    }
    internal static async Task RunAsync(string root,string sdkManifestPath){
        root=Path.GetFullPath(root);if(File.Exists(Path.Combine(root,"rescue-isolation-proof.json")))throw new IOException("Fresh Rescue fixture required");
        var repo=Path.Combine(root,"repo");var head=Encoding.UTF8.GetString(await AttemptStartingState.GitAsync(repo,["rev-parse","HEAD"])).Trim();
        var test=new RequiredTest("dotnet_build","Fixture.csproj");var request=new AgentTaskRequest("Validate a public synthetic Rescue candidate",["isolated candidate build and main unchanged"],"LOW",false,["Fixture.cs"],[test]);
        using var store=new CanonicalStore(Path.Combine(root,"host"));using var journal=new HostTaskJournal(store,repo,"rescue_isolation_fixture",request,head);
        store.ChangePolicies([new(HostTaskJournal.RepositoryId(repo),repo,"auto",request.AllowedFiles,["task_evidence_only"])],"explicit_public_fixture_test");
        using var sdkManifest=JsonDocument.Parse(File.ReadAllBytes(sdkManifestPath));var sdkFiles=sdkManifest.RootElement.GetProperty("files").Deserialize<HostValidationIsolation.InputFile[]>()!;
        var publisher=Path.Combine(store.Root,"validation-inputs","fixture");
        var policy=new HostValidationPolicy.Policy(HostTaskJournal.RepositoryId(repo),"10.0.303",new(Path.Combine(publisher,"sdk"),sdkFiles),new(Path.Combine(publisher,"packages"),[]),new(Path.Combine(publisher,"offline-feed"),[]));
        var policyHash=HostValidationPolicy.Register(journal,policy);
        var starting=await AttemptStartingState.SaveAsync(journal,1);
        const string failed="public static class Fixture { public const int Value = -1; }\n";
        await using(var gate=new SideEffectGate(repo,request.AllowedFiles,head,false,journal))await gate.HandleAsync(new(gate.PipeName,gate.Nonce,"write","Fixture.cs",failed));
        var mainHash=AttemptStartingState.Hash(await AttemptStartingState.CaptureAsync(repo,null));
        var rescue=new HostRescue(journal);var ticketHash=await rescue.ReserveAsync(starting);var ticket=rescue.Load(ticketHash);
        var candidate=new HostRescue.Candidate(journal.TaskId,ticket.AssignmentId,ticket.Generation,head,ticket.StartingStateHash,ticket.StartingDiffHash,
            [new("Fixture.cs",starting.State.Files.Single(f=>f.Path=="Fixture.cs").Hash,"public static class Fixture { public const int Value = 42; }\n")],"Public synthetic candidate; no external model result");
        var checks=new List<string>();
        var unimported=store.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(candidate));
        store.Append(new(journal.TaskId,ticket.WorkspaceId,"windows_host","public_untrusted_candidate","fixture_candidate","fixture","unverified",[unimported],JsonSerializer.SerializeToElement(new{synthetic=true})));
        await Reject(()=>HostValidationPolicy.RunRescueAsync(journal,ticketHash,unimported,ticket.RescueRoot,"",test),"rescue_without_host_applied_receipt",checks);
        var candidateHash=await rescue.ImportAsync(ticketHash,candidate);
        await Reject(()=>HostValidationPolicy.PrepareDotnetAsync(journal,ticket.RescueRoot,test),"rescue_registration_alone_rejected",checks);
        await Reject(()=>HostValidationPolicy.RunRescueAsync(journal,ticketHash,candidateHash,repo,"",test),"rescue_binding_cannot_authorize_main",checks);
        await Reject(()=>HostValidationPolicy.RunRescueAsync(journal,ticketHash,candidateHash,root,"",test),"rescue_wrong_root",checks);
        await Reject(()=>HostValidationPolicy.RunRescueAsync(journal,new string('0',64),candidateHash,ticket.RescueRoot,"",test),"rescue_unknown_ticket",checks);
        var wrong=candidate with{AssignmentId="other_assignment"};var wrongHash=store.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(wrong));
        store.Append(new(journal.TaskId,ticket.WorkspaceId,"windows_host","public_wrong_candidate","fixture_candidate","fixture","unverified",[wrongHash],JsonSerializer.SerializeToElement(new{synthetic=true})));
        await Reject(()=>HostValidationPolicy.RunRescueAsync(journal,ticketHash,wrongHash,ticket.RescueRoot,"",test),"rescue_wrong_candidate_assignment",checks);
        await Reject(()=>HostValidationPolicy.RunRescueAsync(journal,ticketHash,candidateHash,ticket.RescueRoot,"",new("dotnet_build","Other.csproj")),"rescue_unrequested_test",checks);
        using(var otherJournal=new HostTaskJournal(store,repo,"rescue_unrelated_task",request,head))
            await Reject(()=>HostValidationPolicy.RunRescueAsync(otherJournal,ticketHash,candidateHash,ticket.RescueRoot,"",test),"rescue_other_task",checks);
        var source=Path.Combine(ticket.RescueRoot,"Fixture.cs");var original=File.ReadAllBytes(source);File.AppendAllText(source,"// altered\n");
        await Reject(()=>HostValidationPolicy.RunRescueAsync(journal,ticketHash,candidateHash,ticket.RescueRoot,"",test),"rescue_post_import_source_drift",checks);File.WriteAllBytes(source,original);
        var runs=Path.Combine(store.Root,"validation-runs");Check(!Directory.Exists(runs),"Negative binding check started isolated validation");checks.Add("all_binding_negatives_before_child_creation");
        var result=await HostValidationPolicy.RunRescueAsync(journal,ticketHash,candidateHash,ticket.RescueRoot,"",test);
        Check(result.Passed,"Actual ticket-bound isolated build failed");checks.Add("real_ticket_bound_isolated_build");
        Check(mainHash==AttemptStartingState.Hash(await AttemptStartingState.CaptureAsync(repo,null))&&File.ReadAllText(Path.Combine(repo,"Fixture.cs"))==failed,"Rescue validation changed main");checks.Add("main_source_unchanged");
        var spec=new CanonicalStore.TaskSpec(journal.TaskId,"rescue_replacement",ticket.WorkerId,ticket.WorkspaceId,policy.RepositoryId,head,ticket.StartingStateHash,ticket.StartingDiffHash,request.AllowedFiles,[],request.Requirement,request.AcceptanceCriteria,request.RequiredTests,[],2);
        store.ReserveAssignment(spec);
        await Reject(()=>ToolRouter.RunAsync(ticket.RescueRoot,"",test),"stale_assignment_rejects_existing_admission",checks);
        Check(Directory.GetDirectories(runs).Length==1,"Stale admission started another child");
        var proof=new{status="PASS_EXACT_RESCUE_TICKET_ADMISSION_FIXED_BUILD_ONLY",checks,ticket_hash=ticketHash,candidate_hash=candidateHash,policy_hash=policyHash,main_state_hash=mainHash,rescue_root=ticket.RescueRoot,actual_router_result=result,production_qualified=false,actual_rescue_critic_or_promotion_qualified=false,general_routes_qualified=false};
        var path=Path.Combine(root,"rescue-isolation-proof.json");File.WriteAllText(path,JsonSerializer.Serialize(proof));Console.WriteLine(JsonSerializer.Serialize(new{status="PASS",checks,proof=path}));
    }
}
