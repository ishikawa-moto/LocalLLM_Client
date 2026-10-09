using System.Diagnostics;
using System.Text.Json.Nodes;
using LocalBrain.ClientHost.AgentV2;

internal static class TaskMemoryEpochCrashTests
{
    private static AgentTaskRequest Request()=>AgentTaskRequest.Parse("""{"requirement":"Keep canonical epoch across Host termination","acceptance_criteria":["same task counters"],"risk":"LOW","approved_high_risk":false,"allowed_files":["greeting.txt"],"required_tests":[{"kind":"git_diff_check"}]}""");
    private static TaskMemoryEpoch.Seed Seed(CanonicalStore store)=>TaskMemoryEpoch.Build("epoch_crash",AgentTaskRunner.RequestJson(Request()),"baseline",store.GetTask("epoch_crash")!,"failed newline",[],0);
    internal static void CrashFixture(string root,string boundary) {
        var armed=false;
        using var store=new CanonicalStore(root,p=>{if(armed && p==boundary)Environment.FailFast("intentional atomic memory epoch boundary");});
        using var journal=new HostTaskJournal(store,Path.Combine(root,"workspace"),"epoch_crash",Request(),"baseline");
        journal.Checkpoint(new {phase="running",actor_turns=3,tool_calls=2,elapsed_time=23,false_verified=true});
        var seed=Seed(store);armed=true;journal.CommitMemoryEpoch(seed);
        throw new Exception("Atomic epoch crash boundary did not fire");
    }
    internal static async Task RunAsync() {
        foreach(var boundary in new[]{"event_before_commit","event_after_commit"}) {
            var root=Path.Combine(Path.GetTempPath(),"localbrain-epoch-crash-"+Guid.NewGuid().ToString("N"));
            var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{typeof(TaskMemoryEpochCrashTests).Assembly.Location,"--memory-epoch-crash",root,boundary})start.ArgumentList.Add(arg);
            using var child=Process.Start(start)!;var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));await stdout;await stderr;
            if(child.ExitCode==0)throw new Exception("Epoch crash fixture returned success");
            using var store=new CanonicalStore(root);var task=store.GetTask("epoch_crash")!;
            var state=JsonNode.Parse(task.StateJson)!;var expected=boundary=="event_after_commit"?1:0;
            var epoch=state["taskmemory_epoch"]?["number"]?.GetValue<int>()??0;
            var events=store.MemoryCandidates("epoch_crash").Where(c=>c.EventType=="taskmemory_epoch_seed").ToArray();
            if(epoch!=expected || events.Length!=expected || task.ActorTurns!=3 || task.ToolCalls!=2 || state["elapsed_time"]?.GetValue<int>()!=23 || state["false_verified"]?.GetValue<bool>()!=true)throw new Exception("Seed event/state split or task reset across epoch crash");
            if(expected==1) {
                var hash=state["taskmemory_epoch"]!["seed_hash"]!.GetValue<string>();
                if(!events[0].EvidenceRefs.Contains(hash) || CanonicalStore.Hash(store.ReadEvidence(hash))!=hash)throw new Exception("Committed epoch seed/reference integrity failed");
            }
            using var journal=new HostTaskJournal(store,Path.Combine(root,"workspace"),"epoch_crash",Request(),"baseline");
            journal.CommitMemoryEpoch(Seed(store));
            var continued=store.GetTask("epoch_crash")!;
            if(JsonNode.Parse(continued.StateJson)?["taskmemory_epoch"]?["number"]?.GetValue<int>()!=expected+1 || continued.ActorTurns!=3 || continued.ToolCalls!=2 || store.MemoryCandidates("epoch_crash").Count(c=>c.EventType=="taskmemory_epoch_seed")!=expected+1)throw new Exception("Epoch recovery duplicated/reused committed number");
            Console.WriteLine("Atomic memory epoch Host termination at "+boundary+": same-task seed/state/counters PASS");
        }
    }
}