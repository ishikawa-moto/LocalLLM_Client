using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class SideEffectGateTests
{
    private static AgentTaskRequest Request()=>AgentTaskRequest.Parse("""
        {"requirement":"Create greeting.txt","acceptance_criteria":["Scoped file only"],"risk":"LOW","approved_high_risk":false,"allowed_files":["greeting.txt"],"required_tests":[{"kind":"git_diff_check"}]}
        """);
    private static string Snapshot(int n)=>JsonSerializer.Serialize(new {context_limit=32768,estimated_prompt_tokens=n,
        tokens=new {system_prompt=0,tool_schema=0,tool_result=0,conversation=n,retrieval=0,framing=0},tool_count_exposed=0});
    public static async Task CrashFixtureAsync(string parent,string boundary)
    {
        var root=Path.Combine(parent,"repo");var request=Request();
        var git=await GitEvidence.CaptureAsync(root,request.AllowedFiles,default);
        using var store=new CanonicalStore(Path.Combine(parent,"control"));
        var task=Guid.NewGuid().ToString("N");var session=Guid.NewGuid().ToString();
        using var journal=new HostTaskJournal(store,root,task,request,git.Head);
        journal.Checkpoint(new {task_id=task,phase="running",pi_session_id=session,actor_turns=1,
            git_head=git.Head,request_hash=AgentTaskRunner.RequestHash(request),changed_files=git.ChangedFiles,
            review_diff_sha256=CanonicalStore.Hash(Encoding.UTF8.GetBytes(git.ReviewDiff)),false_verified=true,replans=2});
        if(boundary=="started")Environment.FailFast("Fixture: abrupt Host termination after start");
        await using var gate=new SideEffectGate(root,request.AllowedFiles,git.Head,true,journal,
            fault:point=>{if(boundary=="mutation" && point=="mutation_applied_before_checkpoint" ||
                boundary=="staging" && point=="mutation_staging_flushed")Environment.FailFast("Fixture: abrupt Host termination at mutation boundary");});
        if(boundary=="frozen") {
            await gate.HandleAsync(new(gate.PipeName,gate.Nonce,"preflight",PayloadTokens:30000,
                PayloadJson:"{\"max_completion_tokens\":2048}",SnapshotJson:Snapshot(30000)));
            Environment.FailFast("Fixture: abrupt Host termination after durable freeze");
        }
        await gate.HandleAsync(new(gate.PipeName,gate.Nonce,"write","greeting.txt","HELLO\n"));
    }
    private static void Check(bool condition,string name) {if(!condition)throw new InvalidOperationException(name);}
    private static async Task Reject(Func<Task> action,string name) {
        try {await action();}catch(Exception e) when(e is UnauthorizedAccessException or InvalidDataException or InvalidOperationException){return;}
        throw new InvalidOperationException(name);
    }
    private static async Task Git(string root,params string[] args) {
        var start=new ProcessStartInfo("git") { WorkingDirectory=root,RedirectStandardOutput=true,RedirectStandardError=true };
        foreach(var arg in args)start.ArgumentList.Add(arg);
        using var p=Process.Start(start)!;
        var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();await stdout;await stderr;
        Check(p.ExitCode==0,"Git fixture failed");
    }
    public static async Task RunAsync()
    {
        var parent=Path.Combine(Path.GetTempPath(),"lb-sidefx-"+Guid.NewGuid().ToString("N"));
        var root=Path.Combine(parent,"repo");Directory.CreateDirectory(root);
        try {
            await File.WriteAllTextAsync(Path.Combine(root,".gitignore"),".localbrain/\n");
            await Git(root,"init");await Git(root,"add",".gitignore");
            await Git(root,"-c","user.name=Test","-c","user.email=test@localhost","commit","-m","fixture");
            var request=AgentTaskRequest.Parse("""
                {"requirement":"Create greeting.txt","acceptance_criteria":["Scoped file only"],"risk":"LOW","approved_high_risk":false,"allowed_files":["greeting.txt"],"required_tests":[{"kind":"git_diff_check"}]}
                """);
            var initial=await GitEvidence.CaptureAsync(root,request.AllowedFiles,default);
            using var store=new CanonicalStore(Path.Combine(parent,"control"));
            var taskId=Guid.NewGuid().ToString("N");
            using(var journal=new HostTaskJournal(store,root,taskId,request,initial.Head)) {
                var oldSession=Guid.NewGuid().ToString();var nextSession=Guid.NewGuid().ToString();
                journal.Checkpoint(new {task_id=taskId,phase="running",pi_session_id=oldSession,actor_turns=2,tool_calls=3,
                    git_head=initial.Head,request_hash=AgentTaskRunner.RequestHash(request),changed_files=Array.Empty<string>(),
                    review_diff_sha256=CanonicalStore.Hash(Encoding.UTF8.GetBytes(initial.ReviewDiff)),false_verified=true,replans=1});
                await using(var gate=new SideEffectGate(root,request.AllowedFiles,initial.Head,true,journal)) {
                    SideEffectGate.Request R(string op,string? path=null,string? content=null)=>new(gate.PipeName,gate.Nonce,op,path,content);
                    await Reject(()=>gate.HandleAsync(R("write","greeting.txt","HELLO\n") with {Nonce="bad"}),"Wrong capability allowed");
                    foreach(var file in new[]{"../greeting.txt","greeting.txt ","greeting.txt:secret",".git/config",".localbrain/state","other.txt"})
                        await Reject(()=>gate.HandleAsync(R("write",file,"bad")),"Escaped write_scope");
                    var written=await gate.HandleAsync(R("write","greeting.txt","HELLO\n"));
                    Check(written.Ok && File.ReadAllText(Path.Combine(root,"greeting.txt"))=="HELLO\n","Host scoped write failed");
                    await Reject(()=>gate.HandleAsync(R("edit","greeting.txt") with {OldText="missing",NewText="x"}),"Ambiguous edit allowed");
                    await gate.HandleAsync(R("edit","greeting.txt") with {OldText="HELLO",NewText="WORLD"});
                    Check(File.ReadAllText(Path.Combine(root,"greeting.txt"))=="WORLD\n","Host exact edit failed");
                    await Reject(()=>gate.HandleAsync(R("preflight") with {PayloadTokens=100,PayloadJson="{}"}),"Missing output reserve accepted");
                    var accepted=await gate.HandleAsync(R("preflight") with {PayloadTokens=100,PayloadJson="{\"max_tokens\":2048}",SnapshotJson=Snapshot(100)});
                    Check(accepted.Ok,"Small provider payload rejected");
                    Check(ContextTelemetryState.Read(root,taskId)?.PromptTokens==100,"Host context snapshot not current");
                    using(var disconnected=new System.IO.Pipes.NamedPipeClientStream(".",gate.PipeName,System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous)) {
                        await disconnected.ConnectAsync(5000);
                        await disconnected.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(R("unknown"))+"\n"));
                        await disconnected.FlushAsync();
                    }
                    var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"localbrain.exe")) {
                        RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true };
                    start.ArgumentList.Add("host-action");
                    using(var relay=Process.Start(start)!) {
                        await relay.StandardInput.WriteAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(R("write","greeting.txt","日本語\nHELLO\n")))));
                        relay.StandardInput.Close();
                        var output=relay.StandardOutput.ReadToEndAsync();var errors=relay.StandardError.ReadToEndAsync();
                        await relay.WaitForExitAsync();await errors;
                        var reply=JsonSerializer.Deserialize<SideEffectGate.Reply>(Encoding.UTF8.GetString(Convert.FromBase64String(await output)),LocalBrain.ClientHost.ClientConfig.JsonOptions)!;
                        Check(relay.ExitCode==0 && reply.Ok && File.ReadAllText(Path.Combine(root,"greeting.txt"))=="日本語\nHELLO\n",
                            "Disconnected pipe or Unicode/newline relay corrupted content");
                    }
                    var denied=await gate.HandleAsync(R("preflight") with {PayloadTokens=30000,PayloadJson="{\"max_tokens\":2048}",SnapshotJson=Snapshot(30000)});
                    Check(!denied.Ok && denied.Handoff && gate.HandoffRequested,"Context pressure did not freeze session");
                    await Reject(()=>gate.HandleAsync(R("write","greeting.txt","bad")),"Frozen session mutated");
                    await Reject(()=>gate.HandleAsync(R("preflight") with {PayloadTokens=100,PayloadJson="{\"max_tokens\":2048}"}),"Frozen session resumed provider");
                }
                var git=await GitEvidence.CaptureAsync(root,request.AllowedFiles,default);
                journal.RotateSession(oldSession,nextSession,git);
                var rotated=store.GetTask(taskId)!;
                Check(rotated.Handoffs==1 && rotated.ActorTurns==2 && rotated.ToolCalls==3 &&
                    rotated.StateJson.Contains(nextSession) && rotated.StateJson.Contains("true"),"Handoff reset task state");
                journal.Checkpoint(new {phase="failed",actor_turns=2});
                Check(store.GetTask(taskId)!.StateJson.Contains("\"false_verified\":true") &&
                    store.GetTask(taskId)!.StateJson.Contains("\"replans\":1"),"Failure reset completion flags");
                var evidence=journal.EvidenceRefs().Last();
                await using var fresh=new SideEffectGate(root,request.AllowedFiles,initial.Head,true,journal);
                var recall=await fresh.HandleAsync(new(fresh.PipeName,fresh.Nonce,"recall",EvidenceHash:evidence));
                Check(recall.Ok && !string.IsNullOrEmpty(recall.Text),"New session cannot recall canonical evidence");
                var unrelated=store.PutEvidence(Encoding.UTF8.GetBytes("unrelated"));
                await Reject(()=>fresh.HandleAsync(new(fresh.PipeName,fresh.Nonce,"recall",EvidenceHash:unrelated)),"Unrelated evidence disclosed");
                await fresh.HandleAsync(new(fresh.PipeName,fresh.Nonce,"write","greeting.txt","HELLO\n"));
                var approvalId=CanonicalStore.Hash(Encoding.UTF8.GetBytes("interactive proof"));
                store.BindApproval(approvalId,taskId,AgentTaskRunner.RequestHash(request),initial.Head,DateTimeOffset.UtcNow.AddMinutes(1));
                Check(store.HasApproval(taskId,AgentTaskRunner.RequestHash(request),initial.Head),"Approval not bound");
                Check(!store.HasApproval(taskId,AgentTaskRunner.RequestHash(request),"changed"),"Approval accepted different HEAD");
            }
            Check(!SideEffectGate.EvaluateBudget(1000).Handoff && SideEffectGate.EvaluateBudget(20000).Handoff,"Budget reserves incorrect");
            foreach(var boundary in new[]{"started","frozen","staging","mutation"}) {
                var crashParent=Path.Combine(parent,"crash-"+boundary);var crashRoot=Path.Combine(crashParent,"repo");Directory.CreateDirectory(crashRoot);
                await File.WriteAllTextAsync(Path.Combine(crashRoot,".gitignore"),".localbrain/\n");
                await Git(crashRoot,"init");await Git(crashRoot,"add",".gitignore");
                await Git(crashRoot,"-c","user.name=Test","-c","user.email=test@localhost","commit","-m","fixture");
                var start=new ProcessStartInfo("dotnet") {RedirectStandardOutput=true,RedirectStandardError=true};
                start.ArgumentList.Add(typeof(SideEffectGateTests).Assembly.Location);start.ArgumentList.Add("--sidefx-crash");
                start.ArgumentList.Add(crashParent);start.ArgumentList.Add(boundary);
                using var process=Process.Start(start)!;var o=process.StandardOutput.ReadToEndAsync();var e=process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();await o;await e;Check(process.ExitCode!=0,"Crash fixture did not terminate");
                using var reopened=new CanonicalStore(Path.Combine(crashParent,"control"));
                var state=reopened.LatestTask(HostTaskJournal.WorkspaceId(crashRoot))!;
                var actual=await GitEvidence.CaptureAsync(crashRoot,Request().AllowedFiles,default);
                var resume=typeof(AgentTaskRunner).GetMethod("ReadResumeCheckpoint",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
                Check(resume.Invoke(null,[crashRoot,AgentTaskRunner.RequestHash(Request()),actual,false,reopened]) is not null,
                    "Abrupt termination checkpoint cannot resume");
                Check(state.ActorTurns==1 && state.StateJson.Contains("\"false_verified\":true") && state.StateJson.Contains("\"replans\":2"),"Crash reset task counters or gates");
                Console.WriteLine("Host process termination at "+boundary+": same-task recovery PASS");
            }
            Console.WriteLine("Phase 2 Host mutation, context freeze, immutable recall, approval binding and task continuity checks passed.");
        } finally { if(Directory.Exists(parent) && CanonicalStore.Within(Path.GetTempPath(),parent) &&
            Path.GetFileName(parent).StartsWith("lb-sidefx-",StringComparison.Ordinal)) {
            foreach(var file in Directory.EnumerateFiles(parent,"*",SearchOption.AllDirectories)) {
                try {File.SetAttributes(file,FileAttributes.Normal);}
                catch(FileNotFoundException) { /* SQLite may remove a transient file after enumeration. */ }
            }
            Directory.Delete(parent,true);} }
    }
}
