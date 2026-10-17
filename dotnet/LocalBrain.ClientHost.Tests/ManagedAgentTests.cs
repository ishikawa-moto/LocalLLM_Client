using System.Diagnostics;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.AgentV2;

internal static class ManagedAgentTests
{
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,ClientConfig.JsonOptions);
    internal static async Task NativePrepareAsync(string directory)
    {
        directory=Path.GetFullPath(directory);if(Directory.Exists(directory))throw new IOException("Existing public live fixture; observe it, do not prepare/launch again");
        Directory.CreateDirectory(directory);var repo=Path.Combine(directory,"repo");Directory.CreateDirectory(repo);var outputs=Path.Combine(directory,"outputs");Directory.CreateDirectory(outputs);
        async Task Git(params string[] args){var s=new ProcessStartInfo("git"){WorkingDirectory=repo,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var a in args)s.ArgumentList.Add(a);using var p=Process.Start(s)!;var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();await p.WaitForExitAsync();await Task.WhenAll(stdout,stderr);Check(p.ExitCode==0,"Public live Git fixture failed");}
        File.WriteAllText(Path.Combine(repo,"README.md"),"Public native one-task-grant and background Agent fixture only. No private knowledge.\n");File.WriteAllText(Path.Combine(repo,".gitignore"),".localbrain/\n");
        await Git("init");await Git("add",".");await Git("-c","user.name=Public Fixture","-c","user.email=fixture@example.invalid","commit","-m","Public fixture");
        var config=ClientConfig.Load() with{ProjectsFile=Path.Combine(directory,"projects.json"),AuditLog=Path.Combine(directory,"audit.jsonl")};File.WriteAllText(config.ProjectsFile,JsonSerializer.Serialize(new[]{repo}));
        var output=Path.Combine(outputs,"result.txt");var mapped=await WslWorkspace.MapPathAsync(output,CancellationToken.None);
        var request=new AgentTaskRequest("PUBLIC NATIVE UI TEST. Use host_command_start exactly once with operationId public_success, environment wsl, program /usr/bin/python3, arguments "+
            JsonSerializer.Serialize(new[]{"-c","import pathlib,sys; pathlib.Path(sys.argv[1]).write_text('PUBLIC_AGENT_731\\n'); print('PUBLIC_AGENT_OK')",mapped})+
            ", workingDirectory . and expectedOutputs "+JsonSerializer.Serialize(new[]{output})+". The command writes only a public test file outside the Git worktree in the declared output directory. Do not edit repository files. After start, return so the Host can wait, verify the command_exit test, and run a fresh reviewer. Do not claim completion or run the same command with another operation ID.",
            ["The Host-owned WSL command exits0 and the declared output result.txt contains PUBLIC_AGENT_731 followed by a newline; actual receipt and output hash agree."],"LOW",false,["README.md"],[new("command_exit","public_success"),new("git_diff_check",null)]){CommandExecution=new(["windows","wsl"],["."],[outputs],30,15,12,8,32768)};
        var control=Path.Combine(directory,"control");File.WriteAllText(Path.Combine(directory,"request.json"),AgentTaskRunner.RequestJson(request));
        var state=await TaskAgentCoordinator.SubmitAsync(repo,request,config,control:control);File.WriteAllText(Path.Combine(directory,"submission.json"),JsonSerializer.Serialize(state,ClientConfig.JsonOptions));Console.WriteLine(JsonSerializer.Serialize(state));
    }
    internal static async Task RunAsync()
    {
        var dir=Path.Combine(Path.GetTempPath(),"managed-agent-proof-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        var repo=Path.Combine(dir,"repo");Directory.CreateDirectory(repo);
        async Task Git(params string[] args){var start=new ProcessStartInfo("git"){WorkingDirectory=repo,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var a in args)start.ArgumentList.Add(a);using var p=Process.Start(start)!;var aout=p.StandardOutput.ReadToEndAsync();var err=p.StandardError.ReadToEndAsync();await p.WaitForExitAsync();await Task.WhenAll(aout,err);Check(p.ExitCode==0,"Public fixture Git command failed");}
        await Git("init");File.WriteAllText(Path.Combine(repo,"README.txt"),"PUBLIC_MANAGED_FIXTURE\n");await Git("add","README.txt");await Git("-c","user.name=Public Fixture","-c","user.email=fixture@example.invalid","commit","-m","Public fixture");
        var request=TaskCommandGateTests.Request([repo]) with{AllowedFiles=["**"]};
        var parsed=AgentTaskRequest.Parse(AgentTaskRunner.RequestJson(request));Check(parsed.CommandExecution?.MaxCommands==32,"Exact command grant contract survives canonical request hash serialization");
        var legacy=request with{CommandExecution=null,AllowedFiles=["README.txt"],RequiredTests=[new("git_diff_check",null)]};
        var expected=JsonSerializer.Serialize(new{requirement=legacy.Requirement,acceptance_criteria=legacy.AcceptanceCriteria,risk=legacy.Risk,approved_high_risk=legacy.ApprovedHighRisk,allowed_files=legacy.AllowedFiles,required_tests=new[]{new{kind="git_diff_check",target=(string?)null}}});
        Check(AgentTaskRunner.RequestJson(legacy)==expected,"Existing request hashes remain byte-for-byte compatible");
        var refused=false;try{AgentTaskRequest.Parse(AgentTaskRunner.RequestJson(legacy with{AllowedFiles=["**"]}));}catch(InvalidDataException){refused=true;}Check(refused,"Broad scope cannot silently change legacy file grants");
        Check(TaskWriteScope.Contains(["src/**"],"src/new/module.cs")&&!TaskWriteScope.Contains(["src/**"],"src-other/file.cs")&&!TaskWriteScope.Contains(["**"],".git/config"),"Subtree and protected metadata boundaries");
        var config=new ClientConfig("https://localhost.invalid",18081,"PUBLIC_UNUSED",Path.Combine(dir,"projects.json"),Path.Combine(dir,"audit.log"));File.WriteAllText(config.ProjectsFile,JsonSerializer.Serialize(new[]{repo}));var control=Path.Combine(dir,"control");
        var watch=Stopwatch.StartNew();var first=Json(await TaskAgentCoordinator.SubmitAsync(repo,request,config,control:control,launch:false));Check(watch.Elapsed<TimeSpan.FromSeconds(5)&&!first.GetProperty("eligible_for_completion").GetBoolean(),"Managed submission is bounded and queued is never completion");
        var task=first.GetProperty("task_id").GetString()!;var current=TaskAgentCoordinator.CurrentDirectory(repo,control)!;
        Check(!File.Exists(Path.Combine(current,"worker-launch-intent.json")),"Isolated queued proof launches no worker, native UI or model");
        var repeat=Json(await TaskAgentCoordinator.SubmitAsync(repo,request,config,control:control,launch:false));Check(repeat.GetProperty("task_id").GetString()==task,"Duplicate submission observes the same exact task without another grant/worker");
        refused=false;try{await TaskAgentCoordinator.SubmitAsync(repo,request with{Requirement="Different public work"},config,control:control,launch:false);}catch(InvalidOperationException){refused=true;}Check(refused,"A queued task retains workspace ownership");
        refused=false;try{await TaskAgentCoordinator.CancelAsync(repo,Guid.NewGuid().ToString("N"),control);}catch(UnauthorizedAccessException){refused=true;}Check(refused,"Cancel cannot target another task");
        await TaskAgentCoordinator.CancelAsync(repo,task,control);Check(File.Exists(Path.Combine(current,"cancel.json")),"Cancellation is a durable exact-task request");
        TaskCommandGate.WriteReplace(Path.Combine(current,"report.json"),new TaskAgentCoordinator.Report(task,"complete",true,1,null,DateTimeOffset.UtcNow));
        var fakeComplete=Json(await TaskAgentCoordinator.StatusAsync(repo,control));Check(!fakeComplete.GetProperty("eligible_for_completion").GetBoolean(),"A worker-only completion claim cannot substitute for canonical validation");
        var mcpControl=Path.Combine(dir,"mcp-control");var messages=new[]{
            JsonSerializer.Serialize(new{jsonrpc="2.0",id=1,method="tools/list"}),
            JsonSerializer.Serialize(new{jsonrpc="2.0",id=2,method="tools/call",@params=new{name="localbrain_agent_v2_run",arguments=new{workspace=repo,task=JsonDocument.Parse(AgentTaskRunner.RequestJson(request)).RootElement}}}),
            JsonSerializer.Serialize(new{jsonrpc="2.0",id=3,method="tools/call",@params=new{name="localbrain_agent_v2_status",arguments=new{workspace=repo}}})};
        var originalIn=Console.In;var originalOut=Console.Out;var capture=new StringWriter();watch.Restart();
        try{Console.SetIn(new StringReader(string.Join("\n",messages)));Console.SetOut(capture);await new AgentV2McpServer(new ProjectAllowlist(config),config,mcpControl,false).RunAsync();}
        finally{Console.SetIn(originalIn);Console.SetOut(originalOut);}
        var replies=capture.ToString().Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(s=>JsonDocument.Parse(s).RootElement.Clone()).ToArray();
        Check(watch.Elapsed<TimeSpan.FromSeconds(5)&&replies.Length==3&&replies.All(r=>!r.TryGetProperty("error",out _)),"MCP handles submission then status promptly on the same connection");
        var tools=replies[0].GetProperty("result").GetProperty("tools");Check(tools.GetArrayLength()==4&&tools.EnumerateArray().Any(t=>t.GetProperty("name").GetString()=="localbrain_agent_v2_cancel"),"Installed MCP contract exposes exact cancellation with command schema");
        Check(!JsonDocument.Parse(replies[2].GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.GetProperty("eligible_for_completion").GetBoolean(),"MCP queued status cannot falsely claim completion");
        var tracker=new ProgressTracker((id,hash)=>id=="public"&&hash=="genuine");
        void Progress(string id,object details){tracker.Observe(Json(new{type="tool_execution_start",toolCallId=id,toolName="host_command_status",args=new{}}));tracker.Observe(Json(new{type="tool_execution_end",toolCallId=id,isError=false,result=new{details}}));}
        Progress("one",new{operation_id="public",receipt_hash="forged"});Check(tracker.ToolCallsSinceProgress==1,"Model-supplied command progress is not authority");
        Progress("two",new{operation_id="public",receipt_hash="genuine"});Check(tracker.ToolCallsSinceProgress==0,"Host-verified new receipt counts as progress");
        Progress("three",new{operation_id="public",receipt_hash="genuine"});Check(tracker.ToolCallsSinceProgress==1,"Repeated status on the same receipt is not new progress");
        Progress("malformed",new[]{1,2});Check(tracker.ToolCallsSinceProgress==2,"Malformed progress details cannot reset monitoring or crash the task");
        var publicBody=System.Text.Encoding.UTF8.GetBytes("PUBLIC_MANAGED_RELAY");var permit=ManagedRelayPermit.Issue(CanonicalStore.Hash(publicBody),task);
        Check(!ManagedRelayPermit.Consume(null,publicBody)&&ManagedRelayPermit.Consume(permit,publicBody),"Only a one-use Host-issued capability selects the managed Bridge route");
        refused=false;try{ManagedRelayPermit.Consume(permit,publicBody);}catch(IOException){refused=true;}Check(refused,"A consumed capability cannot bypass normal supervision twice");
        permit=ManagedRelayPermit.Issue(CanonicalStore.Hash(publicBody),task);refused=false;try{ManagedRelayPermit.Consume(permit,new byte[]{1});}catch(UnauthorizedAccessException){refused=true;}Check(refused,"Capability is bound to the exact admitted provider payload");
        await PositiveCompletionAsync(dir,repo,request,config);
        Console.WriteLine("Managed Agent admission PASS: bounded submission, exact request/hash compatibility, task ownership, duplicate observation, exact cancel, canonical completion gate, prefix scope and Host-only progress. Public isolated proof; no native approval/model/production changes.");
    }
    private static async Task PositiveCompletionAsync(string directory,string repo,AgentTaskRequest request,ClientConfig config)
    {
        var control=Path.Combine(directory,"positive-control");var submitted=Json(await TaskAgentCoordinator.SubmitAsync(repo,request,config,control:control,launch:false));
        var id=submitted.GetProperty("task_id").GetString()!;var dir=TaskAgentCoordinator.CurrentDirectory(repo,control)!;
        var job=JsonSerializer.Deserialize<TaskAgentCoordinator.Job>(File.ReadAllBytes(Path.Combine(dir,"job.json")),ClientConfig.JsonOptions)!;
        var pending=job.Authorization;File.WriteAllBytes(pending.Output,JsonSerializer.SerializeToUtf8Bytes(new{source="windows_native_task_command_grant",confirmed=true,nonce=pending.Nonce,input_hash=pending.InputHash,task_id=id,request_hash=pending.RequestHash,fixture_only=true}));
        // The existing contract permits the repository root, so use an ignored .git/info/exclude entry for public output.
        var output=Path.Combine(repo,"public-result.txt");File.AppendAllText(Path.Combine(repo,".git","info","exclude"),"\npublic-result.txt\n.localbrain/\n");
        using(TaskCommandAuthorization.Enter(TaskCommandAuthorization.Read(pending)!))using(var store=new CanonicalStore(control))using(var journal=new HostTaskJournal(store,repo,id,request,job.Head)){
            var dotnet=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"dotnet","dotnet.exe");
            await journal.Commands!.StartAsync(new("public_success","windows",dotnet,[typeof(ManagedAgentTests).Assembly.Location,"--task-command-child","success",output],ExpectedOutputs:[output]),CancellationToken.None);
            await journal.Commands.SettleAsync(CancellationToken.None);Check(journal.Commands.Test("public_success").Passed,"Positive managed completion uses a real exited command and file");
            var snapshot=await GitEvidence.CaptureAsync(repo,request.AllowedFiles);Check(snapshot.ChangedFiles.Length==0,"Public command-only fixture leaves Git diff empty");
            journal.SaveValidation(new(true,true,true,false,false,[],false,false,false)); // synthetic Critic verdict, never a real-model proof
            journal.Checkpoint(new{task_id=id,phase="complete",request_hash=job.RequestHash,git_head=job.Head,actor_turns=1,changed_files=snapshot.ChangedFiles,
                review_diff_sha256=AgentTaskRunner.HashTextForHost(snapshot.ReviewDiff),command_evidence_inventory=journal.Commands.InventoryHash()});
        }
        TaskCommandGate.WriteReplace(Path.Combine(dir,"report.json"),new TaskAgentCoordinator.Report(id,"complete",true,1,"Synthetic validation with actual public process",DateTimeOffset.UtcNow));
        Check(Json(await TaskAgentCoordinator.StatusAsync(repo,control)).GetProperty("eligible_for_completion").GetBoolean(),"Command-only completion requires matching canonical gates, Git and real command inventory");
        var receipt=Path.Combine(control,"command-jobs",id,"public_success","receipt.json");var original=File.ReadAllBytes(receipt);File.AppendAllText(receipt," ");
        Check(!Json(await TaskAgentCoordinator.StatusAsync(repo,control)).GetProperty("eligible_for_completion").GetBoolean(),"Even semantically identical receipt-byte drift invalidates the frozen canonical completion inventory");File.WriteAllBytes(receipt,original);
        File.WriteAllText(output,"OUTPUT_DRIFT");Check(!Json(await TaskAgentCoordinator.StatusAsync(repo,control)).GetProperty("eligible_for_completion").GetBoolean(),"Post-completion output drift cannot retain completion");
    }
}
