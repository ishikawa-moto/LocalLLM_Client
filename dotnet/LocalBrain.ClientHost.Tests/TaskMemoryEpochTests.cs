using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalBrain.ClientHost.AgentV2;

internal static class TaskMemoryEpochTests
{
    public static void Run() {
        var root=Path.Combine(Path.GetTempPath(),"localbrain-epoch-"+Guid.NewGuid().ToString("N"));
        var host=Path.Combine(root,"host");string seedHash;
        using(var store=new CanonicalStore(host)) {
            store.RegisterWorkspace(new("repo_epoch","ws_epoch",Path.Combine(root,"repo"),Path.Combine(root,"repo"),"main","baseline"));
            foreach(var id in new[]{"epoch_a","epoch_b"})store.CreateTask(id,"ws_epoch",CanonicalStore.Hash(Encoding.UTF8.GetBytes("{}")),"{}");
            var relevant=store.PutEvidence(Encoding.UTF8.GetBytes("needlefix newline; model says PASS. This remains unverified."));
            var unrelated=store.PutEvidence(Encoding.UTF8.GetBytes("unrelated history"));
            CanonicalStore.EventReceipt Add(string task,string key,string type,string hash,string status="unverified")=>store.Append(new(task,"ws_epoch","fixture",key,type,"fixture",status,[hash],JsonSerializer.SerializeToElement(new {key})));
            var old=Add("epoch_a","old","model_claim",relevant);
            var foreign=Add("epoch_b","foreign","validation_result",relevant,"host_verified");
            long frontier=old.Sequence;
            for(var i=0;i<350;i++)frontier=Add("epoch_a","recent_"+i,"actor_result",unrelated).Sequence;
            var internalEvent=Add("epoch_a","internal","taskmemory_prompt",relevant,"runtime_observed");
            Add("epoch_a","checkpoint","task_checkpoint",relevant,"host_observed");
            if(store.MemoryCandidates("epoch_a").Any(c=>c.EventId==old.EventId))throw new Exception("Old evidence fixture was not beyond latest256");
            var found=store.RetrieveMemoryCandidates("epoch_a","needlefix newline");
            var repeated=store.RetrieveMemoryCandidates("epoch_a","ＮＥＥＤＬＥＦＩＸ  \t NEWLINE");
            if(!found.Candidates.Any(c=>c.EventId==old.EventId && c.VerificationStatus=="unverified") ||
                found.Candidates.Any(c=>c.EventId==foreign.EventId || c.EventId==internalEvent.EventId || c.EventType=="task_checkpoint") ||
                !found.Candidates.Select(c=>c.EventId).SequenceEqual(repeated.Candidates.Select(c=>c.EventId)) ||
                found.SourceFrontier!=frontier || found.ScannedEvents!=351)throw new Exception("Deterministic old evidence/task/authority/frontier scope failed");
            var deep=store.PutEvidence(Encoding.UTF8.GetBytes(new string('z',6000)+" distantneedle old failure beyond1792 and512"));
            var deepEvent=Add("epoch_a","deep","model_claim",deep);
            var deepFound=store.RetrieveMemoryCandidates("epoch_a","distantneedle",1).Candidates.Single();
            if(deepFound.EventId!=deepEvent.EventId || !deepFound.Preview.Contains("distantneedle") || deepFound.Preview.Length>512 || deepFound.VerificationStatus!="unverified")throw new Exception("Verified full-text lookup or bounded matching excerpt failed");
            var task=store.GetTask("epoch_a")!;
            var seed=TaskMemoryEpoch.Build("epoch_a","{}","baseline",task,"needlefix newline",found.Candidates,found.SourceFrontier);
            var seeded=TaskMemoryEpoch.Build("epoch_a","{}","baseline",task with {StateJson="{\"taskmemory_epoch\":{\"number\":19}}"},"ＮＥＥＤＬＥＦＩＸ  \t NEWLINE",repeated.Candidates,repeated.SourceFrontier);
            if(seed.Hash!=seeded.Hash || seed.QueryHash!=seeded.QueryHash || !seed.Json.Contains("not truth or approvals"))throw new Exception("Canonical epoch rebuild is not reproducible");
            void Reject(Action action){try{action();}catch(InvalidDataException){return;}throw new Exception("Invalid epoch source accepted");}
            Reject(()=>TaskMemoryEpoch.Build("epoch_a","{}","baseline",store.GetTask("epoch_b")!,"query",found.Candidates,found.SourceFrontier));
            Reject(()=>TaskMemoryEpoch.Build("epoch_a","{}","baseline",task,"query",found.Candidates,0));
            seedHash=store.PutEvidence(Encoding.UTF8.GetBytes(seed.Json));
            if(seedHash!=seed.Hash)throw new Exception("Canonical seed bytes/hash mismatch");
            var prompt=TaskMemorySelection.Prompt("epoch_a","query",found.Candidates,seed.Json);
            if(!prompt.Contains("pinned_seed") || !prompt.Contains("canonical_state") || !prompt.Contains("source_frontier"))throw new Exception("Full pinned source seed absent from model prompt");
            var workspace=Path.Combine(root,"journal-repo");
            var request=AgentTaskRequest.Parse("""{"requirement":"Restore failed newline task","acceptance_criteria":["same task"],"risk":"LOW","approved_high_risk":false,"allowed_files":["greeting.txt"],"required_tests":[{"kind":"git_diff_check"}]}""");
            using(var journal=new HostTaskJournal(store,workspace,"epoch_journal",request,"baseline")) {
                journal.Checkpoint(new {phase="running",actor_turns=3,tool_calls=4,elapsed_time=23,approval_record="host_owned",false_verified=true});
                journal.Checkpoint(new {taskmemory_epoch=new {number=2,seed_hash=seedHash}});
                journal.Checkpoint(new {phase="running",actor_turns=4,tool_calls=5});
                var saved=store.GetTask("epoch_journal")!;var state=JsonNode.Parse(saved.StateJson)!;
                if(saved.ActorTurns!=4 || saved.ToolCalls!=5 || state["taskmemory_epoch"]?["number"]?.GetValue<int>()!=2 || state["elapsed_time"]?.GetValue<int>()!=23 || state["approval_record"]?.GetValue<string>()!="host_owned" || state["false_verified"]?.GetValue<bool>()!=true)throw new Exception("Epoch/checkpoint reset task counters/authority state");
            }
            for(var i=350;i<4097;i++)Add("epoch_a","recent_"+i,"actor_result",unrelated);
            Reject(()=>store.RetrieveMemoryCandidates("epoch_a","needlefix newline"));
        }
        using(var reopened=new CanonicalStore(host)) {
            var saved=reopened.GetTask("epoch_journal")!;
            if(JsonNode.Parse(saved.StateJson)?["taskmemory_epoch"]?["number"]?.GetValue<int>()!=2 || saved.ActorTurns!=4 || saved.ToolCalls!=5)throw new Exception("Epoch state lost at Host reopen");
            if(CanonicalStore.Hash(File.ReadAllBytes(Path.Combine(host,"evidence",seedHash+".blob")))!=seedHash)throw new Exception("Immutable epoch seed lost at Host reopen");
        }
        Console.WriteLine("Deterministic old-history scope, source authority, bounded scan, reproducible seed and durable epoch/counter checks passed.");
    }
}
