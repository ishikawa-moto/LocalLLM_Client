using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class HostRescueTests
{
    internal sealed record Fixture(string MainRoot,string HostRoot,string TaskId,string Head,string TicketHash,HostRescue.Candidate Candidate,string? CandidateHash=null,string? ValidationHash=null);
    private static void Check(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
    private static async Task Reject(Func<Task> action,string reason){try{await action();}catch(Exception e)when(e is UnauthorizedAccessException or InvalidDataException or IOException){return;}throw new InvalidOperationException(reason);}
    internal static AgentTaskRequest Request=>AgentTaskRequest.Parse("""{"requirement":"Write HELLO plus LF in greeting.txt and SECOND plus LF in other.txt and THIRD plus LF in new.txt","acceptance_criteria":["greeting.txt contains exactly HELLO plus LF","other.txt contains exactly SECOND plus LF","new.txt contains exactly THIRD plus LF","all exact byte checks pass"],"risk":"LOW","approved_high_risk":false,"allowed_files":["greeting.txt","other.txt","new.txt"],"required_tests":[{"kind":"npm_test"},{"kind":"git_diff_check"}]}""");
    private static string Descriptor(string root)=>Path.Combine(root,"fixture.json");
    private static void Save(string root,Fixture fixture)=>File.WriteAllText(Descriptor(root),JsonSerializer.Serialize(fixture),new UTF8Encoding(false));
    private static Fixture Read(string root)=>JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Descriptor(root)))!;
    internal static async Task<Fixture> PrepareAsync(string root) {
        root=Path.GetFullPath(root);CanonicalStore.GuardPath(root);if(Directory.Exists(root))throw new IOException("Fixture root must be new");Directory.CreateDirectory(root);
        var main=Path.Combine(root,"repo");Directory.CreateDirectory(main);
        File.WriteAllText(Path.Combine(main,".gitignore"),".localbrain/\nnode_modules/\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(main,"greeting.txt"),"START\n",new UTF8Encoding(false));File.WriteAllText(Path.Combine(main,"other.txt"),"START\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(main,"package.json"),"""{"scripts":{"test":"node test.cjs"}}""",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(main,"test.cjs"),"""const fs=require('fs');const cp=require('child_process');for(const [file,value] of [['greeting.txt','HELLO\n'],['other.txt','SECOND\n'],['new.txt','THIRD\n']])if(!fs.readFileSync(file).equals(Buffer.from(value)))throw Error('Exact bytes '+file);if(cp.spawnSync('git',['rev-parse','--show-toplevel'],{encoding:'utf8'}).status!==0)throw Error('WSL Git identity failed');console.log('All three exact byte checks and WSL Git identity passed');""",new UTF8Encoding(false));
        await AttemptStartingState.GitAsync(main,["init","-q"]);await AttemptStartingState.GitAsync(main,["add","--",".gitignore","greeting.txt","other.txt","package.json","test.cjs"]);
        await AttemptStartingState.GitAsync(main,["-c","user.name=LocalBrain fixture","-c","user.email=fixture@example.invalid","commit","-qm","fixture baseline"]);
        var head=Encoding.UTF8.GetString(await AttemptStartingState.GitAsync(main,["rev-parse","HEAD"])).Trim();var hostRoot=Path.Combine(root,"host");var task="rescue_"+Guid.NewGuid().ToString("N");
        using var store=new CanonicalStore(hostRoot);using var journal=new HostTaskJournal(store,main,task,Request,head);
        store.ChangePolicies([new(HostTaskJournal.RepositoryId(main),main,"auto",Request.AllowedFiles,["task_evidence_only"])],"explicit_public_fixture_test");
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
