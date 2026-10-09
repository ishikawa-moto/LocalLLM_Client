using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class HostPersonalValidationTests
{
    internal static async Task RunAsync(string root)
    {
        root=Path.GetFullPath(root);if(Directory.Exists(root))throw new IOException("Fresh personal route proof required");
        var repo=Path.Combine(root,"repo");Directory.CreateDirectory(repo);
        await File.WriteAllTextAsync(Path.Combine(repo,".gitignore"),"node_modules/\n.localbrain/\n");
        await File.WriteAllTextAsync(Path.Combine(repo,"package.json"),"""{"private":true,"scripts":{"test":"node test.cjs"}}""");
        await File.WriteAllTextAsync(Path.Combine(repo,"test.cjs"),"const assert=require('node:assert/strict');assert.equal(2+2,4);console.log('actual assertion passed');\n");
        await File.WriteAllTextAsync(Path.Combine(repo,"sample.txt"),"clean\n");
        await AttemptStartingState.GitAsync(repo,["init"]);
        await AttemptStartingState.GitAsync(repo,["add","."]);
        await AttemptStartingState.GitAsync(repo,["-c","user.name=LocalBrain fixture","-c","user.email=fixture@invalid","commit","-m","personal route baseline"]);
        var head=Encoding.UTF8.GetString(await AttemptStartingState.GitAsync(repo,["rev-parse","HEAD"])).Trim();
        var git=new RequiredTest("git_diff_check",null);var npm=new RequiredTest("npm_test",null);
        var request=new AgentTaskRequest("Personal named validation fixture",["actual checks"],"LOW",false,["sample.txt"],[git,npm]);
        using var store=new CanonicalStore(Path.Combine(root,"host"));using var journal=new HostTaskJournal(store,repo,"personal_route_fixture",request,head);
        var checks=new List<string>();var results=new List<ToolRouter.TestResult>();
        void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);}
        async Task Reject(Func<Task> action,string name){try{await action();}catch(UnauthorizedAccessException){checks.Add(name);return;}throw new Exception("Unexpected acceptance: "+name);}
        await Reject(()=>HostPersonalValidation.RunAsync(journal,repo,"",git,null,default),"host_opt_in_required");
        journal.EnablePersonalValidation();
        await Reject(()=>HostPersonalValidation.RunAsync(journal,repo,"",new("npm_test","unexpected"),null,default),"typed_requested_test_required");
        await Reject(()=>HostValidationPolicy.RunAsync(journal,root,"",git),"registered_source_root_required");
        var wsl=await WslWorkspace.ResolveRegisteredMainAsync(store,HostTaskJournal.WorkspaceId(repo));
        await Reject(()=>HostValidationPolicy.RunAsync(journal,repo,wsl+"-wrong",npm),"wsl_source_identity_required");
        var gitPass=await HostValidationPolicy.RunAsync(journal,repo,wsl,git);results.Add(gitPass);Check(gitPass.Passed,"actual_windows_git_pass");
        await File.WriteAllTextAsync(Path.Combine(repo,"sample.txt"),"bad whitespace   \n");
        var gitFail=await HostValidationPolicy.RunAsync(journal,repo,wsl,git);results.Add(gitFail);Check(!gitFail.Passed && gitFail.ExitCode!=0,"actual_windows_git_whitespace_failure");
        await File.WriteAllTextAsync(Path.Combine(repo,"sample.txt"),"clean\n");
        var npmPass=await ToolRouter.RunAsync(repo,wsl,npm);results.Add(npmPass);Check(npmPass.Passed && npmPass.StdoutBytes>0,"actual_wsl_npm_assertion_pass");
        await File.WriteAllTextAsync(Path.Combine(repo,"test.cjs"),"require('node:assert/strict').equal(2+2,5);\n");
        var npmFail=await HostValidationPolicy.RunAsync(journal,repo,wsl,npm);results.Add(npmFail);Check(!npmFail.Passed && npmFail.ExitCode!=0,"actual_wsl_npm_assertion_failure");
        await File.WriteAllTextAsync(Path.Combine(repo,"test.cjs"),"process.stdout.write('x'.repeat(2500000));\n");
        var outputFail=await ToolRouter.RunAsync(repo,wsl,npm);results.Add(outputFail);Check(!outputFail.Passed && outputFail.FailureCodes.Contains("output_limit") && outputFail.StdoutBytes+outputFail.StderrBytes<=2000000,"combined_output_limit");
        await File.WriteAllTextAsync(Path.Combine(repo,"test.cjs"),"require('node:fs').appendFileSync('sample.txt','changed by test');\n");
        var changed=await HostValidationPolicy.RunAsync(journal,repo,wsl,npm);results.Add(changed);Check(!changed.Passed && changed.FailureCodes.Contains("source_changed"),"test_source_change_not_passed");
        await File.WriteAllTextAsync(Path.Combine(repo,"sample.txt"),"clean\n");
        await File.WriteAllTextAsync(Path.Combine(repo,"test.cjs"),"setTimeout(()=>console.log('done'),2000);\n");
        using(var cancel=new CancellationTokenSource())
        {
            var cancelled=await HostPersonalValidation.RunAsync(journal,repo,wsl,npm,null,cancel.Token,_=>cancel.Cancel());
            results.Add(cancelled);Check(!cancelled.Passed && cancelled.FailureCodes.Contains("cancelled"),"owned_process_cancel_reaped");
        }
        try{await HostPersonalValidation.RunAsync(journal,repo,wsl,npm,null,default,_=>throw new IOException("synthetic after-start IO fault"));throw new Exception("Fault unexpectedly passed");}
        catch(IOException){checks.Add("after_start_io_fault_reaped");}
        var faults=store.EventIds(journal.TaskId).Select(id=>store.ReadTaskEvent(journal.TaskId,id)).Where(e=>e.Input.EventType=="trusted_personal_validation_failed").ToArray();
        Check(faults.Length==1 && JsonDocument.Parse(store.ReadEvidence(faults[0].Input.EvidenceRefs.Single())).RootElement.GetProperty("owned_process_exit_confirmed").GetBoolean(),"io_fault_canonical_exit_confirmation");
        await File.WriteAllTextAsync(Path.Combine(repo,"test.cjs"),"const assert=require('node:assert/strict');assert.equal(2+2,4);console.log('actual assertion passed');\n");
        store.ChangePolicies([new(HostTaskJournal.RepositoryId(repo),repo,"auto",request.AllowedFiles,["task_evidence_only"])],"explicit_public_fixture_test");
        var starting=await AttemptStartingState.SaveAsync(journal,1);
        var rescue=new HostRescue(journal);var ticketHash=await rescue.ReserveAsync(starting);var ticket=rescue.Load(ticketHash);
        var candidate=new HostRescue.Candidate(journal.TaskId,ticket.AssignmentId,ticket.Generation,head,ticket.StartingStateHash,ticket.StartingDiffHash,
            [new("sample.txt",starting.State.Files.Single(f=>f.Path=="sample.txt").Hash,"reviewed candidate\n")],"Synthetic personal route candidate");
        var candidateHash=await rescue.ImportAsync(ticketHash,candidate);
        var rescueWsl=await WslWorkspace.ResolveRegisteredRescueAsync(store,ticket.WorkspaceId);
        var mainHash=AttemptStartingState.Hash(await AttemptStartingState.CaptureAsync(repo,null));
        await Reject(()=>HostValidationPolicy.RunAsync(journal,ticket.RescueRoot,rescueWsl,npm),"rescue_registration_alone_rejected");
        var rescueGit=await HostValidationPolicy.RunRescueAsync(journal,ticketHash,candidateHash,ticket.RescueRoot,rescueWsl,git);results.Add(rescueGit);Check(rescueGit.Passed,"actual_ticket_bound_rescue_git");
        var rescueNpm=await HostValidationPolicy.RunRescueAsync(journal,ticketHash,candidateHash,ticket.RescueRoot,rescueWsl,npm);results.Add(rescueNpm);Check(rescueNpm.Passed,"actual_ticket_bound_rescue_npm");
        Check(mainHash==AttemptStartingState.Hash(await AttemptStartingState.CaptureAsync(repo,null)),"rescue_tests_leave_main_unchanged");
        var proof=new{status="PASS_TRUSTED_PERSONAL_GIT_NPM_ONLY",checks,results,task_id=journal.TaskId,head,
            canonical_root=store.Root,hostile_code_isolation=false,production_changed=false};
        File.WriteAllText(Path.Combine(root,"proof.json"),JsonSerializer.Serialize(proof));Console.WriteLine(JsonSerializer.Serialize(proof));
    }
}
