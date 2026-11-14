using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.AgentV2;

internal static class InvestigationTests
{
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static PiRunResult Result(string text,bool handoff=false)=>new(text,0,0,0,0,0,0,0,0,false,0,0,null,null,false,handoff);
    internal static async Task RunAsync(string directory,string repo,ClientConfig config)
    {
        var request=new AgentTaskRequest("Investigate PUBLIC_GOAL_731 from README.md, retain progress and report the finding with evidence.",
            ["Report the public README finding, evidence and unknowns."],"LOW",false,["README.md"],[new("git_diff_check",null)]){ReadOnly=true};
        Check(AgentTaskRequest.Parse(AgentTaskRunner.RequestJson(request)).ReadOnly,"Read-only task participates in request identity");
        Check(AgentTaskRunner.RequestHash(request)!=AgentTaskRunner.RequestHash(request with{ReadOnly=false}),"Read-only cannot share a mutation request hash");
        foreach(var invalid in new[]{request with{Risk="NORMAL"},request with{RequiredTests=[new("npm_test",null)]},request with{CommandExecution=new(["windows"],["."],[],30,10,6,1,4096)}}){
            var rejected=false;try{AgentTaskRequest.Parse(AgentTaskRunner.RequestJson(invalid));}catch(InvalidDataException){rejected=true;}
            Check(rejected,"Read-only mode must never authorize commands or bypass risk-specific review");
        }
        var control=Path.Combine(directory,"investigation-control");
        var submitted=JsonSerializer.SerializeToElement(await TaskAgentCoordinator.SubmitAsync(repo,request,config,control:control,launch:false));
        var id=submitted.GetProperty("task_id").GetString()!;var dir=TaskAgentCoordinator.CurrentDirectory(repo,control)!;
        var job=JsonSerializer.Deserialize<TaskAgentCoordinator.Job>(File.ReadAllBytes(Path.Combine(dir,"job.json")),ClientConfig.JsonOptions)!;
        Check(job.Authorization is null && !File.Exists(Path.Combine(dir,"authorization-input.json")),"Safe read-only capability creates no command approval or fabricated grant");
        var calls=0;var reads=0;string? firstSession=null;var rotated=false;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution=AgentTaskRunner.RunInvestigationAsync(job,config,(CancellationToken)default,async(prompt,critic,session,token)=>{
            await Task.Yield();
            if(critic){
                Check(prompt.Contains("PUBLIC_EVIDENCE_731")&&prompt.Contains("PUBLIC_PAGE_4096")&&prompt.Contains("PUBLIC_SEARCH_EVIDENCE"),
                    "Fresh Critic receives real retained reads, search results and the recalled page");
                return Result("{\"verdict\":\"PASS\",\"issues\":[]}");
            }
            calls++;Check(prompt.Contains(request.Requirement)&&prompt.Contains(request.AcceptanceCriteria[0]),"Every continuation receives exact original goal and criteria");
            if(calls==1){firstSession=session;reads++;
                HostTaskJournal.Current!.RecordToolEvent(JsonSerializer.SerializeToElement(new{type="tool_execution_start",toolCallId="public_read",toolName="read",args=new{path="README.md"}}));
                entered.SetResult();await release.Task.WaitAsync(token);
                HostTaskJournal.Current!.RecordToolEvent(JsonSerializer.SerializeToElement(new{type="tool_execution_end",toolCallId="public_read",toolName="read",isError=false,result=new{content="PUBLIC_EVIDENCE_731"}}));
                return Result("I will continue working.");
            }
            Check(prompt.Contains("PUBLIC_EVIDENCE_731"),"Progress survives settled turn and handoff without repeated file reads");
            if(calls==2)return Result("",true);
            rotated=session!=firstSession;
            HostTaskJournal.Current!.RecordToolEvent(JsonSerializer.SerializeToElement(new{type="tool_execution_end",toolCallId="search",toolName="host_memory_search",isError=false,result=new{content="PUBLIC_SEARCH_EVIDENCE"}}));
            HostTaskJournal.Current!.RecordToolEvent(JsonSerializer.SerializeToElement(new{type="tool_execution_start",toolCallId="recall",toolName="host_recall",args=new{offset=4096}}));
            HostTaskJournal.Current!.RecordToolEvent(JsonSerializer.SerializeToElement(new{type="tool_execution_end",toolCallId="recall",toolName="host_recall",isError=false,result=new{content="PUBLIC_PAGE_4096"}}));
            return Result("README.md:1 contains PUBLIC_EVIDENCE_731. The public finding is supported by the retained read result. No additional environment was inspected; other conditions remain unknown.");
        });
        await entered.Task;
        var waiting=CanonicalStore.ReadLatest(control,HostTaskJournal.WorkspaceId(repo))!;
        using(var pending=JsonDocument.Parse(waiting.StateJson))Check(!execution.IsCompleted&&pending.RootElement.GetProperty("activity").GetString()=="tool_running"&&calls==1,"An actual pending tool stays running and cannot trigger another Actor");
        release.SetResult();var result=await execution;
        Check(result.Complete&&result.Turns==3&&calls==3&&reads==1&&rotated,"Incomplete turn and context handoff automatically continue once each without replay");
        TaskCommandGate.WriteReplace(Path.Combine(dir,"report.json"),new TaskAgentCoordinator.Report(id,"complete",true,3,null,DateTimeOffset.UtcNow));
        var status=JsonSerializer.SerializeToElement(await TaskAgentCoordinator.StatusAsync(repo,control));
        Check(status.GetProperty("eligible_for_completion").GetBoolean()&&status.GetProperty("final_report").GetString()!.Contains("PUBLIC_EVIDENCE_731"),"Canonical completion gate and immutable report reach MCP status");
        var duplicate=JsonSerializer.SerializeToElement(await TaskAgentCoordinator.SubmitAsync(repo,request,config,control:control,launch:false));
        Check(duplicate.GetProperty("task_id").GetString()==id&&calls==3,"Duplicate submission observes completed task without another Actor");
        var again=false;try{await AgentTaskRunner.RunInvestigationAsync(job,config,default,(_,_,_,_)=>throw new Exception("must not run"));}catch(InvalidOperationException){again=true;}
        Check(again,"Unknown/existing task execution cannot be replayed");

        var infraControl=Path.Combine(directory,"investigation-infra");
        await TaskAgentCoordinator.SubmitAsync(repo,request,config,control:infraControl,launch:false);
        var infraDir=TaskAgentCoordinator.CurrentDirectory(repo,infraControl)!;
        var infraJob=JsonSerializer.Deserialize<TaskAgentCoordinator.Job>(File.ReadAllBytes(Path.Combine(infraDir,"job.json")),ClientConfig.JsonOptions)!;
        var attempts=0;
        var stopped=await AgentTaskRunner.RunInvestigationAsync(infraJob,config,default,(_,_,_,_)=>{attempts++;throw new IOException("public provider failure");});
        Check(!stopped.Complete&&attempts==1&&stopped.Reason!.StartsWith("infrastructure_stopped"),"Infrastructure failure neither retries an unknown dispatch nor calls external rescue");

        var timeoutControl=Path.Combine(directory,"investigation-pi-deadline");
        await TaskAgentCoordinator.SubmitAsync(repo,request,config,control:timeoutControl,launch:false);
        var timeoutDir=TaskAgentCoordinator.CurrentDirectory(repo,timeoutControl)!;
        var timeoutJob=JsonSerializer.Deserialize<TaskAgentCoordinator.Job>(File.ReadAllBytes(Path.Combine(timeoutDir,"job.json")),ClientConfig.JsonOptions)!;
        var timeoutAttempts=0;
        var timedOut=await AgentTaskRunner.RunInvestigationAsync(timeoutJob,config,default,(_,critic,_,token)=>{
            Check(!critic&&!token.IsCancellationRequested,"Pi can time out before the investigation deadline");
            timeoutAttempts++;
            HostTaskJournal.Current!.RecordToolEvent(JsonSerializer.SerializeToElement(new{type="tool_execution_end",
                toolCallId="deadline_read",toolName="read",isError=false,result=new{content="PUBLIC_PARTIAL_EVIDENCE"}}));
            throw new PiTurnTimeoutException();
        });
        Check(!timedOut.Complete&&timedOut.Turns==1&&timedOut.Reason=="pi_turn_deadline_exceeded"&&timeoutAttempts==1,
            "A Pi turn deadline settles as incomplete without replay or another model call");
        TaskCommandGate.WriteReplace(Path.Combine(timeoutDir,"report.json"),new TaskAgentCoordinator.Report(
            timeoutJob.TaskId,"blocked",false,timedOut.Turns,timedOut.Reason,DateTimeOffset.UtcNow));
        var timeoutStatus=JsonSerializer.SerializeToElement(await TaskAgentCoordinator.StatusAsync(repo,timeoutControl));
        Check(timeoutStatus.GetProperty("canonical_phase").GetString()=="blocked"&&
            !timeoutStatus.GetProperty("eligible_for_completion").GetBoolean()&&
            timeoutStatus.GetProperty("final_report").GetString()!.Contains("No automatic replay."),
            "A timed-out turn records the incomplete report and terminal Canonical phase exposed to MCP");

        PiRunResult Interrupted(string action)=>new("",4,0,0,0,0,0,0,4,false,0,0,null,new SupervisorDecision(action,"hard_rule","recent_action_did_not_change_state",null,null,false),true);
        async Task<(AgentTaskRunner.InvestigationResult Result,int Actors)> Supervised(string name,string action){
            var supervisedControl=Path.Combine(directory,name);
            await TaskAgentCoordinator.SubmitAsync(repo,request,config,control:supervisedControl,launch:false);
            var supervisedJob=JsonSerializer.Deserialize<TaskAgentCoordinator.Job>(File.ReadAllBytes(Path.Combine(TaskAgentCoordinator.CurrentDirectory(repo,supervisedControl)!,"job.json")),ClientConfig.JsonOptions)!;
            var actors=0;
            var outcome=await AgentTaskRunner.RunInvestigationAsync(supervisedJob,config,default,async(prompt,critic,session,token)=>{
                await Task.Yield();
                if(critic)return Result("{\"verdict\":\"PASS\",\"issues\":[]}");
                if(++actors==1)return Interrupted(action);
                Check(prompt.Contains("Host Supervisor interrupted the previous attempt to re-plan"),"REPLAN feedback reaches the next Actor turn");
                HostTaskJournal.Current!.RecordToolEvent(JsonSerializer.SerializeToElement(new{type="tool_execution_start",toolCallId=name+"_read",toolName="read",args=new{path="README.md"}}));
                HostTaskJournal.Current!.RecordToolEvent(JsonSerializer.SerializeToElement(new{type="tool_execution_end",toolCallId=name+"_read",toolName="read",isError=false,result=new{content="PUBLIC_EVIDENCE_731"}}));
                return Result("README.md:1 contains PUBLIC_EVIDENCE_731. The public finding is supported by the retained read result. Other conditions remain unknown.");
            });
            return (outcome,actors);
        }
        var replanned=await Supervised("investigation-replan","REPLAN");
        Check(replanned.Result.Complete&&replanned.Actors==2&&replanned.Result.Turns==2,"A Supervisor REPLAN continues to a new Actor turn and completes only through the gate");
        var halted=await Supervised("investigation-stop","STOP");
        Check(!halted.Result.Complete&&halted.Actors==1&&halted.Result.Reason=="supervisor_stopped_no_progress","A Supervisor STOP still ends the investigation without completion");

        var source=Encoding.UTF8.GetBytes("{\"messages\":[{\"role\":\"user\",\"content\":\"PUBLIC_GOAL\"}],\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"read_file\"}},{\"type\":\"function\",\"function\":{\"name\":\"run_terminal_command\"}},{\"type\":\"function\",\"function\":{\"name\":\"mcp_localbrain_agent_v2_investigate\"}}]}");
        var routed=JsonNode.Parse(ContinueManagedRouting.Prepare(source))!;
        Check(routed["tools"]!.AsArray().Count==1&&routed["messages"]![1]!["content"]!.GetValue<string>()=="PUBLIC_GOAL","Continue investigation tools route to Host without rewriting the user goal");
        var withSystem=JsonNode.Parse(source)!.AsObject();
        withSystem["messages"]!.AsArray().Insert(0,new JsonObject{["role"]="system",["content"]="ORIGINAL_CLIENT_POLICY"});
        var prepared=JsonNode.Parse(ContinueManagedRouting.Prepare(Encoding.UTF8.GetBytes(withSystem.ToJsonString())))!;
        Check(prepared["messages"]!.AsArray().Count(m=>m?["role"]?.GetValue<string>()=="system")==1 &&
            prepared["messages"]![0]!["content"]!.GetValue<string>().StartsWith("ORIGINAL_CLIENT_POLICY\n\n",StringComparison.Ordinal) &&
            prepared["messages"]![1]!["content"]!.GetValue<string>()=="PUBLIC_GOAL",
            "Real Continue requests retain one leading system message accepted by the qualified Qwen template");
        withSystem["messages"]![0]!["content"]=new JsonArray(new JsonObject{["type"]="text",["text"]="ORIGINAL_BLOCK_POLICY"});
        prepared=JsonNode.Parse(ContinueManagedRouting.Prepare(Encoding.UTF8.GetBytes(withSystem.ToJsonString())))!;
        Check(prepared["messages"]![0]!["content"]!.AsArray().Count==2 &&
            prepared["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>()=="ORIGINAL_BLOCK_POLICY",
            "Structured system content remains intact within the same leading system message");
        var plain=Encoding.UTF8.GetBytes("{\"messages\":[],\"tools\":[]}");
        Check(ContinueManagedRouting.Prepare(plain).SequenceEqual(plain),"Other model/managed relay requests remain byte-exact");
        var mcpTask=JsonNode.Parse(AgentTaskRunner.RequestJson(request with{AllowedFiles=["**"]}))!.AsObject();
        mcpTask.Remove("read_only");
        var mcpInput=JsonSerializer.Serialize(new{jsonrpc="2.0",id=731,method="tools/call",@params=new{name="localbrain_agent_v2_investigate",arguments=new{workspace=repo,task=mcpTask}}});
        var originalIn=Console.In;var originalOut=Console.Out;var output=new StringWriter();var mcpControl=Path.Combine(directory,"readonly-mcp-input");
        try{Console.SetIn(new StringReader(mcpInput+"\n"));Console.SetOut(output);await new AgentV2McpServer(new ProjectAllowlist(config),config,mcpControl,false).RunAsync();}
        finally{Console.SetIn(originalIn);Console.SetOut(originalOut);}
        using(var reply=JsonDocument.Parse(output.ToString()))Check(reply.RootElement.GetProperty("id").GetInt32()==731&&!reply.RootElement.TryGetProperty("error",out _),"Investigate capability owns read-only mode when the caller omits the optional flag");
        var mcpDir=TaskAgentCoordinator.CurrentDirectory(repo,mcpControl)!;
        var mcpJob=JsonSerializer.Deserialize<TaskAgentCoordinator.Job>(File.ReadAllBytes(Path.Combine(mcpDir,"job.json")),ClientConfig.JsonOptions)!;
        Check(mcpJob.Authorization is null && AgentTaskRequest.Parse(mcpJob.RequestJson).ReadOnly,"Read-only wildcard selection creates no mutation or command grant");
        await McpStdioTests.AgentEntryAsync(directory,repo,config);
        Console.WriteLine("Investigation PASS: exact goal, automatic incomplete-turn continuation, context handoff, retained evidence, no duplicate reads, immutable final report, completion gate, no command capability, infrastructure stop, Pi deadline settlement and Continue routing.");
    }
}
