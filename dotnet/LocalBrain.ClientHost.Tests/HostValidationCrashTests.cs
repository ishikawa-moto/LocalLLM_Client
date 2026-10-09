using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

[SupportedOSPlatform("windows")]
internal static class HostValidationCrashTests
{
    private static AgentTaskRequest Request()=>new("Validate synthetic isolation fixture",["isolated build"],"LOW",false,["Fixture.cs"],[new("dotnet_build","Fixture.csproj"),new("dotnet_test","Fixture.csproj")]);
    private static string Dacl(string input)=>new DirectoryInfo(input).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    internal static async Task OwnerAsync(string root,string stage){
        var repo=Path.Combine(root,"repo");var head=Encoding.UTF8.GetString(await AttemptStartingState.GitAsync(repo,["rev-parse","HEAD"])).Trim();
        using var store=new CanonicalStore(Path.Combine(root,"host"));using var journal=new HostTaskJournal(store,repo,"isolation_crash_"+stage,Request(),head);
        var input=Path.Combine(store.Root,"validation-inputs","fixture");
        var run=Path.Combine(store.Root,"validation-runs",Guid.NewGuid().ToString("N")[..12]);var box=Path.Combine(run,"box");var scratch=Path.Combine(box,"scratch");Directory.CreateDirectory(scratch);
        var payload=Path.Combine(box,"payload");Directory.CreateDirectory(payload);
        foreach(var file in Directory.GetFiles(Path.GetDirectoryName(typeof(HostValidationCrashTests).Assembly.Location)!))File.Copy(file,Path.Combine(payload,Path.GetFileName(file)));
        using var hostLease=new FileStream(Path.Combine(run,"host-lease.lock"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
        var profile="LBV."+Guid.NewGuid().ToString("N")[..12];
        var plan=new HostValidationCleanup.Plan(journal.TaskId,run,input,profile,["Local\\"+profile+".restore","Local\\"+profile+".validate"],Dacl(input));
        var hash=HostValidationCleanup.Prepare(journal,plan);
        HostValidationCleanup.CreateProfile(journal,plan,hash,out var sid,out var created);
        try{
            var principal=new SecurityIdentifier(sid);HostValidationCleanup.Grant(input,principal,false);HostValidationCleanup.Grant(box,principal,false);HostValidationCleanup.Grant(scratch,principal,true);WindowsIsolationNative.LowIntegrity(scratch);
            File.WriteAllText(Path.Combine(root,"crash-owner-"+stage+".json"),JsonSerializer.Serialize(new{plan_hash=hash,plan,created}));
            if(stage=="created"){await Task.Delay(Timeout.Infinite);return;}
            var sdk=Path.Combine(input,"sdk");var windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var env=new Dictionary<string,string>{{"SystemRoot",windows},{"PATH",Path.Combine(windows,"System32")},{"DOTNET_ROOT",sdk},{"DOTNET_ROOT_X64",sdk},{"DOTNET_EnableDiagnostics","0"},{"TEMP",scratch},{"TMP",scratch},{"LOCALAPPDATA",scratch},{"APPDATA",scratch},{"USERPROFILE",scratch}};
            var captureRoot=Path.Combine(run,"capture");Directory.CreateDirectory(captureRoot);
            var result=WindowsIsolationNative.Capture(Path.Combine(sdk,"dotnet.exe"),[Path.Combine(payload,"LocalBrain.ClientHost.Tests.dll"),"--isolation-crash-target",scratch],scratch,sid,env,65536,180000,captureRoot,CancellationToken.None,plan.Jobs[0]);
            throw new Exception("Crash owner unexpectedly returned: "+JsonSerializer.Serialize(result));
        }finally{if(created)HostValidationCleanup.Finish(journal,plan,hash,false);if(sid!=IntPtr.Zero)WindowsIsolationNative.FreeSid(sid);}
    }
    internal static async Task RunAsync(string root){
        root=Path.GetFullPath(root);var input=Path.Combine(root,"host","validation-inputs","fixture");var original=Dacl(input);
        var proof=new List<object>();
        foreach(var stage in new[]{"created","running"}){
            var receipt=Path.Combine(root,"crash-owner-"+stage+".json");if(File.Exists(receipt))throw new IOException("Fresh crash fixture required");
            var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"dotnet","dotnet.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{typeof(HostValidationCrashTests).Assembly.Location,"--isolation-crash-owner",root,stage})start.ArgumentList.Add(arg);
            using var owner=Process.Start(start)??throw new IOException("Crash owner did not start");
            var stdout=owner.StandardOutput.ReadToEndAsync();var stderr=owner.StandardError.ReadToEndAsync();
            HostValidationCleanup.Plan? plan=null;
            try{
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while(true){
                    deadline.Token.ThrowIfCancellationRequested();if(owner.HasExited)throw new Exception("Owner exited before requested crash boundary: "+await stderr);
                    if(File.Exists(receipt)){
                        using var r=JsonDocument.Parse(File.ReadAllBytes(receipt));plan=r.RootElement.GetProperty("plan").Deserialize<HostValidationCleanup.Plan>()!;
                        if(stage=="created"||File.Exists(Path.Combine(plan.Run,"box","scratch","child-ready.json")))break;
                    }
                    await Task.Delay(100,deadline.Token);
                }
                var observedLive=stage=="running"&&!WindowsIsolationNative.JobAbsentOrEmpty(plan!.Jobs[0]);
                if(stage=="running"&&!observedLive)throw new Exception("Actual isolated Job was not live at Host termination");
                owner.Kill(false);await owner.WaitForExitAsync();
                var cleanupDeadline=Stopwatch.StartNew();while(plan!.Jobs.Any(j=>!WindowsIsolationNative.JobAbsentOrEmpty(j))){if(cleanupDeadline.Elapsed>TimeSpan.FromSeconds(10))throw new IOException("Host death did not terminate owned Job");await Task.Delay(100);}
                var repo=Path.Combine(root,"repo");var head=Encoding.UTF8.GetString(await AttemptStartingState.GitAsync(repo,["rev-parse","HEAD"])).Trim();
                using var store=new CanonicalStore(Path.Combine(root,"host"));using var journal=new HostTaskJournal(store,repo,"isolation_crash_"+stage,Request(),head);
                HostValidationCleanup.Recover(journal);var before=store.EventIds(journal.TaskId).Length;HostValidationCleanup.Recover(journal);
                if(before!=store.EventIds(journal.TaskId).Length||Dacl(input)!=original)throw new Exception("Recovery replay changed canonical state or source DACL");
                using var cleanup=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(plan.Run,"profile-cleanup-result.json")));
                if(!cleanup.RootElement.GetProperty("deleted").GetBoolean()||!cleanup.RootElement.GetProperty("recovery").GetBoolean())throw new Exception("Crash recovery did not remove owned profile");
                var code=WindowsIsolationNative.CreateAppContainerProfile(plan.Profile,plan.Profile,"Synthetic absence check",IntPtr.Zero,0,out var absence);
                if(code<0)Marshal.ThrowExceptionForHR(code);WindowsIsolationNative.FreeSid(absence);code=WindowsIsolationNative.DeleteAppContainerProfile(plan.Profile);if(code<0)Marshal.ThrowExceptionForHR(code);
                proof.Add(new{stage,host_terminated=true,actual_job_live_before_host_loss=observedLive,owned_jobs_after_host_loss=0,profile_removed_and_recreated=true,input_dacl_restored=true,replay_events_unchanged=true,run=plan.Run});
            }finally{if(!owner.HasExited){owner.Kill(false);await owner.WaitForExitAsync();}File.WriteAllText(Path.Combine(root,"crash-owner-"+stage+".stdout.txt"),await stdout);File.WriteAllText(Path.Combine(root,"crash-owner-"+stage+".stderr.txt"),await stderr);}
        }
        File.WriteAllText(Path.Combine(root,"crash-proof.json"),JsonSerializer.Serialize(new{status="PASS_TWO_OWNED_PROFILE_HOST_LOSS_BOUNDARIES",proof,creation_receipt_gap_qualified=false,production_qualified=false}));
        Console.WriteLine("PASS two owned-profile Host-loss boundaries; creation receipt gap remains unqualified");
    }
}
