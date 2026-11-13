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
            if(critic){Check(prompt.Contains("PUBLIC_EVIDENCE_731"),"Fresh Critic receives real retained tool evidence");return Result("{\"verdict\":\"PASS\",\"issues\":[]}");}
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

        var source=Encoding.UTF8.GetBytes("{\"messages\":[{\"role\":\"user\",\"content\":\"PUBLIC_GOAL\"}],\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"read_file\"}},{\"type\":\"function\",\"function\":{\"name\":\"run_terminal_command\"}},{\"type\":\"function\",\"function\":{\"name\":\"mcp_localbrain_agent_v2_investigate\"}}]}");
        var routed=JsonNode.Parse(ContinueManagedRouting.Prepare(source))!;
        Check(routed["tools"]!.AsArray().Count==1&&routed["messages"]![1]!["content"]!.GetValue<string>()=="PUBLIC_GOAL","Continue investigation tools route to Host without rewriting the user goal");
        var plain=Encoding.UTF8.GetBytes("{\"messages\":[],\"tools\":[]}");
        Check(ContinueManagedRouting.Prepare(plain).SequenceEqual(plain),"Other model/managed relay requests remain byte-exact");
        Console.WriteLine("Investigation PASS: exact goal, automatic incomplete-turn continuation, context handoff, retained evidence, no duplicate reads, immutable final report, completion gate, no command capability, infrastructure stop and Continue routing.");
    }
}
