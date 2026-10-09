using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class HostValidationStorageTests
{
    internal static async Task RunAsync(string root,string sdkManifestPath,bool administrativeRegistration=false)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        root=Path.GetFullPath(root);if(File.Exists(Path.Combine(root,"proof.json")))throw new IOException("Fresh storage proof required");
        var repo=Path.Combine(root,"repo");Directory.CreateDirectory(repo);
        await File.WriteAllTextAsync(Path.Combine(repo,"Fixture.csproj"),"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(repo,"Fixture.cs"),"public static class Fixture { public const int Value = 42; }\n");
        await File.WriteAllTextAsync(Path.Combine(repo,".gitignore"),".localbrain/\n");
        await AttemptStartingState.GitAsync(repo,["init"]);await AttemptStartingState.GitAsync(repo,["add","."]);
        await AttemptStartingState.GitAsync(repo,["-c","user.name=LocalBrain fixture","-c","user.email=fixture@invalid","commit","-m","storage fixture"]);
        var head=Encoding.UTF8.GetString(await AttemptStartingState.GitAsync(repo,["rev-parse","HEAD"])).Trim();
        var test=new RequiredTest("dotnet_build","Fixture.csproj");
        var request=new AgentTaskRequest("Ordinary temporary storage validation",["actual build and temporary cleanup"],"LOW",false,["Fixture.cs"],[test]);
        using var store=new CanonicalStore(Path.Combine(root,"host"));using var journal=new HostTaskJournal(store,repo,"personal_storage_fixture",request,head);journal.EnablePersonalValidation();
        var checks=new List<string>();void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);}
        Task Reject(Action action,string name){try{action();}catch(Exception e)when(e is UnauthorizedAccessException or IOException){checks.Add(name);return Task.CompletedTask;}throw new Exception("Unexpected storage acceptance: "+name);}
        HostValidationStorage.CheckSpace(journal,1024);checks.Add("actual_host_free_space_preflight");
        await Reject(()=>HostValidationStorage.CheckSpace(journal,1024,1),"synthetic_low_space_refused");
        var orphan=Path.Combine(store.Root,"validation-inputs","abcdef000001");Directory.CreateDirectory(orphan);File.WriteAllText(Path.Combine(orphan,"keep.txt"),"unowned");
        await Reject(()=>HostValidationStorage.Retire(journal,orphan),"unallocated_input_preserved");Check(File.Exists(Path.Combine(orphan,"keep.txt")),"unowned_bytes_unchanged");
        var unused=Path.Combine(store.Root,"validation-inputs","abcdef000002");Directory.CreateDirectory(unused);HostValidationStorage.Allocated(journal,unused);File.WriteAllText(Path.Combine(unused,"temporary.txt"),"owned copy");
        var evidenceHash=store.PutEvidence(Encoding.UTF8.GetBytes("must retain canonical evidence"));HostValidationStorage.Retire(journal,unused);
        Check(!Directory.Exists(unused) && store.ReadEvidence(evidenceHash).Length>0,"unused_owned_input_retired_evidence_preserved");
        using(var pending=new HostTaskJournal(store,repo,"pending_storage_fixture",request,head))
        {
            pending.EnablePersonalValidation();var input=Path.Combine(store.Root,"validation-inputs","abcdef000003");Directory.CreateDirectory(input);HostValidationStorage.Allocated(pending,input);
            var run=Path.Combine(store.Root,"validation-runs","abcdef000003");Directory.CreateDirectory(run);const string profile="LBV.abcdef000003";
            HostValidationCleanup.Prepare(pending,new(pending.TaskId,run,input,profile,["Local\\"+profile+".restore","Local\\"+profile+".validate"],"synthetic_unused_profile"));
            await Reject(()=>HostValidationStorage.Retire(pending,input),"incomplete_child_cleanup_retains_input");Check(Directory.Exists(input),"pending_input_not_deleted");
        }
        var publisher=Path.Combine(store.Root,"validation-inputs","publisher");
        using var manifest=JsonDocument.Parse(File.ReadAllBytes(sdkManifestPath));
        var sdkFiles=manifest.RootElement.GetProperty("files").Deserialize<HostValidationIsolation.InputFile[]>()!;
        var policy=new HostValidationPolicy.Policy(HostTaskJournal.RepositoryId(repo),"10.0.303",new(Path.Combine(publisher,"sdk"),sdkFiles),new(Path.Combine(publisher,"packages"),[]),new(Path.Combine(publisher,"offline-feed"),[]));
        var registration=administrativeRegistration
            ?(await HostValidationRegistration.RegisterAsync(store,repo,policy)).PolicyHash
            :HostValidationPolicy.Register(journal,policy);
        if(administrativeRegistration)checks.Add("host_administrative_registration_precedes_real_sdk_route");
        var build=await HostValidationPolicy.RunAsync(journal,repo,"",test);
        Check(build.Passed,"actual_personal_publisher_build_pass");
        await File.WriteAllTextAsync(Path.Combine(repo,"Fixture.cs"),"public static class Fixture { syntax error }\n");
        var failure=await HostValidationPolicy.RunAsync(journal,repo,"",test);
        Check(!failure.Passed && failure.ExitCode!=0,"actual_compile_failure_not_passed");
        var retired=store.EventIds(journal.TaskId).Select(id=>store.ReadTaskEvent(journal.TaskId,id)).Where(e=>e.Input.EventType=="personal_validation_storage_retired")
            .Select(e=>JsonSerializer.Deserialize<JsonElement>(store.ReadEvidence(e.Input.EvidenceRefs.Single()))).ToArray();
        Check(retired.Length==3 && retired.All(r=>!Directory.Exists(r.GetProperty("input_root").GetString()!)),"success_and_failure_generated_inputs_retired");
        Check(retired.SelectMany(r=>r.GetProperty("scratch").EnumerateArray()).All(p=>!Directory.Exists(p.GetString()!)),"completed_run_scratch_retired");
        Check(File.Exists(Path.Combine(publisher,"sdk","dotnet.exe")) && Directory.Exists(orphan) && store.ReadEvidence(evidenceHash).Length>0,"publisher_unowned_and_evidence_preserved");
        var proof=new{status="PASS_PERSONAL_PRACTICAL_STORAGE_FIXED_DOTNET",checks,registration,actual_build=build,actual_compile_failure=failure,
            canonical_root=store.Root,retired_inputs=retired,task_id=journal.TaskId,hard_scratch_quota=false,production_changed=false};
        File.WriteAllText(Path.Combine(root,"proof.json"),JsonSerializer.Serialize(proof));Console.WriteLine(JsonSerializer.Serialize(proof));
    }
}
