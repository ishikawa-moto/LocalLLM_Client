using System.Diagnostics;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.AgentV2;

internal static class TaskCommandGateTests
{
    static void Check(bool v,string why){if(!v)throw new Exception(why);}
    static JsonElement Value(object v)=>JsonSerializer.SerializeToElement(v,ClientConfig.JsonOptions);
    internal static AgentTaskRequest Request(string[] outputs)=>new("Execute the public fixture and verify its real output",["Public expected file exists and command receipt is successful"],"LOW",false,["public.txt"],[new("command_exit","public_success")]){
        CommandExecution=new(["windows","wsl"],["."],outputs,5,5,12,32,8192)};
    internal static TaskCommandAuthorization.Grant FixtureGrant(string directory,string root,string taskId,AgentTaskRequest request,string head)
    {
        var pending=TaskCommandAuthorization.Prepare(directory,root,taskId,request,head);
        File.WriteAllBytes(pending.Output,JsonSerializer.SerializeToUtf8Bytes(new{source="windows_native_task_command_grant",confirmed=true,nonce=pending.Nonce,input_hash=pending.InputHash,task_id=taskId,request_hash=pending.RequestHash,fixture_only=true}));
        return TaskCommandAuthorization.Read(pending)!;
    }
    internal static async Task ChildAsync(string mode,string path)
    {
        if(mode=="success"){File.WriteAllText(path,"PUBLIC_CSV_731\n");Console.WriteLine("公開fixtureの出力");return;}
        if(mode=="error"){Console.Error.WriteLine("PUBLIC_ERROR");Environment.ExitCode=7;return;}
        if(mode=="spam"){Console.WriteLine(new string('x',40000));await Task.Delay(3000);return;}
        if(mode=="wait"){File.WriteAllText(path,Environment.ProcessId.ToString());await Task.Delay(60000);return;}
        throw new Exception("Unknown public child fixture");
    }
    static TaskCommandRequest Windows(string id,string mode,string path,int? timeout=null)=>new(id,"windows",Environment.ProcessPath!.EndsWith("dotnet.exe",StringComparison.OrdinalIgnoreCase)?Environment.ProcessPath!:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"dotnet","dotnet.exe"),
        [typeof(TaskCommandGateTests).Assembly.Location,"--task-command-child",mode,path],ExpectedOutputs:mode=="success"?[path]:[],TimeoutSeconds:timeout);
    static async Task<JsonElement> Done(TaskCommandGate gate,string id)
    {
        for(var i=0;i<160;i++){var state=Value(await gate.StatusAsync(id,false,CancellationToken.None));if(state.GetProperty("state").GetString()!="running")return state;await Task.Delay(100);}
        throw new Exception("Owned public command did not settle within its finite fixture budget");
    }
    internal static async Task RunAsync(bool wsl=false)
    {
        var root=Path.Combine(Path.GetTempPath(),"task-command-proof-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var repo=Path.Combine(root,"repo");var output=Path.Combine(root,"output");Directory.CreateDirectory(repo);Directory.CreateDirectory(output);
        var request=Request([output]);var taskId=Guid.NewGuid().ToString("N");var grant=FixtureGrant(Path.Combine(root,"grant"),repo,taskId,request,"public_head");
        using var scope=TaskCommandAuthorization.Enter(grant);using var store=new CanonicalStore(Path.Combine(root,"control"));
        using var journal=new HostTaskJournal(store,repo,taskId,request,"public_head");var gate=journal.Commands!;
        var file=Path.Combine(output,"result.csv");var cmd=Windows("public_success","success",file);
        var first=Value(await gate.StartAsync(cmd,CancellationToken.None));Check(first.GetProperty("state").GetString()=="running","Command start returns a running handle, not success");
        var result=await Done(gate,cmd.OperationId);Check(result.GetProperty("exit_code").GetInt32()==0&&result.GetProperty("host_command_progress").GetBoolean()&&File.ReadAllText(file)=="PUBLIC_CSV_731\n","Real Windows owned output and receipt required");
        var hash=result.GetProperty("receipt_hash").GetString()!;var repeated=Value(await gate.StartAsync(cmd,CancellationToken.None));Check(repeated.GetProperty("receipt_hash").GetString()==hash,"Identical operation ID reuses exact terminal receipt without a second process");
        Check(gate.Test(cmd.OperationId).Passed&&gate.VerifyToolProgress(cmd.OperationId,hash),"Command validation is bound to Host receipt and actual expected outputs");
        using(var resumedJournal=new HostTaskJournal(store,repo,taskId,request,"public_head")){
            var resumed=Value(await resumedJournal.Commands!.StatusAsync(cmd.OperationId,false,CancellationToken.None));
            Check(resumed.GetProperty("receipt_hash").GetString()==hash,"A fresh invocation imports the same command receipt idempotently without replay or canonical conflict");
        }
        var refused=false;try{await gate.StartAsync(cmd with{Arguments=["different"]},CancellationToken.None);}catch(UnauthorizedAccessException){refused=true;}Check(refused,"Same operation ID cannot change its command");
        refused=false;try{await gate.StartAsync(Windows("outside","success",Path.Combine(root,"outside.txt")),CancellationToken.None);}catch(UnauthorizedAccessException){refused=true;}Check(refused,"Undeclared expected output outside the task roots is rejected before launch");
        refused=false;try{await gate.StartAsync(cmd with{OperationId="badcwd",WorkingDirectory=".."},CancellationToken.None);}catch(InvalidDataException){refused=true;}Check(refused,"Unapproved cwd is rejected");
        await gate.StartAsync(Windows("public_error","error",file),CancellationToken.None);var error=await Done(gate,"public_error");Check(error.GetProperty("exit_code").GetInt32()==7&&!gate.Test("public_error").Passed,"Nonzero real exit cannot pass validation");
        await gate.StartAsync(Windows("public_timeout","wait",Path.Combine(output,"timeout.pid"),1),CancellationToken.None);var timeout=await Done(gate,"public_timeout");Check(timeout.GetProperty("state").GetString()=="timeout","Owned process has a hard finite timeout");
        var timedPid=int.Parse(File.ReadAllText(Path.Combine(output,"timeout.pid")));Check(Exited(timedPid),"Timed-out owned child is actually stopped");
        await gate.StartAsync(Windows("public_cancel","wait",Path.Combine(output,"cancel.pid")),CancellationToken.None);await Task.Delay(400);await gate.StatusAsync("public_cancel",true,CancellationToken.None);var cancelled=await Done(gate,"public_cancel");Check(cancelled.GetProperty("state").GetString()=="cancelled","Cancellation is owned and recorded");
        await gate.StartAsync(Windows("public_spam","spam",file),CancellationToken.None);var spam=await Done(gate,"public_spam");Check(spam.GetProperty("state").GetString()=="output_limit","Output cap stops owned command instead of filling disk");
        var unknown=Path.Combine(gate.Root,"public_unknown");Directory.CreateDirectory(unknown);var originalManifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(gate.Root,"public_success","manifest.json"))).RootElement.Clone();
        File.WriteAllText(Path.Combine(unknown,"manifest.json"),originalManifest.GetRawText());
        var uncertain=Value(await gate.StatusAsync("public_unknown",false,CancellationToken.None));Check(uncertain.GetProperty("state").GetString()=="outcome_unknown","Missing terminal receipt is unknown, never success or automatic replay");
        var replayBlocked=Value(await gate.StartAsync(cmd with{OperationId="new_id_unknown_bypass"},CancellationToken.None));Check(replayBlocked.GetProperty("state").GetString()=="outcome_unknown","Changing operation ID cannot bypass an unknown identical intent");
        File.WriteAllText(file,"changed after completion");Check(!gate.Test("public_success").Passed,"Expected-output drift invalidates success without replacing original receipt");
        if(wsl){
            var wslOutput=Path.Combine(output,"wsl.txt");var linuxOutput=await WslWorkspace.MapPathAsync(wslOutput,CancellationToken.None);
            await gate.StartAsync(new("public_wsl","wsl","/usr/bin/python3",["-c","import pathlib; pathlib.Path(__import__('sys').argv[1]).write_text('WSL_PUBLIC_731'); print('PUBLIC_WSL_OK')",linuxOutput],ExpectedOutputs:[wslOutput]),CancellationToken.None);
            var linux=await Done(gate,"public_wsl");Check(linux.GetProperty("state").GetString()=="completed"&&linux.GetProperty("exit_code").GetInt32()==0&&File.ReadAllText(wslOutput)=="WSL_PUBLIC_731","Real WSL owned supervisor and expected output must pass");
        }
        var changedGrant=File.ReadAllBytes(grant.Pending.Output);File.WriteAllText(grant.Pending.Output,"{}");refused=false;try{await gate.StartAsync(Windows("after_revoke","error",file),CancellationToken.None);}catch(Exception e) when(e is UnauthorizedAccessException or KeyNotFoundException){refused=true;}Check(refused,"Changed task grant cannot authorize a later command");File.WriteAllBytes(grant.Pending.Output,changedGrant);
        Console.WriteLine("Task command gate PASS: real owned Windows"+(wsl?"/WSL":"")+" processes, output hashes, one-grant reuse, immutable operations, no-replay unknown outcome, scope admission, nonzero exit, timeout, cancellation, output budget and output/grant drift. Synthetic grant fixture only; no production or real native-user approval.");
    }
    static bool Exited(int pid){try{using var p=Process.GetProcessById(pid);return p.HasExited;}catch(ArgumentException){return true;}}
}
