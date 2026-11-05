using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.AgentV2;
using LocalBrain.ClientHost.Slack;

internal static class ExternalReviewAvailabilityTests
{
    private static AgentTaskRequest Request(string risk="NORMAL")=>AgentTaskRequest.Parse("""
        {"requirement":"Add two integers","acceptance_criteria":["2+3=5"],"risk":"NORMAL","approved_high_risk":false,
         "allowed_files":["fixture.py"],"required_tests":[{"kind":"git_diff_check"}]}
        """) with {Risk=risk};
    internal static async Task RunAsync()
    {
        var checks=0;void Check(bool value,string reason){if(!value)throw new Exception(reason);checks++;}
        async Task Reject(Func<Task> action){try{await action();}catch(UnauthorizedAccessException){checks++;return;}throw new Exception("Authorization bypassed");}
        var solCalls=0;var opusCalls=0;
        Task<SolReviewer.Review> Sol(string verdict){solCalls++;return Task.FromResult(new SolReviewer.Review(verdict,verdict=="ISSUES"?["bug"]:[]));}
        Task<CopilotReviewer.Review> Opus(){opusCalls++;return Task.FromResult(new CopilotReviewer.Review("PASS",[]));}
        var low=await ExternalReviewCoordinator.RouteAsync("LOW",()=>Sol("PASS"),Opus,default);
        Check(low.Passed && solCalls==0 && opusCalls==0,"LOW called an external reviewer");
        var normal=await ExternalReviewCoordinator.RouteAsync("NORMAL",()=>Sol("PASS"),Opus,default);
        Check(normal.Passed && solCalls==1 && opusCalls==0 && normal.Routing.RouteName=="sol","NORMAL did not prefer Sol");
        var fallback=await ExternalReviewCoordinator.RouteAsync("NORMAL",()=>Sol("UNAVAILABLE"),Opus,default);
        Check(fallback.Passed && !fallback.SolRequired && opusCalls==1 && fallback.Routing.RouteName=="copilot","NORMAL fallback failed");
        foreach(var verdict in new[]{"ISSUES","INVALID"}) {
            var blocked=await ExternalReviewCoordinator.RouteAsync("NORMAL",()=>Sol(verdict),Opus,default);
            Check(!blocked.Passed && opusCalls==1,"Failed verdict shopped for PASS");
        }
        var high=await ExternalReviewCoordinator.RouteAsync("HIGH",()=>Sol("UNAVAILABLE"),Opus,default);
        Check(!high.Passed && high.SolRequired && high.Unavailable && opusCalls==1,"HIGH Sol gate weakened");
        await Reject(()=>ExternalReviewCoordinator.RouteAsync("NORMAL",()=>throw new UnauthorizedAccessException("approval pending"),Opus,default));
        Check(opusCalls==1,"Approval denial triggered fallback");
        var local=new LocalValidationV2(true,true,true,true,false,[],false,true,true,"sol");
        Check(!local.IsComplete && !(local with{SolReviewPassed=true,LocalReviewPassed=false}).IsComplete,"Local/HIGH gates weakened");
        using(RestrictedExecution.EnterSlack()) {
            await Reject(()=>SolReviewer.RunReviewAsync("PUBLIC fixture","high",default));
            await Reject(()=>CopilotReviewer.ReviewFinalAsync("PUBLIC fixture",default));
            await Reject(()=>ExternalReviewCoordinator.RouteAsync("NORMAL",()=>Sol("PASS"),Opus,default));
        }
        Check(!await SolReviewer.HealthAsync(),"Unqualified/no-journal route passed health");
        Check(ReviewerCli.Instructions(true).Contains("pre-edit plan review") &&
            !ReviewerCli.Instructions(true).Contains("supported by the diff and passed") &&
            ReviewerCli.Instructions(false).Contains("supported by the diff and passed"),"Plan/final review rubrics mixed");
        var start=ReviewerCli.StartInfo("C:\\public\\codex.exe","C:\\public","sol","high");
        var args=start.ArgumentList.ToArray();
        Check(!start.UseShellExecute && start.RedirectStandardInput && args.Contains("features.shell_tool=false") &&
            args.Contains("features.code_mode_host=false") && args.Contains("features.view_image=false") &&
            args.Contains("permissions.review_packet.filesystem={\":root\"=\"deny\",\":minimal\"=\"read\"}") &&
            args.Contains("forced_login_method=\"chatgpt\"") && args.Contains("--ignore-user-config") && args.Contains("--ephemeral"),"Packet-only CLI restrictions lost");
        Check(!start.Environment.Keys.Any(k=>k.StartsWith("OPENAI_",StringComparison.OrdinalIgnoreCase)||k.StartsWith("LOCALBRAIN_",StringComparison.OrdinalIgnoreCase)||k.StartsWith("OTEL_",StringComparison.OrdinalIgnoreCase)),"Inherited secrets/telemetry");
        var opusStart=ReviewerCli.StartInfo("C:\\public\\copilot.exe","C:\\public","copilot","high");
        Check(opusStart.ArgumentList.Contains("--available-tools=") && opusStart.ArgumentList.Contains("--deny-tool=*"),"Opus generic file access reintroduced");
        const string answer="{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"{\\\"verdict\\\":\\\"PASS\\\",\\\"issues\\\":[]}\"}}\n{\"type\":\"turn.completed\"}";
        Check(ReviewerCli.DecodeCodex(answer).Status=="OK","CLI JSONL answer rejected");
        Check(ReviewerCli.DecodeCodex("{\"type\":\"item.started\",\"item\":{\"type\":\"command_execution\"}}\n"+answer).Status=="INVALID","Tool event passed");
        Check(ReviewerCli.DecodeCodex(answer.Split('\n')[0]).Status!="OK","Incomplete turn passed");
        Check(ReviewerCli.FailureReason("rate limit reached")=="USAGE_LIMIT" &&
            ReviewerCli.FailureReason("authentication expired")=="AUTHENTICATION" &&
            ReviewerCli.FailureReason("connection failed")=="CONNECTION","Unavailable reason lost");
        foreach(var invalid in new[]{"{\"verdict\":\"PASS\",\"issues\":[],\"approved\":true}","{\"verdict\":\"PASS\",\"verdict\":\"ISSUES\",\"issues\":[]}","{\"verdict\":\"ISSUES\",\"issues\":[\" \" ]}"})
            Check(SolReviewer.Parse(invalid).Verdict=="INVALID","Malformed verdict passed");
        var parent=Directory.CreateTempSubdirectory("review-host-proof-");
        try {
            var repo=Directory.CreateDirectory(Path.Combine(parent.FullName,"repo")).FullName;
            using var store=new CanonicalStore(Path.Combine(parent.FullName,"canonical"));
            using var journal=new HostTaskJournal(store,repo,"public_review_fixture",Request(),"fixture");
            var unavailable=await ExternalReviewCoordinator.RunAsync(Request(),"def add(a,b): return a+b",["fixture.py"],[],new("",[]),default);
            Check(!unavailable.Passed && unavailable.Unavailable,"Absent configured CLI became PASS");
            Check(store.EventIds(journal.TaskId).Select(id=>store.ReadTaskEvent(journal.TaskId,id)).Count(e=>e.Input.EventType=="external_review_result")==2,"Unavailable review evidence lost");
            await Reject(()=>SolReviewer.RunReviewAsync("api_key=synthetic_credential_not_exportable","high",default));
            Check(store.GetTask(journal.TaskId)!.ExternalReviews==0,"Unqualified route dispatched");
        } finally {CleanFixture(parent);}
        Console.WriteLine(JsonSerializer.Serialize(new{review_checks=checks,status="PASS",external_inference=false,production_changes=false}));
    }

    internal static async Task LiveAsync(string binary,string sha256)
    {
        // Public fixture, isolated canonical store and human-selected exact CLI. No production config or data.
        var parent=Directory.CreateTempSubdirectory("review-live-public-");
        try {
            var repo=Directory.CreateDirectory(Path.Combine(parent.FullName,"repo")).FullName;
            using var store=new CanonicalStore(Path.Combine(parent.FullName,"canonical"));
            var request=Request();using var journal=new HostTaskJournal(store,repo,"public_live_review",request,"fixture");
            store.ChangePolicies([new(HostTaskJournal.RepositoryId(repo),repo,"auto",["fixture.py"],["task_evidence_only"])],"fixture_human");
            File.WriteAllText(Path.Combine(store.Root,"reviewers.json"),JsonSerializer.Serialize(new ReviewerCli.Settings(new(binary,sha256,true,true)),ClientConfig.JsonOptions));
            var result=await ExternalReviewCoordinator.RunAsync(request,"def add(a,b): return a+b",["fixture.py"],
                [new("git_diff_check","public_fixture",true,0,0,0,new string('0',64),new string('0',64),0,null,[])],new("",[]),default);
            var events=store.EventIds(journal.TaskId).Select(id=>store.ReadTaskEvent(journal.TaskId,id)).ToArray();
            if(!result.Passed || result.Sol.Verdict!="PASS" || result.Routing.RouteName!="sol" ||
                store.GetTask(journal.TaskId)!.ExternalReviews!=1 || !events.Any(e=>e.Input.EventType=="external_review_result") ||
                !events.Any(e=>e.Input.EventType=="external_dispatch_authorized"))
                throw new Exception("Live Host review did not qualify: "+JsonSerializer.Serialize(result));
            await new LocalValidationV2(true,true,true,true,true,[],false,true,result.Passed,"sol").WriteAsync(repo);
            if(!store.EventIds(journal.TaskId).Select(id=>store.ReadTaskEvent(journal.TaskId,id)).Any(e=>e.Input.EventType=="validation_result"))
                throw new Exception("Canonical validation evidence lost");
            Console.WriteLine(JsonSerializer.Serialize(new{status="PASS",model=ReviewerCli.Model("sol"),actual_model_verified=false,
                result.Sol.Verdict,canonical_events=events.Select(e=>e.Input.EventType),external_inferences=1,production_changes=0}));
        } finally {CleanFixture(parent);}
    }
    private static void CleanFixture(DirectoryInfo parent)
    {
        // Canonical blobs are immutable/read-only. Only this test's freshly created directory is disposable.
        var full=Path.GetFullPath(parent.FullName);
        if(!full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase) ||
            !parent.Name.StartsWith("review-",StringComparison.Ordinal))throw new Exception("Fixture cleanup escaped temp root");
        foreach(var file in parent.EnumerateFiles("*",SearchOption.AllDirectories))file.Attributes&=~FileAttributes.ReadOnly;
        parent.Delete(true);
    }
}
