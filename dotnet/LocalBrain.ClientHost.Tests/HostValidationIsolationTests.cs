using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using LocalBrain.ClientHost.AgentV2;

internal static class HostValidationIsolationTests
{
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static async Task Reject(Func<Task> action,string name,List<string> checks){
        try{await action();}catch(Exception e)when(e is UnauthorizedAccessException or IOException or InvalidDataException){checks.Add(name);return;}
        throw new Exception("Unexpected acceptance: "+name);
    }
    [SupportedOSPlatform("windows")]
    private static void CollisionControl(HostTaskJournal journal,string input,List<string> checks){
        var run=Path.Combine(journal.Canonical.Root,"validation-runs",Guid.NewGuid().ToString("N")[..12]);Directory.CreateDirectory(run);
        using var hostLease=new FileStream(Path.Combine(run,"host-lease.lock"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
        var profile="LBV."+Guid.NewGuid().ToString("N")[..12];
        var plan=new HostValidationCleanup.Plan(journal.TaskId,run,input,profile,["Local\\"+profile+".restore","Local\\"+profile+".validate"],"fixture");
        var hash=HostValidationCleanup.Prepare(journal,plan);
        var code=WindowsIsolationNative.CreateAppContainerProfile(profile,profile,"Synthetic collision fixture",IntPtr.Zero,0,out var existing);
        if(code<0)Marshal.ThrowExceptionForHR(code);
        try{
            IntPtr rejected=IntPtr.Zero;var owned=true;
            try{HostValidationCleanup.CreateProfile(journal,plan,hash,out rejected,out owned);throw new Exception("Existing profile accepted");}
            catch(COMException ex)when(ex.HResult==unchecked((int)0x800700B7)){Check(!owned,"Existing profile marked as owned");}
            finally{if(rejected!=IntPtr.Zero)WindowsIsolationNative.FreeSid(rejected);}
            code=WindowsIsolationNative.CreateAppContainerProfile(profile,profile,"Synthetic collision fixture",IntPtr.Zero,0,out var repeated);
            if(repeated!=IntPtr.Zero)WindowsIsolationNative.FreeSid(repeated);
            Check(code==unchecked((int)0x800700B7),"Profile collision removed the pre-existing profile");checks.Add("profile_collision_preserves_existing");
        }finally{WindowsIsolationNative.FreeSid(existing);var cleanupCode=WindowsIsolationNative.DeleteAppContainerProfile(profile);if(cleanupCode<0)Marshal.ThrowExceptionForHR(cleanupCode);}
    }
    [SupportedOSPlatform("windows")]
    private static void AmbiguousCreationControl(HostTaskJournal journal,string input,List<string> checks){
        var run=Path.Combine(journal.Canonical.Root,"validation-runs",Guid.NewGuid().ToString("N")[..12]);Directory.CreateDirectory(run);
        using(var lease=new FileStream(Path.Combine(run,"host-lease.lock"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None)){}
        var profile="LBV."+Guid.NewGuid().ToString("N")[..12];
        var plan=new HostValidationCleanup.Plan(journal.TaskId,run,input,profile,["Local\\"+profile+".restore","Local\\"+profile+".validate"],"fixture");var hash=HostValidationCleanup.Prepare(journal,plan);
        var code=WindowsIsolationNative.CreateAppContainerProfile(profile,profile,"Synthetic ambiguous creation fixture",IntPtr.Zero,0,out var fixtureSid);if(code<0)Marshal.ThrowExceptionForHR(code);
        try{
            try{HostValidationCleanup.Recover(journal);throw new Exception("Receipt gap altered ambiguous profile");}
            catch(IOException e)when(e.Message.Contains("ownership is ambiguous",StringComparison.Ordinal)){}
            code=WindowsIsolationNative.CreateAppContainerProfile(profile,profile,"Synthetic ambiguous creation fixture",IntPtr.Zero,0,out var repeated);if(repeated!=IntPtr.Zero)WindowsIsolationNative.FreeSid(repeated);
            Check(code==unchecked((int)0x800700B7),"Recovery removed an ambiguous profile");checks.Add("creation_receipt_gap_preserves_ambiguous_profile");
        }finally{
            // The fixture retained the successful OS creation receipt; the recovery code did not infer ownership.
            HostValidationCleanup.Created(journal,plan,hash,fixtureSid);HostValidationCleanup.Finish(journal,plan,hash,false);WindowsIsolationNative.FreeSid(fixtureSid);
        }
    }
    internal static async Task RunAsync(string root,string sdkRoot,string sdkManifestPath,bool profileProof=false,bool registryProof=false){
        if(registryProof)profileProof=true;
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        root=Path.GetFullPath(root);if(Directory.Exists(root))throw new IOException("Fresh proof root required");
        Directory.CreateDirectory(root);var repo=Path.Combine(root,"repo");Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo,"Fixture.csproj"),"""
            <Project>
              <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType></PropertyGroup>
              <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
              <Target Name="RestoreSummary" BeforeTargets="Restore"><Message Text="Failed: 0, Passed: 7, Skipped: 0, Total: 7" Importance="high" /></Target>
              <Target Name="VSTest"><Message Text="Fixture validation intentionally executes zero tests" Importance="high" /></Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(repo,"Fixture.cs"),"public sealed class Fixture { public int Value => 42; }\n");
        if(profileProof){
            var project=Path.Combine(repo,"Fixture.csproj");var xml=File.ReadAllText(project);
            xml=xml.Replace("<TargetFramework>net10.0</TargetFramework>","<TargetFramework>net10.0-windows</TargetFramework>",StringComparison.Ordinal);
            xml=xml.Replace("</Project>","<ItemGroup><Reference Include=\"Microsoft.Build.Framework\"><HintPath>$(MSBuildBinPath)/Microsoft.Build.Framework.dll</HintPath><Private>false</Private></Reference><Reference Include=\"Microsoft.Build.Utilities.Core\"><HintPath>$(MSBuildBinPath)/Microsoft.Build.Utilities.Core.dll</HintPath><Private>false</Private></Reference></ItemGroup><UsingTask TaskName=\"ProfileFixtureTask\" AssemblyFile=\"$(TargetPath)\" /><Target Name=\"OwnProfileStorageProbe\" AfterTargets=\"Build\"><ProfileFixtureTask /></Target></Project>",StringComparison.Ordinal);
            File.WriteAllText(project,xml);File.WriteAllText(Path.Combine(repo,"Fixture.cs"),registryProof?HostValidationRegistryFixture.Source:HostValidationProfileFixture.Source);
        }
        await AttemptStartingState.GitAsync(repo,["init","--initial-branch=main"]);
        await AttemptStartingState.GitAsync(repo,["add","--","Fixture.csproj","Fixture.cs"]);
        await AttemptStartingState.GitAsync(repo,["-c","user.name=Local Fixture","-c","user.email=fixture@invalid.local","commit","-m","Synthetic isolation fixture"]);
        var head=Encoding.UTF8.GetString(await AttemptStartingState.GitAsync(repo,["rev-parse","HEAD"])).Trim();
        var build=new RequiredTest("dotnet_build","Fixture.csproj");var zeroTest=new RequiredTest("dotnet_test","Fixture.csproj");
        var requested=new AgentTaskRequest("Validate synthetic isolation fixture",["isolated build"],"LOW",false,["Fixture.cs"],[build,zeroTest]);
        var checks=new List<string>();
        await Reject(()=>ToolRouter.RunAsync(repo,"",build),"missing_host_journal",checks);
        using var store=new CanonicalStore(Path.Combine(root,"host"));
        using var journal=new HostTaskJournal(store,repo,"isolated_router_fixture",requested,head);
        await Reject(()=>ToolRouter.RunAsync(repo,"",build),"missing_admission",checks);
        await Reject(()=>ToolRouter.RunAsync(repo,"",new("dotnet_build","Other.csproj")),"unrequested_test",checks);
        await Reject(()=>ToolRouter.RunAsync(root,"",build),"unregistered_source_root",checks);
        Check(!Directory.Exists(Path.Combine(store.Root,"validation-runs")),"Rejected requests created a validation process");
        var input=Path.Combine(store.Root,"validation-inputs","fixture");Directory.CreateDirectory(input);
        Directory.CreateDirectory(Path.Combine(input,"packages"));Directory.CreateDirectory(Path.Combine(input,"offline-feed"));
        var entries=new List<HostValidationIsolation.InputFile>();
        using var manifest=JsonDocument.Parse(File.ReadAllBytes(sdkManifestPath));
        foreach(var f in manifest.RootElement.GetProperty("files").EnumerateArray()){
            var relative=f.GetProperty("path").GetString()!;
            var bytes=File.ReadAllBytes(Path.Combine(sdkRoot,relative));
            Check(bytes.LongLength==f.GetProperty("bytes").GetInt64()&&CanonicalStore.Hash(bytes).Equals(f.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase),"Qualified SDK fixture bytes drifted");
            var destination=Path.Combine(input,"sdk",relative);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.WriteAllBytes(destination,bytes);
            entries.Add(new("sdk/"+relative,bytes.LongLength,CanonicalStore.Hash(bytes)));
        }
        foreach(var f in Directory.GetFiles(repo,"*",SearchOption.TopDirectoryOnly)){
            var bytes=File.ReadAllBytes(f);var relative="repo/"+Path.GetFileName(f);var destination=Path.Combine(input,relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.WriteAllBytes(destination,bytes);entries.Add(new(relative,bytes.LongLength,CanonicalStore.Hash(bytes)));
        }
        var files=entries.ToArray();
        await Reject(()=>HostValidationIsolation.AdmitDotnetAsync(journal,repo,build,input,files.Where(f=>f.Path!="repo/Fixture.cs").ToArray(),"10.0.303"),"incomplete_manifest",checks);
        var missingSource=Path.Combine(input,"repo","Fixture.cs");var missingBytes=File.ReadAllBytes(missingSource);File.Delete(missingSource);
        await Reject(()=>HostValidationIsolation.AdmitDotnetAsync(journal,repo,build,input,files.Where(f=>f.Path!="repo/Fixture.cs").ToArray(),"10.0.303"),"source_and_manifest_both_incomplete",checks);
        File.WriteAllBytes(missingSource,missingBytes);
        var admission=await HostValidationIsolation.AdmitDotnetAsync(journal,repo,build,input,files,"10.0.303");
        var changed=Path.Combine(input,"sdk","LICENSE.txt");var original=File.ReadAllBytes(changed);File.AppendAllText(changed,"tamper");
        await Reject(()=>ToolRouter.RunAsync(repo,"",build),"changed_sdk",checks);File.WriteAllBytes(changed,original);
        var extra=Path.Combine(input,"extra.bin");File.WriteAllText(extra,"extra");
        await Reject(()=>ToolRouter.RunAsync(repo,"",build),"additional_input",checks);File.Delete(extra);
        var source=Path.Combine(repo,"Fixture.cs");var originalSource=File.ReadAllBytes(source);File.AppendAllText(source,"// changed\n");
        await Reject(()=>ToolRouter.RunAsync(repo,"",build),"source_drift",checks);File.WriteAllBytes(source,originalSource);
        Check(!Directory.Exists(Path.Combine(store.Root,"validation-runs")),"Rejected admission created a validation process");
        var result=await ToolRouter.RunAsync(repo,"",build);
        File.WriteAllText(Path.Combine(root,"build-result.json"),JsonSerializer.Serialize(result));
        Check(result.Passed&&result.ExitCode==0,"Actual isolated ToolRouter build failed");checks.Add("actual_isolated_build");
        if(profileProof)HostValidationProfileFixture.CheckRoutedBuild(root,checks);
        if(registryProof)HostValidationRegistryFixture.CheckRoutedBuild(root,checks);
        await HostValidationIsolation.AdmitDotnetAsync(journal,repo,zeroTest,input,files,"10.0.303");
        var empty=await ToolRouter.RunAsync(repo,"",zeroTest);
        Check(!empty.Passed&&empty.ExitCode==0&&empty.ExecutedTests is null&&empty.FailureCodes.Contains("no_tests_detected"),"Restore-only summary incorrectly passed zero-test validation");checks.Add("restore_summary_cannot_qualify_tests");
        File.WriteAllText(Path.Combine(root,"zero-tests-result.json"),JsonSerializer.Serialize(empty));
        foreach(var run in Directory.GetDirectories(Path.Combine(store.Root,"validation-runs"))){
            Check(JsonDocument.Parse(File.ReadAllBytes(Path.Combine(run,"profile-cleanup-result.json"))).RootElement.GetProperty("deleted").GetBoolean(),"Isolation profile not deleted");
            foreach(var capture in Directory.GetDirectories(run).Where(p=>Path.GetFileName(p) is "restore" or "validate")){
                using var trace=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(capture,"capture-result.json")));
                Check(trace.RootElement.GetProperty("reason").GetString()=="completed","Expected completed captures");
            }
        }
        CollisionControl(journal,input,checks);AmbiguousCreationControl(journal,input,checks);
        var beforeReplay=store.EventIds(journal.TaskId).Length;HostValidationCleanup.Recover(journal);Check(beforeReplay==store.EventIds(journal.TaskId).Length,"Cleanup replay duplicated evidence");checks.Add("canonical_cleanup_replay");
        File.WriteAllText(Path.Combine(root,"proof.json"),JsonSerializer.Serialize(new{status="PASS_ACTUAL_ROUTER_SYNTHETIC_BUILD_AND_NEGATIVES",checks,head,admission_hash=admission,input_file_count=files.Length,canonical_events=store.EventIds(journal.TaskId).Length,production_qualified=false}));
        Console.WriteLine(JsonSerializer.Serialize(new{status="PASS",checks,proof=root}));
    }
}
