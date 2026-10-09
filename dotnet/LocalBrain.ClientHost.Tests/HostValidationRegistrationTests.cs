using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class HostValidationRegistrationTests
{
    internal static async Task RunAsync(string? destination=null)
    {
        var parent=Path.GetFullPath(destination??Path.Combine(Path.GetTempPath(),"lb-publisher-register-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(parent);var root=Path.Combine(parent,"repo");Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root,".gitignore"),".localbrain/\n");
        await AttemptStartingState.GitAsync(root,["init","-q"]);await AttemptStartingState.GitAsync(root,["add",".gitignore"]);
        await AttemptStartingState.GitAsync(root,["-c","user.name=Fixture","-c","user.email=fixture@localhost","commit","-qm","fixture"]);
        using var store=new CanonicalStore(Path.Combine(parent,"host"));
        var publisher=Path.Combine(store.Root,"validation-inputs","publisher");var sdk=Path.Combine(publisher,"sdk");
        Directory.CreateDirectory(Path.Combine(sdk,"sdk","10.0.303"));Directory.CreateDirectory(Path.Combine(publisher,"packages"));Directory.CreateDirectory(Path.Combine(publisher,"feed"));
        // Registration metadata fixture only; these bytes are never executed as an SDK.
        await File.WriteAllTextAsync(Path.Combine(sdk,"dotnet.exe"),"synthetic input, not an executable");
        await File.WriteAllTextAsync(Path.Combine(sdk,"sdk","10.0.303","fixture.txt"),"synthetic SDK registration input");
        var files=Directory.GetFiles(sdk,"*",SearchOption.AllDirectories).Select(p=>new HostValidationIsolation.InputFile(Path.GetRelativePath(sdk,p).Replace('\\','/'),new FileInfo(p).Length,CanonicalStore.Hash(File.ReadAllBytes(p)))).ToArray();
        var policy=new HostValidationPolicy.Policy(HostTaskJournal.RepositoryId(root),"10.0.303",new(sdk,files),new(Path.Combine(publisher,"packages"),[]),new(Path.Combine(publisher,"feed"),[]));
        var checks=new List<string>();
        void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);}
        async Task Reject(Func<Task> call,string name){try{await call();}catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidOperationException){checks.Add(name);return;}throw new Exception(name);}
        var first=await HostValidationRegistration.RegisterAsync(store,root,policy);
        Check(first.CreatedRegistrationTask&&store.GetTask(first.TaskId)!.ActorTurns==0,"new workspace receives metadata registration task only");
        var initialState=store.GetTask(first.TaskId)!.StateJson;
        var again=await HostValidationRegistration.RegisterAsync(store,root,policy);
        Check(!again.CreatedRegistrationTask&&again.TaskId==first.TaskId&&store.GetTask(first.TaskId)!.StateJson==initialState,"repeat registration preserves latest task and state");
        var request=AgentTaskRequest.Parse("""{"requirement":"Fix greeting","acceptance_criteria":["Scoped output"],"risk":"LOW","approved_high_risk":false,"allowed_files":["greeting.txt"],"required_tests":[{"kind":"git_diff_check"}]}""");
        var head=Encoding.UTF8.GetString(await AttemptStartingState.GitAsync(root,["rev-parse","HEAD"])).Trim();var task=Guid.NewGuid().ToString("N");
        using(var journal=new HostTaskJournal(store,root,task,request,head)){
            journal.Checkpoint(new{task_id=task,phase="failed",git_head=head,pi_session_id=Guid.NewGuid().ToString(),request_hash=AgentTaskRunner.RequestHash(request),actor_turns=2,tool_calls=3,false_verified=true,replans=1});
            journal.BindApproval(CanonicalStore.Hash(Encoding.UTF8.GetBytes("fixture approval")),DateTimeOffset.UtcNow.AddMinutes(2));
        }
        var before=store.GetTask(task)!;var registered=await HostValidationRegistration.RegisterAsync(store,root,policy);var after=store.GetTask(task)!;
        Check(registered.TaskId==task&&!registered.CreatedRegistrationTask&&after.StateJson==before.StateJson&&after.ActorTurns==2&&after.ToolCalls==3&&after.RequestHash==before.RequestHash,"existing failed Actor identity session flags and counters preserved");
        Check(store.HasApproval(task,AgentTaskRunner.RequestHash(request),head),"existing approval retained");
        using(var busy=new FileStream(Path.Combine(root,".localbrain","active.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            await Reject(async()=>{await HostValidationRegistration.RegisterAsync(store,root,policy);},"active workspace registration refused");
        await Reject(async()=>{await HostValidationRegistration.RegisterAsync(store,root,policy with{RepositoryId="another_repository"});},"wrong workspace policy refused");
        Check(store.LatestTask(HostTaskJournal.WorkspaceId(root))!.TaskId==task,"negative registration cannot replace latest task");
        Check(File.ReadAllText(Path.Combine(root,".gitignore"))==".localbrain/\n"&&!File.Exists(Path.Combine(root,"global.json"))&&!File.Exists(Path.Combine(root,"greeting.txt")),"registration does not mutate repository files");
        var legacyRoot=Path.Combine(parent,"legacy-repo");Directory.CreateDirectory(legacyRoot);
        await File.WriteAllTextAsync(Path.Combine(legacyRoot,".gitignore"),".localbrain/\n");await File.WriteAllTextAsync(Path.Combine(legacyRoot,"greeting.txt"),"START\n");
        await AttemptStartingState.GitAsync(legacyRoot,["init","-q"]);await AttemptStartingState.GitAsync(legacyRoot,["add",".gitignore","greeting.txt"]);
        await AttemptStartingState.GitAsync(legacyRoot,["-c","user.name=Fixture","-c","user.email=fixture@localhost","commit","-qm","legacy baseline"]);
        await File.WriteAllTextAsync(Path.Combine(legacyRoot,"greeting.txt"),"HELLO\n");var legacyGit=await GitEvidence.CaptureAsync(legacyRoot,request.AllowedFiles,default);
        var legacyTask=Guid.NewGuid().ToString("N");Directory.CreateDirectory(Path.Combine(legacyRoot,".localbrain"));
        var legacyPath=Path.Combine(legacyRoot,".localbrain","task-state.json");
        await File.WriteAllTextAsync(legacyPath,JsonSerializer.Serialize(new{task_id=legacyTask,phase="failed",pi_session_id=Guid.NewGuid().ToString(),git_head=legacyGit.Head,request_hash=AgentTaskRunner.RequestHash(request),actor_turns=1,recovery_count=0,changed_files=legacyGit.ChangedFiles,review_diff_sha256=CanonicalStore.Hash(Encoding.UTF8.GetBytes(legacyGit.ReviewDiff))}));
        var legacyBytes=await File.ReadAllBytesAsync(legacyPath);
        var resume=typeof(AgentTaskRunner).GetMethod("ReadResumeCheckpoint",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
        var beforeResume=JsonSerializer.SerializeToElement(resume.Invoke(null,[legacyRoot,AgentTaskRunner.RequestHash(request),legacyGit,false,store]));
        Check(beforeResume.GetProperty("TaskId").GetString()==legacyTask,"valid existing repo-only checkpoint is resumable before registration");
        await Reject(async()=>{await HostValidationRegistration.RegisterAsync(store,legacyRoot,policy with{RepositoryId=HostTaskJournal.RepositoryId(legacyRoot)});},"registration cannot shadow legacy checkpoint");
        var afterResume=JsonSerializer.SerializeToElement(resume.Invoke(null,[legacyRoot,AgentTaskRunner.RequestHash(request),legacyGit,false,store]));
        Check(store.LatestTask(HostTaskJournal.WorkspaceId(legacyRoot)) is null&&afterResume.GetProperty("TaskId").GetString()==legacyTask&&(await File.ReadAllBytesAsync(legacyPath)).SequenceEqual(legacyBytes),"legacy owner bytes and ordinary resume remain unchanged");
        var proof=JsonSerializer.Serialize(new{status="PASS_HOST_ADMINISTRATIVE_PUBLISHER_REGISTRATION_TASK_CONTINUITY",checks,task_id=task,registration_hash=registered.PolicyHash,real_sdk_executed=false,production_changed=false});
        await File.WriteAllTextAsync(Path.Combine(parent,"proof.json"),proof);Console.WriteLine(proof);
    }
}
