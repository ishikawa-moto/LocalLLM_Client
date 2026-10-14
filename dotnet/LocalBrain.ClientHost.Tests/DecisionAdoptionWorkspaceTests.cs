using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class DecisionAdoptionWorkspaceTests
{
    internal static void Run()
    {
        var baseDir=Path.Combine(Path.GetTempPath(),"localbrain-adoption-workspace-"+Guid.NewGuid().ToString("N"));
        var repo=Path.Combine(baseDir,"repo");Directory.CreateDirectory(repo);File.WriteAllText(Path.Combine(repo,"keep.txt"),"unchanged");
        using var store=new CanonicalStore(Path.Combine(baseDir,"host"));
        var ws=HostTaskJournal.WorkspaceId(repo);
        var proposal=new string('b',64);
        using(var journal=DecisionAdoptionWorkspace.Open(store,repo,"fixture_adoption_001",proposal,new string('a',40)).Journal){
            var task=store.LatestTask(ws)!;var state=JsonDocument.Parse(task.StateJson).RootElement;
            Check(task.ActorTurns==0&&task.ToolCalls==0&&state.GetProperty("administrative_purpose").GetString()=="decision_adoption"&&!state.GetProperty("eligible_for_completion").GetBoolean(),"Administrative task gained Actor/completion authority");
            Check(!Directory.Exists(Path.Combine(repo,".localbrain"))&&File.ReadAllText(Path.Combine(repo,"keep.txt"))=="unchanged","Repository changed");
        }
        var id=store.LatestTask(ws)!.TaskId;
        using(var journal=DecisionAdoptionWorkspace.Open(store,repo,"fixture_adoption_001",proposal,new string('a',40)).Journal)
            Check(store.LatestTask(ws)!.TaskId==id,"Identical intake duplicated task");
        Reject(()=>DecisionAdoptionWorkspace.Open(store,repo,"fixture_adoption_001",new string('c',64),new string('a',40)),"Proposal drift accepted");
        using(var journal=DecisionAdoptionWorkspace.Open(store,repo,"fixture_adoption_002",new string('c',64),new string('a',40)).Journal)
            Check(store.LatestTask(ws)!.TaskId!=id&&store.GetTask(id) is not null,"Separate adoption deleted previous owner");
        var request=AgentTaskRequest.Parse("""{"requirement":"Existing development task","acceptance_criteria":["Existing state retained"],"risk":"LOW","approved_high_risk":false,"allowed_files":["keep.txt"],"required_tests":[{"kind":"git_diff_check"}]}""");
        using(var journal=new HostTaskJournal(store,repo,"fixture_existing_task",request,new string('a',40)))journal.Checkpoint(new{phase="failed",git_head=new string('a',40),actor_turns=3,false_verified=true});
        var original=store.GetTask("fixture_existing_task")!;
        using(var journal=DecisionAdoptionWorkspace.Open(store,repo,"fixture_adoption_003",new string('d',64),new string('a',40)).Journal){
            var after=store.LatestTask(ws)!;Check(after.TaskId==original.TaskId&&after.StateJson==original.StateJson&&after.ActorTurns==3,"Existing developer task shadowed/reset");
        }
        var otherRepo=Path.Combine(baseDir,"legacy");Directory.CreateDirectory(Path.Combine(otherRepo,".localbrain"));File.WriteAllText(Path.Combine(otherRepo,".localbrain/task-state.json"),"preserve legacy bytes");
        Reject(()=>DecisionAdoptionWorkspace.Open(store,otherRepo,"fixture_adoption_004",new string('e',64),new string('a',40)),"Legacy owner replaced");
        Check(store.LatestTask(HostTaskJournal.WorkspaceId(otherRepo)) is null,"Refused legacy path created canonical task");
        Console.WriteLine("Host adoption workspace bootstrap: metadata-only/no Actor or file writes, exact retry, hash drift rejection, old owner preservation and no completion claim PASS; disposable storage only.");
    }
    private static void Check(bool b,string msg){if(!b)throw new Exception(msg);}
    private static void Reject(Action action,string msg){try{action();}catch(InvalidDataException){return;}catch(InvalidOperationException){return;}throw new Exception(msg);}
}
