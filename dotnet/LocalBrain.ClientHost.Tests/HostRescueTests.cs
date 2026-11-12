using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class HostRescueTests
{
    internal sealed record Fixture(string MainRoot,string HostRoot,string TaskId,string Head,string TicketHash,HostRescue.Candidate Candidate,string? CandidateHash=null,string? ValidationHash=null);
    private static void Check(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
    private static async Task Reject(Func<Task> action,string reason){try{await action();}catch(Exception e)when(e is UnauthorizedAccessException or InvalidDataException or IOException or JsonException){return;}throw new InvalidOperationException(reason);}
    internal static AgentTaskRequest Request=>AgentTaskRequest.Parse("""{"requirement":"Write HELLO plus LF in greeting.txt and SECOND plus LF in other.txt and THIRD plus LF in new.txt","acceptance_criteria":["greeting.txt contains exactly HELLO plus LF","other.txt contains exactly SECOND plus LF","new.txt contains exactly THIRD plus LF","all exact byte checks pass"],"risk":"LOW","approved_high_risk":false,"allowed_files":["greeting.txt","other.txt","new.txt"],"required_tests":[{"kind":"npm_test"},{"kind":"git_diff_check"}]}""");
    private static string Descriptor(string root)=>Path.Combine(root,"fixture.json");
    private static void Save(string root,Fixture fixture)=>File.WriteAllText(Descriptor(root),JsonSerializer.Serialize(fixture),new UTF8Encoding(false));
    private static Fixture Read(string root)=>JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Descriptor(root)))!;
    internal static async Task<Fixture> PrepareAsync(string root,AgentTaskRequest? request=null,string exportMode="auto",string[]? exportScope=null) {
        request??=Request;
        root=Path.GetFullPath(root);CanonicalStore.GuardPath(root);if(Directory.Exists(root))throw new IOException("Fixture root must be new");Directory.CreateDirectory(root);
        var main=Path.Combine(root,"repo");Directory.CreateDirectory(main);
        File.WriteAllText(Path.Combine(main,".gitignore"),".localbrain/\nnode_modules/\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(main,"greeting.txt"),"START\n",new UTF8Encoding(false));File.WriteAllText(Path.Combine(main,"other.txt"),"START\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(main,"package.json"),"""{"scripts":{"test":"node test.cjs"}}""",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(main,"test.cjs"),"""const fs=require('fs');const cp=require('child_process');for(const [file,value] of [['greeting.txt','HELLO\n'],['other.txt','SECOND\n'],['new.txt','THIRD\n']])if(!fs.readFileSync(file).equals(Buffer.from(value)))throw Error('Exact bytes '+file);if(cp.spawnSync('git',['rev-parse','--show-toplevel'],{encoding:'utf8'}).status!==0)throw Error('WSL Git identity failed');console.log('All three exact byte checks and WSL Git identity passed');""",new UTF8Encoding(false));
        await AttemptStartingState.GitAsync(main,["init","-q"]);await AttemptStartingState.GitAsync(main,["add","--",".gitignore","greeting.txt","other.txt","package.json","test.cjs"]);
        await AttemptStartingState.GitAsync(main,["-c","user.name=LocalBrain fixture","-c","user.email=fixture@example.invalid","commit","-qm","fixture baseline"]);
        var head=Encoding.UTF8.GetString(await AttemptStartingState.GitAsync(main,["rev-parse","HEAD"])).Trim();var hostRoot=Path.Combine(root,"host");var task="rescue_"+Guid.NewGuid().ToString("N");
        using var store=new CanonicalStore(hostRoot);using var journal=new HostTaskJournal(store,main,task,request,head);
        store.ChangePolicies([new(HostTaskJournal.RepositoryId(main),main,exportMode,request.AllowedFiles,exportScope??["task_evidence_only"])],"explicit_public_fixture_test");
        var starting=await AttemptStartingState.SaveAsync(journal,1);
        // Host-generated synthetic failure, explicitly distinguished from an actual Codex/Qwen response.
        await using(var gate=new SideEffectGate(main,Request.AllowedFiles,head,false,journal))foreach(var file in Request.AllowedFiles)await gate.HandleAsync(new(gate.PipeName,gate.Nonce,"write",file,"BROKEN\n"));
        var rescue=new HostRescue(journal);var ticketHash=await rescue.ReserveAsync(starting);var ticket=rescue.Load(ticketHash);
        Check(File.ReadAllText(Path.Combine(ticket.RescueRoot,"greeting.txt"))=="START\n","Rescue used failed rather than pre-attempt diff");
        Check(File.ReadAllText(Path.Combine(main,"greeting.txt"))=="BROKEN\n","Reserve changed main");
        var candidate=new HostRescue.Candidate(task,ticket.AssignmentId,ticket.Generation,head,ticket.StartingStateHash,ticket.StartingDiffHash,
            [new("greeting.txt",starting.State.Files.Single(f=>f.Path=="greeting.txt").Hash,"HELLO\n"),new("other.txt",starting.State.Files.Single(f=>f.Path=="other.txt").Hash,"SECOND\n"),new("New.txt",null,"THIRD\n")],"Synthetic untrusted candidate for protocol qualification; not an actual Codex model response");
        var fixture=new Fixture(main,hostRoot,task,head,ticketHash,candidate);Save(root,fixture);return fixture;
    }
    internal static async Task ExternalProofAsync(string root) {
        var request=Request with{RequiredTests=[new("dotnet_test","Fixture.csproj")]};
        var fixture=await PrepareAsync(root,request,"manual",["task_evidence_only","file:greeting.txt"]);
        using var store=new CanonicalStore(fixture.HostRoot);
        using var journal=new HostTaskJournal(store,fixture.MainRoot,fixture.TaskId,request,fixture.Head);
        var rescue=new HostRescue(journal);var ticket=rescue.Load(fixture.TicketHash);
        var prepared=rescue.PreparedExport(ticket);
        using var packet=JsonDocument.Parse(prepared.Payload);
        var sources=packet.RootElement.GetProperty("starting_source");
        Check(sources.GetArrayLength()==1 && sources[0].GetProperty("Path").GetString()=="greeting.txt" &&
            sources[0].GetProperty("Content").GetString()=="START\n","Source selection escaped explicit file export scope");
        Check(prepared.Manifest.PolicyMode=="manual" &&
            CanonicalStore.Hash(Encoding.UTF8.GetBytes(prepared.Payload))==ticket.ExportPayloadHash,"Export preview is not the exact bound packet");
        var encoded=JsonSerializer.Serialize(fixture.Candidate);
        Check(HostRescue.ParseExternalCandidate(encoded).Changes.SequenceEqual(fixture.Candidate.Changes),"Valid candidate was not decoded");
        await Reject(()=>Task.FromResult(HostRescue.ParseExternalCandidate(encoded.Insert(1,"\"Command\":\"fixture\","))),"Unexpected model field was accepted");
        await Reject(()=>Task.FromResult(HostRescue.ParseExternalCandidate(encoded.Insert(1,"\"TaskId\":\"other\","))),"Duplicate model field was accepted");
        await Reject(()=>Task.FromResult(HostRescue.ParseExternalCandidate(encoded.Replace("\"Path\":\"greeting.txt\"","\"Path\":\"other.txt\",\"Path\":\"greeting.txt\""))),"Duplicate nested field was accepted");
        await Reject(()=>rescue.ImportAsync(fixture.TicketHash,fixture.Candidate with {
            Changes=[new("test.cjs",null,"outside write scope")]
        }),"Candidate changed a file outside the task scope");
        await Reject(()=>ReviewerCli.GenerateRescueAsync(journal,prepared with {
            Manifest=prepared.Manifest with {PolicyMode="auto"}
        },CancellationToken.None,developmentCodex:"codex.exe"),"Development transport accepted a non-manual export");
        var enabled=new ReviewerCli.Endpoint("codex.exe","",RescueEnabled:true);
        Check(HostRescue.AutomaticProviders("NORMAL",new(Sol:enabled,Terra:enabled)).SequenceEqual(["terra","sol"]) &&
            HostRescue.AutomaticProviders("LOW",new(Sol:enabled,Terra:enabled)).SequenceEqual(["terra"]) &&
            HostRescue.AutomaticProviders("LOW",new(Sol:enabled)).Length==0,
            "Terra staging must keep LOW bounded and prefer Terra for NORMAL");
        var all=new ReviewerCli.Settings(Sol:enabled,Opus:enabled,Terra:enabled,Haiku:enabled,FullRescueRouting:true);
        Check(HostRescue.AutomaticProviders("LOW",all).SequenceEqual(["terra","haiku"]) &&
            HostRescue.AutomaticProviders("MEDIUM",all).SequenceEqual(["terra","haiku","sol","opus"]) &&
            HostRescue.AutomaticProviders("HIGH",all).SequenceEqual(["sol","opus"]) &&
            HostRescue.AutomaticProviders("NORMAL",all with{Haiku=null}).SequenceEqual(["terra","sol","opus"]),
            "Full routing must follow the requested order and omit disabled providers");
        Check(RescueRouting.Decide("LOW","""{"difficulty":"LOW","confidence":0.75}""",0.75).Level=="LOW" &&
            RescueRouting.Decide("LOW","""{"difficulty":"LOW","confidence":0.74}""",0.75).Level=="NORMAL" &&
            RescueRouting.Decide("NORMAL","""{"difficulty":"HIGH","confidence":0.1}""",0.75).Level=="HIGH" &&
            RescueRouting.Decide("HIGH","""{"difficulty":"LOW","confidence":1}""",0.75).Level=="HIGH",
            "Difficulty must respect the 0.75 boundary, task floor, and sticky HIGH");
        var dotnet=request with{RequiredTests=[new("dotnet_test","Fixture.csproj")]};
        var realTest=new ToolRouter.TestResult("dotnet_test","windows",true,0,40,0,"","",10,3,[]);
        Check(AutomaticCompletion.HasPassingTests(dotnet,[realTest]) &&
            !AutomaticCompletion.HasPassingTests(dotnet,[realTest with{ExecutedTests=0}]) &&
            AutomaticCompletion.ChangesVerificationInputs(dotnet,["Fixture.csproj","tests/assertion.cs"]) &&
            !AutomaticCompletion.ChangesVerificationInputs(dotnet,["Arithmetic.cs"]),
            "Automatic completion requires a non-empty passed test run without modified verification inputs");
        Check(HostRescue.AutomaticFallback(new("UNAVAILABLE",Reason:"USAGE_LIMIT")) &&
            HostRescue.AutomaticFallback(new("VERIFIED_TEST_FAILURE")) &&
            !HostRescue.AutomaticFallback(new("UNAVAILABLE",Reason:"CONNECTION")) &&
            !HostRescue.AutomaticFallback(new("INVALID")),
            "Only a provider-specific outage or a verified failing candidate can advance fallback");
        await Reject(()=>{
            using var lease=new HostEgress(store).AuthorizeDispatch(prepared,"codex_protocol");
            return Task.CompletedTask;
        },"Unapproved packet was authorized by HostEgress");
        await Reject(()=>rescue.RunExternalAsync(fixture.TicketHash,developmentCodex:"codex.exe"),
            "Development transport bypassed the registered publisher requirement");
        var advice=rescue.PrepareAdvice(fixture.TicketHash);
        Check(advice.ManifestHash!=prepared.ManifestHash && advice.Payload.Contains("GUIDANCE") &&
            rescue.PrepareAdvice(fixture.TicketHash).ManifestHash==advice.ManifestHash,
            "Advice must have its own stable, human-approvable manifest");
        await Reject(()=>Task.FromResult(rescue.PrepareAdvice(fixture.TicketHash,provider:"terra")),
            "A Sol manifest was reused for Terra");
        await Reject(()=>rescue.RunAdviceAsync(fixture.TicketHash,prepared.ManifestHash,null),
            "Code-candidate approval was accepted for advice");
        Check(SolReviewer.ParseRescue("""{"verdict":"GUIDANCE","steps":["Use the approved file and run the required test."]}""",1).Verdict=="GUIDANCE" &&
            SolReviewer.ParseRescue("""{"verdict":"GUIDANCE","steps":["Fix file"],"command":"test"}""",1).Verdict=="INVALID",
            "Advice parsing did not enforce the bounded response");
        var direct=rescue.PrepareDirectEdit(fixture.TicketHash);
        Check(direct.ManifestHash!=prepared.ManifestHash && direct.ManifestHash!=advice.ManifestHash,
            "Direct edit reused another mode's approval");
        await Reject(()=>Task.FromResult(rescue.PrepareEditDirectory(ticket,direct)),
            "Unexported existing source was copied into the edit directory");
        var cliEvents="""
            {"type":"item.completed","item":{"type":"file_change"}}
            {"type":"item.completed","item":{"type":"agent_message","text":"Updated greeting"}}
            {"type":"turn.completed"}
            """;
        Check(ReviewerCli.DecodeCodex(cliEvents).Status=="INVALID" &&
            ReviewerCli.DecodeCodex(cliEvents,allowEditEvents:true).Status=="OK",
            "Only the explicit edit mode should accept an edit event");
        var result=await rescue.RunExternalAsync(fixture.TicketHash);
        Check(result is {Status:"UNAVAILABLE",Reason:"HOST_ROUTE_NOT_QUALIFIED"},"Unqualified provider was not rejected");
        var events=store.EventIds(fixture.TaskId).Select(id=>store.ReadTaskEvent(fixture.TaskId,id)).ToArray();
        Check(!events.Any(e=>e.Input.EventType is "external_dispatch_authorized" or "rescue_cli_input" or "development_rescue_cli_started" or
            "codex_rescue_external_response" or "codex_candidate_application_intent"),"An unqualified external run performed a side effect");
        Check(File.ReadAllText(Path.Combine(ticket.RescueRoot,"greeting.txt"))=="START\n" &&
            File.ReadAllText(Path.Combine(fixture.MainRoot,"greeting.txt"))=="BROKEN\n","A blocked run modified a worktree");
        var editFixture=await PrepareAsync(root+"-edit",request,"manual",
            ["task_evidence_only","file:greeting.txt","file:other.txt","file:new.txt"]);
        using var editStore=new CanonicalStore(editFixture.HostRoot);
        using var editJournal=new HostTaskJournal(editStore,editFixture.MainRoot,editFixture.TaskId,request,editFixture.Head);
        var editing=new HostRescue(editJournal);
        var terraAdvice=editing.PrepareAdvice(editFixture.TicketHash,provider:"terra");
        Check(ReviewerCli.RescueTarget(terraAdvice)==("terra","gpt-6.1-terra") &&
            ReviewerCli.StartInfo("codex.exe",editFixture.MainRoot,"terra","high").ArgumentList.Contains("gpt-6.1-terra"),
            "Terra must be bound in the packet and selected by the Codex transport");
        var snapshotModel = "gpt-6.1-terra-2026-10-20";
        Check(ReviewerCli.ConfiguredRescueModel(new(Terra:new("codex.exe","",Model:snapshotModel)),"terra")==snapshotModel,
            "Terra model snapshots must be configurable");
        var copilotEvents="""
            {"type":"assistant.message","data":{"content":"fixture","toolRequests":[]}}
            {"type":"result"}
            """;
        Check(ReviewerCli.DecodeCopilotRescue(copilotEvents).Answer=="fixture" &&
            ReviewerCli.DecodeCopilotRescue(copilotEvents.Replace("[]","[{}]")).Status=="INVALID" &&
            ReviewerCli.DecodeCopilotRescue(copilotEvents.Replace("{\"type\":\"result\"}","")).Status=="INVALID",
            "Copilot requires a terminal reply without tool requests");
        Check(ReviewerCli.ConfiguredRescueModel(new(),"haiku")=="claude-haiku-5.5" &&
            ReviewerCli.ConfiguredRescueModel(new(),"opus")=="claude-opus-5.5",
            "Copilot models must stay distinct in Host routing");
        await Reject(()=>Task.FromResult(ReviewerCli.ConfiguredRescueModel(
            new(Terra:new("codex.exe","",Model:"gpt-6.1-sol")),"terra")),"Terra configuration accepted a different model family");
        var terraUnavailable=await ReviewerCli.GenerateRescueAsync(editJournal,terraAdvice,CancellationToken.None);
        Check(terraUnavailable is { Status:"UNAVAILABLE",Reason:"HOST_ROUTE_NOT_QUALIFIED" },
            "Unqualified Terra must fail closed independently of Sol");
        var editTicket=editing.Load(editFixture.TicketHash);
        var edit=editing.PrepareEditDirectory(editTicket,editing.PrepareDirectEdit(editFixture.TicketHash));
        Check(Directory.GetFiles(edit.Root).Length==2 && !File.Exists(Path.Combine(edit.Root,"test.cjs")),
            "Edit directory contained data beyond the approved starting sources");
        File.WriteAllText(Path.Combine(edit.Root,"greeting.txt"),"HELLO\n",new UTF8Encoding(false));
        var actual=HostRescue.CollectDirectCandidate(editTicket,edit,"Updated greeting");
        Check(actual.Changes.Length==1 && actual.Changes[0]==editFixture.Candidate.Changes[0] &&
            File.ReadAllText(Path.Combine(editTicket.RescueRoot,"greeting.txt"))=="START\n",
            "Candidate was not constructed from the actual scoped file bytes and the bound baseline");
        File.WriteAllText(Path.Combine(edit.Root,"unexpected.txt"),"outside scope");
        await Reject(()=>Task.FromResult(HostRescue.CollectDirectCandidate(editTicket,edit,"Updated greeting")),
            "Out-of-scope filesystem change was accepted");
        // Local test receipts exercise event binding only. No provider, authorization dialog or real test claim.
        var editExport=editing.PrepareDirectEdit(editFixture.TicketHash);
        var responseHash=editStore.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(actual));
        var candidateHash=await editing.ImportAsync(editFixture.TicketHash,actual);
        var applied=editStore.EventIds(editFixture.TaskId).Select(id=>editStore.ReadTaskEvent(editFixture.TaskId,id))
            .Single(e=>e.Input.EventType=="codex_candidate_applied_in_rescue");
        var resultState=applied.Input.Metadata.GetProperty("result_state_hash").GetString()!;
        var resultDiff=applied.Input.Metadata.GetProperty("codex_result_diff_hash").GetString()!;
        var validation=new HostRescue.Validation(editFixture.TicketHash,candidateHash,resultState,resultDiff,
            [new("dotnet_test","local_binding_fixture",true,0,0,0,"","",0,1,[])],"PASS",true);
        var validationHash=editStore.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(validation));
        editStore.Append(new(editFixture.TaskId,editTicket.WorkspaceId,"windows_host","binding_fixture_validation",
            "codex_rescue_validated","codex_rescue","host_verified",[editFixture.TicketHash,validationHash],
            JsonSerializer.SerializeToElement(new{ticket_hash=editFixture.TicketHash,result_state_hash=resultState})));
        editStore.Append(new(editFixture.TaskId,editTicket.WorkspaceId,"windows_host","binding_fixture_response",
            "codex_rescue_external_response","codex_rescue","untrusted_candidate",
            [editFixture.TicketHash,editExport.ManifestHash,responseHash],
            JsonSerializer.SerializeToElement(new{ticket_hash=editFixture.TicketHash,manifest_hash=editExport.ManifestHash,
                response_hash=responseHash,provider="sol",requested_model=ReviewerCli.Model("sol")})));
        await Reject(()=>Task.FromResult(new DifferentialLearning(editJournal).Assemble(editFixture.TicketHash,validationHash)),
            "Learning accepted a late Sol response without the exact prior dispatch");
        var newRepo=HostTaskJournal.RepositoryId(fixture.MainRoot);
        var scoped=editStore.SetRepositoryExportPolicy(new(newRepo,fixture.MainRoot,"auto",
            ["greeting.txt"],["task_evidence_only","file:greeting.txt"]),"proof_operator");
        Check(scoped is {Mode:"auto",AuditMatched:true} &&
            editStore.ReadPolicy(HostTaskJournal.RepositoryId(editFixture.MainRoot),editFixture.MainRoot) is {Mode:"manual",AuditMatched:true},
            "One repository's policy change must preserve the existing audited repository policy");
        var disabled=editStore.SetRepositoryExportPolicy(new(newRepo,fixture.MainRoot,"deny",[],[]),"proof_operator");
        Check(disabled is {Mode:"deny",AuditMatched:true},"Revocation must become the current audited policy");
        Console.WriteLine(JsonSerializer.Serialize(new {status="PASS",checks=37,actual_model_requests=0,task_id=fixture.TaskId,
            ticket_hash=fixture.TicketHash,manifest_hash=prepared.ManifestHash,events=events.Select(e=>new{e.EventId,e.Input.EventType})}));
    }
    internal static async Task CrashAsync(string root,string boundary) {
        var fixture=Read(root);using var store=new CanonicalStore(fixture.HostRoot);using var journal=new HostTaskJournal(store,fixture.MainRoot,fixture.TaskId,Request,fixture.Head);
        var rescue=new HostRescue(journal,point=>{if(point==boundary)Environment.FailFast("intentional rescue crash "+boundary);});
        if(boundary.StartsWith("import_"))await rescue.ImportAsync(fixture.TicketHash,fixture.Candidate);
        else await rescue.PromoteAsync(fixture.TicketHash,fixture.ValidationHash!);
        throw new InvalidOperationException("Rescue crash boundary not reached");
    }
    private static async Task TerminateAtAsync(string root,string boundary) {
        var start=new ProcessStartInfo("dotnet") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{typeof(HostRescueTests).Assembly.Location,"--rescue-crash",root,boundary})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));await process.WaitForExitAsync(timeout.Token);_ = await stdout;_ = await stderr;Check(process.ExitCode!=0,"Crash fixture unexpectedly passed");
    }
    private sealed class IncrementalInput:MemoryStream {
        public int ReadBytes;public bool WasDisposed;
        public IncrementalInput():base(new byte[3*1024*1024]){}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){var count=await base.ReadAsync(buffer[..Math.Min(buffer.Length,4096)],token);ReadBytes+=count;return count;}
        protected override void Dispose(bool disposing){WasDisposed=true;base.Dispose(disposing);}
    }
    private sealed class StalledInput:MemoryStream {
        private readonly TaskCompletionSource<int> pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasDisposed;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)=>new(pending.Task);
        protected override void Dispose(bool disposing){WasDisposed=true;pending.TrySetResult(0);base.Dispose(disposing);}
    }
    internal static async Task RunAsync() {
        var stalled=new StalledInput();var timedOut=false;
        try{await RescueInput.ReadAsync(stalled,deadline:TimeSpan.FromMilliseconds(100));}catch(OperationCanceledException){timedOut=true;}
        Check(timedOut && stalled.WasDisposed,"Host deadline did not stop/dispose a cancellation-ignoring stream");
        var oversized=new IncrementalInput();await Reject(()=>RescueInput.ReadAsync(oversized),"Oversized incremental input accepted");
        Check(oversized.ReadBytes==2*1024*1024+1 && oversized.WasDisposed,"Untrusted stream was not stopped/disposed at the byte boundary");
        var parent=Path.Combine(Path.GetTempPath(),"localbrain-rescue-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(parent);
        foreach(var boundary in new[]{"import_before_1","import_after_1","import_after_2"}) {
            var root=Path.Combine(parent,boundary);var fixture=await PrepareAsync(root);await TerminateAtAsync(root,boundary);
            using var store=new CanonicalStore(fixture.HostRoot);using var journal=new HostTaskJournal(store,fixture.MainRoot,fixture.TaskId,Request,fixture.Head);var rescue=new HostRescue(journal);
            var hash=await rescue.ImportAsync(fixture.TicketHash,fixture.Candidate);var ticket=rescue.Load(fixture.TicketHash);
            Check(File.ReadAllText(Path.Combine(ticket.RescueRoot,"greeting.txt"))=="HELLO\n" && File.ReadAllText(Path.Combine(ticket.RescueRoot,"other.txt"))=="SECOND\n" && File.ReadAllText(Path.Combine(ticket.RescueRoot,"new.txt"))=="THIRD\n","Interrupted import did not recover");
            Check(File.ReadAllText(Path.Combine(fixture.MainRoot,"greeting.txt"))=="BROKEN\n","Candidate touched main before review");
            await Reject(()=>rescue.ImportAsync(fixture.TicketHash,fixture.Candidate with{Generation=fixture.Candidate.Generation+1}),"Stale generation accepted");
            await Reject(()=>rescue.ImportAsync(fixture.TicketHash,fixture.Candidate with{Changes=[new("../escape.txt",null,"bad")]}),"Out-of-scope result accepted");
            File.WriteAllText(Path.Combine(ticket.RescueRoot,"greeting.txt"),"ALTERED\n");await Reject(()=>rescue.ValidateAsync(fixture.TicketHash,hash),"Altered post-import source was validated");
            Console.WriteLine("Rescue Host termination "+boundary+": starting parity, same candidate resume, main isolation and stale/scope/drift rejection PASS");
        }
    }
    internal static async Task LiveAsync(string root) {
        var personal = LocalBrain.ClientHost.ClientConfig.Load().PersonalValidation;
        var fixture=await PrepareAsync(root);
        using(var store=new CanonicalStore(fixture.HostRoot))using(var journal=new HostTaskJournal(store,fixture.MainRoot,fixture.TaskId,Request,fixture.Head)) {
            if(personal)journal.EnablePersonalValidation();
            var rescue=new HostRescue(journal);var candidateHash=await rescue.ImportAsync(fixture.TicketHash,fixture.Candidate);var validationHash=await rescue.ValidateAsync(fixture.TicketHash,candidateHash);
            fixture=fixture with{CandidateHash=candidateHash,ValidationHash=validationHash};Save(root,fixture);
        }
        await TerminateAtAsync(root,"promotion_after_1");
        using(var store=new CanonicalStore(fixture.HostRoot))using(var journal=new HostTaskJournal(store,fixture.MainRoot,fixture.TaskId,Request,fixture.Head)) {
            if(personal)journal.EnablePersonalValidation();
            var rescue=new HostRescue(journal);await rescue.PromoteAsync(fixture.TicketHash,fixture.ValidationHash!);var ticket=rescue.Load(fixture.TicketHash);
            Check(File.ReadAllText(Path.Combine(fixture.MainRoot,"greeting.txt"))=="HELLO\n" && File.ReadAllText(Path.Combine(fixture.MainRoot,"other.txt"))=="SECOND\n" && File.ReadAllText(Path.Combine(fixture.MainRoot,"new.txt"))=="THIRD\n","Promotion recovery did not finish");
            Check(File.ReadAllText(Path.Combine(ticket.RescueRoot,"greeting.txt"))=="HELLO\n","Parity evidence destroyed");
            var mainWsl=await MapMainAsync(fixture.MainRoot);foreach(var test in Request.RequiredTests)Check((await ToolRouter.RunAsync(fixture.MainRoot,mainWsl,test)).Passed,"Promoted main test failed");
            var validation=JsonSerializer.Deserialize<HostRescue.Validation>(store.ReadEvidence(fixture.ValidationHash!))!;
            Console.WriteLine(JsonSerializer.Serialize(new{status="PASS_REAL_HOST_GIT_WSL_TESTS_FRESH_QWEN_CRITIC_AND_PROMOTION_RECOVERY",fixture.TaskId,fixture.TicketHash,fixture.CandidateHash,fixture.ValidationHash,validation.CriticVerdict,required_tests=validation.Tests.Length,promotion_crash_boundary="promotion_after_1",main_exact_bytes_passed=true,main_head_unchanged=Encoding.UTF8.GetString(await AttemptStartingState.GitAsync(fixture.MainRoot,["rev-parse","HEAD"])).Trim()==fixture.Head,rescue_evidence_retained=true,candidate_source="synthetic untrusted fixture, not actual external Codex response",external_cloud_dispatch=false}));
        }
    }
    private static async Task<string> MapMainAsync(string root) {
        var config=LocalBrain.ClientHost.ClientConfig.Load();var path=Path.Combine(Path.GetDirectoryName(root)!,"fixture-projects.json");File.WriteAllText(path,JsonSerializer.Serialize(new[]{root}));
        return await WslWorkspace.ResolveAsync(new LocalBrain.ClientHost.ProjectAllowlist(config with{ProjectsFile=path}),root);
    }
}
