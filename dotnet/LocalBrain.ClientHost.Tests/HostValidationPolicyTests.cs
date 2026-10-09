using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class HostValidationPolicyTests
{
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    private static async Task Reject(Func<Task> action,string name,List<string> checks){
        try{await action();}catch(Exception e)when(e is UnauthorizedAccessException or InvalidDataException or IOException){checks.Add(name);return;}
        throw new Exception("Unexpected publisher acceptance: "+name);
    }
    internal static async Task RunAsync(string root,string sdkManifestPath){
        root=Path.GetFullPath(root);var proof=Path.Combine(root,"publisher-proof.json");if(File.Exists(proof))throw new IOException("Fresh policy fixture required");
        var repo=Path.Combine(root,"repo");var head=Encoding.UTF8.GetString(await AttemptStartingState.GitAsync(repo,["rev-parse","HEAD"])).Trim();
        var build=new RequiredTest("dotnet_build","Fixture.csproj");var request=new AgentTaskRequest("Validate synthetic isolation fixture",["isolated build"],"LOW",false,["Fixture.cs"],[build]);
        using var store=new CanonicalStore(Path.Combine(root,"host"));using var journal=new HostTaskJournal(store,repo,"publisher_policy_fixture",request,head);
        var checks=new List<string>();await Reject(()=>HostValidationPolicy.PrepareDotnetAsync(journal,repo,build),"missing_host_publisher",checks);
        var input=Path.Combine(store.Root,"validation-inputs","fixture");using var manifest=JsonDocument.Parse(File.ReadAllBytes(sdkManifestPath));
        var sdkFiles=manifest.RootElement.GetProperty("files").Deserialize<HostValidationIsolation.InputFile[]>()!;
        var policy=new HostValidationPolicy.Policy(HostTaskJournal.RepositoryId(repo),"10.0.303",new(Path.Combine(input,"sdk"),sdkFiles),new(Path.Combine(input,"packages"),[]),new(Path.Combine(input,"offline-feed"),[]));
        await Reject(()=>{HostValidationPolicy.Register(journal,policy with{RepositoryId="repo_other"});return Task.CompletedTask;},"publisher_repository_mismatch",checks);
        var registration=HostValidationPolicy.Register(journal,policy);checks.Add("canonical_host_registration");
        await Reject(()=>HostValidationPolicy.PrepareDotnetAsync(journal,repo,new("dotnet_build","Other.csproj")),"publisher_unrequested_test",checks);
        await Reject(()=>HostValidationPolicy.PrepareDotnetAsync(journal,root,build),"publisher_other_source_root",checks);
        var config=Path.Combine(store.Root,"validation-policy",policy.RepositoryId+".json");var originalReference=File.ReadAllBytes(config);
        var changedReference=Encoding.UTF8.GetString(originalReference).Replace(registration,new string('f',64),StringComparison.Ordinal);File.WriteAllText(config,changedReference);
        await Reject(()=>HostValidationPolicy.PrepareDotnetAsync(journal,repo,build),"publisher_reference_tamper",checks);File.WriteAllBytes(config,originalReference);
        HostValidationPolicy.Register(journal,policy);var currentReference=File.ReadAllBytes(config);File.WriteAllBytes(config,originalReference);
        await Reject(()=>HostValidationPolicy.PrepareDotnetAsync(journal,repo,build),"publisher_old_registration_replay",checks);File.WriteAllBytes(config,currentReference);
        using(var old=JsonDocument.Parse(originalReference))using(var current=JsonDocument.Parse(currentReference)){
            using var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(store.Root,"canonical.db"));db.Open();
            void RegistryEvent(string id){using var command=db.CreateCommand();command.CommandText="UPDATE validation_publisher_registry SET event_id=$id WHERE repository_id=$repo";command.Parameters.AddWithValue("$id",id);command.Parameters.AddWithValue("$repo",policy.RepositoryId);command.ExecuteNonQuery();}
            RegistryEvent(old.RootElement.GetProperty("EventId").GetString()!);
            await Reject(()=>HostValidationPolicy.PrepareDotnetAsync(journal,repo,build),"publisher_registry_rollback_against_audit",checks);
            RegistryEvent(current.RootElement.GetProperty("EventId").GetString()!);
        }
        using(journal.Canonical.AcquireValidationPublisher(policy.RepositoryId,registration)){
            await Reject(()=>{HostValidationPolicy.Register(journal,policy);return Task.CompletedTask;},"publisher_change_during_dispatch",checks);
        }
        var license=Path.Combine(input,"sdk","LICENSE.txt");var originalLicense=File.ReadAllBytes(license);File.AppendAllText(license,"tamper");
        await Reject(()=>HostValidationPolicy.PrepareDotnetAsync(journal,repo,build),"publisher_sdk_mutation",checks);File.WriteAllBytes(license,originalLicense);
        var before=Directory.GetDirectories(Path.Combine(store.Root,"validation-inputs")).Length;
        var result=await HostValidationPolicy.RunAsync(journal,repo,"",build);
        Check(Directory.GetDirectories(Path.Combine(store.Root,"validation-inputs")).Length==before+1,"Host did not create a fresh prepared input");
        Check(result.Passed,"Publisher-prepared actual router build failed");checks.Add("host_preparation_real_router_build");
        File.WriteAllText(proof,JsonSerializer.Serialize(new{status="PASS_HOST_PUBLISHER_PREPARATION_FIXED_BUILD_ONLY",checks,registration_hash=registration,actual_router_result=result,production_qualified=false,all_task_storage_budget_qualified=false}));
        Console.WriteLine(JsonSerializer.Serialize(new{status="PASS",checks,proof}));
    }
}
