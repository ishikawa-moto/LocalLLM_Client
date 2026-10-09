using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.AgentV2;

internal static class TaskMemoryEpochLive
{
    public static async Task RunAsync(string hostRoot) {
        hostRoot=Path.GetFullPath(hostRoot);
        if(File.Exists(Path.Combine(hostRoot,"canonical.db")))throw new InvalidOperationException("Live epoch fixture requires a fresh isolated Host root");
        var config=JsonSerializer.Deserialize<TaskMemoryQ8.Options>(File.ReadAllText(Path.Combine(hostRoot,"taskmemory-q8","config.json")),ClientConfig.JsonOptions)!;
        if(config.ContextLimit!=131072)throw new Exception("Epoch live fixture requires isolated128K candidate config");
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(25));
        var taskId="epoch_live_"+Guid.NewGuid().ToString("N");
        var workspace=Path.Combine(hostRoot,"pilot-workspace");
        var request=AgentTaskRequest.Parse("""{"requirement":"Recover old failed newline test evidence; memory may select IDs only","acceptance_criteria":["same task and preserved counters"],"risk":"LOW","approved_high_risk":false,"allowed_files":["greeting.txt"],"required_tests":[{"kind":"git_diff_check"}]}""");
        string failureId;string[] failureRefs;string requestHash;
        int firstPid;int secondPid;int pressureTokens;object firstMetrics;object firstUse;object secondMetrics;
        using(var store=new CanonicalStore(hostRoot))
        using(var journal=new HostTaskJournal(store,workspace,taskId,request,"fixture_baseline"))
        await using(var memory=journal.CreateMemory()) {
            journal.Checkpoint(new {phase="running",actor_turns=3,tool_calls=2,elapsed_time=23,unresolved_issue="greeting.txt missing LF",false_verified=true});
            journal.Record("validation_result",new {passed=false,reason="greeting.txt missing terminating LF newline; old failure needlefix"},"host_verified");
            var failure=store.MemoryCandidates(taskId).Single(e=>e.EventType=="validation_result");failureId=failure.EventId;failureRefs=failure.EvidenceRefs;
            var unrelated=store.PutEvidence(Encoding.UTF8.GetBytes("unrelated recent actor observation"));
            for(var i=0;i<300;i++)store.Append(new(taskId,HostTaskJournal.WorkspaceId(workspace),"windows_host","irrelevant_"+i,"actor_result","fixture","unverified",[unrelated],JsonSerializer.SerializeToElement(new {i})));
            if(store.MemoryCandidates(taskId).Any(c=>c.EventId==failureId))throw new Exception("Old failure is not beyond legacy256 window");
            var refs=await journal.SelectMemoryRefsAsync(memory,"Select the old validation_result for needlefix missing terminating LF newline",deadline.Token);
            AssertSelected(store,taskId,failureId,failureRefs,refs,1);
            var diag=JsonSerializer.SerializeToElement(memory.Diagnostics);firstPid=diag.GetProperty("process_id").GetInt32();firstMetrics=memory.Diagnostics;
            using(var process=Process.GetProcessById(firstPid))firstUse=new {process.WorkingSet64,process.PeakWorkingSet64,process.PrivateMemorySize64};
            if(diag.GetProperty("requested_context_limit").GetInt32()!=131072 || !diag.GetProperty("runtime_metric_lines").EnumerateArray().Any(e=>e.GetString()!.Contains("4352")))throw new Exception("Actual128K q8 KV allocation absent from runtime metrics");
            Console.WriteLine(JsonSerializer.Serialize(new {stage="first_epoch_verified",task_id=taskId,failure_id=failureId,firstMetrics,firstUse}));
            try {await memory.PredictAsync(string.Concat(Enumerable.Repeat(" x",150000)),[failureId],token:deadline.Token);throw new Exception("Oversized actual-token prompt accepted");}
            catch(InvalidDataException e) when(e.Message.Contains("token budget")) {}
            pressureTokens=JsonSerializer.SerializeToElement(memory.Diagnostics).GetProperty("measured_prompt_tokens").GetInt32();
            if(pressureTokens+TaskMemoryQ8.OutputReserve+TaskMemoryQ8.SafetyReserve<=131072)throw new Exception("Actual tokenizer pressure threshold was not crossed");
            journal.Record("taskmemory_context_pressure",new {actual_prompt_tokens=pressureTokens,configured_context=131072,action="reject before completion; discard native state and rebuild from canonical sources"},"runtime_observed");
            requestHash=store.GetTask(taskId)!.RequestHash;
            AssertState(store,taskId,requestHash,1);
            await memory.DisposeAsync();AssertGone(firstPid);
            Console.WriteLine(JsonSerializer.Serialize(new {stage="pressure_refused_and_old_state_discarded",pressureTokens,first_pid_gone=true}));
        }
        // This is a new Host store/journal/memory instance; no old native KV or derived seed is loaded.
        using(var store=new CanonicalStore(hostRoot))
        using(var journal=new HostTaskJournal(store,workspace,taskId,request,"fixture_baseline"))
        await using(var memory=journal.CreateMemory()) {
            AssertState(store,taskId,requestHash,1);
            var refs=await journal.SelectMemoryRefsAsync(memory,"Select the old validation_result for needlefix missing terminating LF newline",deadline.Token);
            AssertSelected(store,taskId,failureId,failureRefs,refs,2);AssertState(store,taskId,requestHash,2);
            secondMetrics=memory.Diagnostics;secondPid=JsonSerializer.SerializeToElement(secondMetrics).GetProperty("process_id").GetInt32();
            if(secondPid==firstPid)throw new Exception("Native epoch did not create a new owned process");
            await memory.DisposeAsync();AssertGone(secondPid);
            var state=JsonNode.Parse(store.GetTask(taskId)!.StateJson)!;
            var hash=state["taskmemory_epoch"]!["seed_hash"]!.GetValue<string>();
            var bytes=File.ReadAllBytes(Path.Combine(hostRoot,"evidence",hash+".blob"));
            if(CanonicalStore.Hash(bytes)!=hash)throw new Exception("Rebuilt immutable seed hash invalid");
            using var seed=JsonDocument.Parse(bytes);
            if(seed.RootElement.GetProperty("canonical_state").TryGetProperty("taskmemory_epoch",out _))throw new Exception("Rebuilt seed nested prior derived epoch");
            Console.WriteLine(JsonSerializer.Serialize(new {stage="cold_host_rebuild_verified",task_id=taskId,secondMetrics,seed_hash=hash,first_pid=firstPid,second_pid=secondPid,both_processes_gone=true,epochs=2,actor_turns=3,tool_calls=2,elapsed_time=23,false_verified_preserved=true,approval_granted=false,actual_pressure_tokens=pressureTokens}));
        }
    }
    private static void AssertGone(int pid) {
        try {using var process=Process.GetProcessById(pid);if(!process.HasExited)throw new Exception("Old native epoch process survived disposal");}catch(ArgumentException) {}
    }
    private static void AssertState(CanonicalStore store,string taskId,string requestHash,int epoch) {
        var task=store.GetTask(taskId)!;var state=JsonNode.Parse(task.StateJson)!;
        if(task.RequestHash!=requestHash || task.ActorTurns!=3 || task.ToolCalls!=2 || task.Handoffs!=0 || task.ExternalReviews!=0 ||
           state["taskmemory_epoch"]?["number"]?.GetValue<int>()!=epoch || state["elapsed_time"]?.GetValue<int>()!=23 || state["false_verified"]?.GetValue<bool>()!=true ||
           store.HasApproval(taskId,requestHash,"fixture_baseline"))throw new Exception("Epoch/reopen reset task state or conferred approval");
    }
    private static void AssertSelected(CanonicalStore store,string taskId,string failureId,string[] failureRefs,string[] refs,int epoch) {
        var events=store.MemoryCandidates(taskId);
        if(!failureRefs.All(refs.Contains) || !events.Any(e=>e.EventType=="taskmemory_selection" && e.Preview.Contains(failureId,StringComparison.Ordinal)) ||
           events.Any(e=>e.EventType=="taskmemory_unavailable"))throw new Exception("Actual native reranking did not select old canonical failure; fallback is not qualification");
        var epochState=JsonNode.Parse(store.GetTask(taskId)!.StateJson)?["taskmemory_epoch"];
        if(epochState?["number"]?.GetValue<int>()!=epoch)throw new Exception("Native recall did not advance durable epoch");
    }
}