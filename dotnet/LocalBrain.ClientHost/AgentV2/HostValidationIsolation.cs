using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.Versioning;

namespace LocalBrain.ClientHost.AgentV2;

// Host canonical admission is mandatory. No project setting or model-supplied command can bypass it.
internal static class HostValidationIsolation
{
    internal sealed record InputFile([property:JsonPropertyName("path")] string Path,
        [property:JsonPropertyName("bytes")] long Bytes,[property:JsonPropertyName("sha256")] string Sha256);
    internal sealed record RescueBinding(string TicketHash,string CandidateHash);
    internal sealed record DotnetAdmission(string TaskId,string WindowsRoot,string Kind,string? Target,
        string SourceStateHash,string InputRoot,InputFile[] Files,string SdkDirectory,string SdkVersion,
        string ProjectPath,string PackageDirectory,string FeedDirectory,RescueBinding? Rescue=null);
    private static readonly SemaphoreSlim execution=new(1,1);
    private const int CombinedLimit=65536;
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private static string Relative(string value) {
        if(string.IsNullOrWhiteSpace(value)||Path.IsPathRooted(value)||value.Contains(':')||
            value.Split('/',(char)92).Any(p=>p is "" or "." or ".."))throw new InvalidDataException("Invalid admission relative path");
        return value.Replace((char)92,'/');
    }
    private static string Under(string root,string relative)=>Path.Combine(root,Relative(relative));
    private static void RequireWorkspace(HostTaskJournal journal,string root) {
        CanonicalStore.GuardPath(root);
        if(!string.Equals(root,Path.GetFullPath(journal.WindowsRoot),StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Rescue validation requires a ticket-bound isolated admission; workspace registration alone is insufficient");
    }
    internal static async Task RequireWorkspaceAsync(HostTaskJournal journal,string root,RescueBinding? rescue,CancellationToken token){
        CanonicalStore.GuardPath(root);
        if(string.Equals(root,Path.GetFullPath(journal.WindowsRoot),StringComparison.OrdinalIgnoreCase)){
            if(rescue is not null)throw new UnauthorizedAccessException("Rescue binding cannot authorize main validation");return;
        }
        if(rescue is null)throw new UnauthorizedAccessException("Independent Rescue requires exact ticket and candidate binding");
        await new HostRescue(journal).RequireValidationSourceAsync(rescue.TicketHash,rescue.CandidateHash,root,token);
    }
    private static void RequireRequested(HostTaskJournal journal,RequiredTest test) {
        var request=AgentTaskRequest.Parse(journal.Canonical.TaskRequest(journal.TaskId));
        if(!request.RequiredTests.Any(t=>t==test))throw new UnauthorizedAccessException("Validation is not requested by canonical task");
    }
    private static void VerifyFiles(DotnetAdmission plan,HostValidationInputLease lease) {
        if(plan.Files.Length is <1 or >10000||plan.Files.Sum(f=>f.Bytes)>1536L*1024*1024)
            throw new InvalidDataException("Admission exceeds Host file/byte budget");
        var expected=plan.Files.Select(f=>Relative(f.Path)).Order(StringComparer.Ordinal).ToArray();
        if(expected.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=expected.Length)throw new InvalidDataException("Duplicate admission paths");
        var found=new List<string>();var pending=new Stack<string>();pending.Push(plan.InputRoot);
        while(pending.Count>0){var dir=pending.Pop();lease.LockDirectory(dir);foreach(var path in Directory.EnumerateFileSystemEntries(dir)){
            var attrs=File.GetAttributes(path);if((attrs&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked admission entry");
            if((attrs&FileAttributes.Directory)!=0)pending.Push(path);else found.Add(Path.GetRelativePath(plan.InputRoot,path).Replace((char)92,'/'));
        }}
        if(!found.Order(StringComparer.Ordinal).SequenceEqual(expected,StringComparer.Ordinal))throw new IOException("Admission file set changed");
        foreach(var entry in plan.Files){
            var relative=Relative(entry.Path);
            if(System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(relative),@"^(?:\.env(?:\..*)?|id_(?:rsa|ed25519)|.*\.(?:pem|pfx|p12|key))$",System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new UnauthorizedAccessException("Secret/credential input is not admitted");
            if(entry.Bytes is <0 or >128L*1024*1024||entry.Sha256.Length!=64)throw new InvalidDataException("Invalid input byte/hash budget");
            var bytes=lease.Read(Under(plan.InputRoot,relative));
            if(bytes.LongLength!=entry.Bytes||!Hash(bytes).Equals(entry.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Admission bytes changed");
        }
    }
    internal static async Task<string> AdmitDotnetAsync(HostTaskJournal journal,string windowsRoot,RequiredTest test,
        string inputRoot,InputFile[] files,string sdkVersion,CancellationToken token=default,RescueBinding? rescue=null) {
        var root=Path.GetFullPath(windowsRoot);await RequireWorkspaceAsync(journal,root,rescue,token);RequireRequested(journal,test);
        if(test.Kind is not ("dotnet_build" or "dotnet_test")||test.Target is null)throw new InvalidDataException("Only named .NET admission is implemented");
        var target=Relative(test.Target);if(Path.GetExtension(target)!=".csproj")throw new InvalidDataException("Project target required");
        inputRoot=Path.GetFullPath(inputRoot);var parent=Path.Combine(journal.Canonical.Root,"validation-inputs");
        if(Path.GetDirectoryName(inputRoot)!=parent)throw new UnauthorizedAccessException("Input tree must be a fresh Host-owned admission under canonical root");
        if(!System.Text.RegularExpressions.Regex.IsMatch(sdkVersion,@"^\d+\.\d+\.\d+$"))throw new InvalidDataException("Exact stable SDK version required");
        var snapshot=await AttemptStartingState.CaptureAsync(root,null,token);
        if(snapshot.Head!=journal.ExpectedHead)throw new UnauthorizedAccessException("Admission HEAD differs from canonical task");
        var sourceHash=journal.Canonical.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(snapshot));
        var plan=new DotnetAdmission(journal.TaskId,root,test.Kind,test.Target,sourceHash,inputRoot,files,
            "sdk",sdkVersion,"repo/"+target,"packages","offline-feed",rescue);
        using var lease=new HostValidationInputLease();VerifyFiles(plan,lease);
        var copiedSource=files.Where(f=>Relative(f.Path).StartsWith("repo/",StringComparison.Ordinal)).ToArray();
        if(!copiedSource.Select(f=>Relative(f.Path)[5..]).Order(StringComparer.Ordinal).SequenceEqual(
            snapshot.Files.Where(f=>f.Hash is not null).Select(f=>f.Path.Replace((char)92,'/')).Order(StringComparer.Ordinal),StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Copied source must include the complete captured non-ignored file set");
        foreach(var file in copiedSource){
            var source=snapshot.Files.SingleOrDefault(f=>f.Path.Replace((char)92,'/')==Relative(file.Path)[5..]);
            if(source?.Hash is null||!source.Hash.Equals(file.Sha256,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Copied source differs from actual starting state");
        }
        if(!files.Any(f=>Relative(f.Path)==plan.ProjectPath)||!files.Any(f=>Relative(f.Path)=="sdk/dotnet.exe")||
            !files.Any(f=>Relative(f.Path).StartsWith("sdk/sdk/"+sdkVersion+"/",StringComparison.Ordinal)))throw new InvalidDataException("Incomplete executable/project/SDK admission");
        var hash=journal.Canonical.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(plan));
        var workspace=rescue is null?HostTaskJournal.WorkspaceId(root):new HostRescue(journal).Load(rescue.TicketHash).WorkspaceId;
        journal.Canonical.Append(new(journal.TaskId,workspace,"windows_host",Guid.NewGuid().ToString("N"),
            "validation_isolation_admitted","host_validation","host_verified",[hash,sourceHash],JsonSerializer.SerializeToElement(new{plan_hash=hash})));
        return hash;
    }
    private static async Task<DotnetAdmission> LoadAsync(HostTaskJournal journal,string root,RequiredTest test,CancellationToken token) {
        RequireRequested(journal,test);
        foreach(var id in journal.Canonical.EventIds(journal.TaskId).Reverse()){
            var e=journal.Canonical.ReadTaskEvent(journal.TaskId,id);
            if(e.Input.EventType!="validation_isolation_admitted"||e.Input.Producer!="windows_host"||e.Input.VerificationStatus!="host_verified")continue;
            var hash=e.Input.Metadata.GetProperty("plan_hash").GetString()!;
            if(!e.Input.EvidenceRefs.Contains(hash,StringComparer.Ordinal))throw new UnauthorizedAccessException("Admission evidence is not bound to Host event");
            var plan=JsonSerializer.Deserialize<DotnetAdmission>(journal.Canonical.ReadEvidence(hash))??throw new InvalidDataException("Invalid admission");
            if(plan.TaskId==journal.TaskId&&plan.WindowsRoot==root&&plan.Kind==test.Kind&&plan.Target==test.Target){
                await RequireWorkspaceAsync(journal,root,plan.Rescue,token);
                if(Path.GetDirectoryName(plan.InputRoot)!=Path.Combine(journal.Canonical.Root,"validation-inputs"))throw new UnauthorizedAccessException("Admission input escaped Host root");
                return plan;
            }
        }
        throw new UnauthorizedAccessException("No Host-canonical isolated validation admission; unrestricted fallback is forbidden");
    }
    internal static async Task<ToolRouter.TestResult> RunAsync(string windowsRoot,string wslRoot,RequiredTest test,CancellationToken token) {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Isolation must be owned by Windows Host");
        token.ThrowIfCancellationRequested();
        var journal=HostTaskJournal.Current??throw new UnauthorizedAccessException("Validation needs an owning canonical Windows Host journal");
        var root=Path.GetFullPath(windowsRoot);
        if(journal.PersonalValidationEnabled && test.Kind is ("git_diff_check" or "npm_test"))
            return await HostPersonalValidation.RunAsync(journal,root,wslRoot,test,null,token);
        if(test.Kind is not ("dotnet_build" or "dotnet_test")){
            RequireWorkspace(journal,root);RequireRequested(journal,test);
            journal.Record("validation_isolation_rejected",new{test.Kind,reason="sealed_route_not_registered"},"host_verified");
            throw new UnauthorizedAccessException("Sealed validation route is not implemented; unrestricted fallback is forbidden");
        }
        var plan=await LoadAsync(journal,root,test,token);await execution.WaitAsync(token);
        try{HostValidationCleanup.Recover(journal);return await RunDotnetAsync(journal,plan,test,token);}finally{execution.Release();}
    }
    [SupportedOSPlatform("windows")]
    private static async Task<ToolRouter.TestResult> RunDotnetAsync(HostTaskJournal journal,DotnetAdmission plan,RequiredTest test,CancellationToken token) {
        using var lease=new HostValidationInputLease();VerifyFiles(plan,lease);
        await RequireWorkspaceAsync(journal,plan.WindowsRoot,plan.Rescue,token);
        var expected=JsonSerializer.Deserialize<AttemptStartingState.Snapshot>(journal.Canonical.ReadEvidence(plan.SourceStateHash))!;
        var before=await AttemptStartingState.CaptureAsync(plan.WindowsRoot,null,token);
        if(AttemptStartingState.Hash(before)!=AttemptStartingState.Hash(expected))throw new UnauthorizedAccessException("Source changed since Host admission");
        foreach(var file in plan.Files.Where(f=>Relative(f.Path).StartsWith("repo/",StringComparison.Ordinal))){
            var original=Under(plan.WindowsRoot,Relative(file.Path)[5..]);
            if(!Hash(lease.Read(original)).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Original source admission changed");
        }
        var run=Path.Combine(journal.Canonical.Root,"validation-runs",Guid.NewGuid().ToString("N")[..12]);
        CanonicalStore.GuardPath(run);if(Directory.Exists(run))throw new IOException("Fresh validation run required");
        var box=Path.Combine(run,"box");var scratch=Path.Combine(box,"scratch");Directory.CreateDirectory(scratch);CanonicalStore.GuardPath(scratch);
        var sdk=Under(plan.InputRoot,plan.SdkDirectory);var exe=Path.Combine(sdk,"dotnet.exe");
        var feed=Under(plan.InputRoot,plan.FeedDirectory);var packages=Under(plan.InputRoot,plan.PackageDirectory);
        Directory.CreateDirectory(Path.Combine(scratch,"artifacts"));
        File.WriteAllText(Path.Combine(box,"global.json"),JsonSerializer.Serialize(new{sdk=new{version=plan.SdkVersion,rollForward="disable",allowPrerelease=false,paths=new[]{"$host$"}}}));
        var nuget=Path.Combine(box,"NuGet.Config");File.WriteAllText(nuget,"<configuration><packageSources><clear/><add key=\"host-offline\" value=\""+System.Security.SecurityElement.Escape(feed)+"\"/></packageSources></configuration>");
        var profile="LBV."+Guid.NewGuid().ToString("N")[..12];IntPtr sid=IntPtr.Zero;var created=false;
        using var hostLease=new FileStream(Path.Combine(run,"host-lease.lock"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
        var cleanup=new HostValidationCleanup.Plan(journal.TaskId,run,plan.InputRoot,profile,["Local\\"+profile+".restore","Local\\"+profile+".validate"],
            new DirectoryInfo(plan.InputRoot).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        var cleanupHash=HostValidationCleanup.Prepare(journal,cleanup);
        var watch=Stopwatch.StartNew();var captures=new List<JsonElement>();using var output=new MemoryStream();using var error=new MemoryStream();
        string reason="completed";int exit=0;byte[] validationOutput=[];
        HostValidationProfileRegistry.Lease? registryLease=null;
        try{
            HostValidationCleanup.CreateProfile(journal,cleanup,cleanupHash,out sid,out created);
            var profileSeal=HostValidationProfileStorage.SealCreated(journal,cleanupHash,profile,sid);
            var principal=new SecurityIdentifier(sid);
            HostValidationCleanup.Grant(plan.InputRoot,principal,false);HostValidationCleanup.Grant(box,principal,false);HostValidationCleanup.Grant(scratch,principal,true);WindowsIsolationNative.LowIntegrity(scratch);
            var env=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){
                ["SystemRoot"]=Environment.GetFolderPath(Environment.SpecialFolder.Windows),["PATH"]=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32"),
                ["TEMP"]=scratch,["TMP"]=scratch,["LOCALAPPDATA"]=scratch,["APPDATA"]=scratch,["ProgramData"]=scratch,["USERPROFILE"]=scratch,
                ["ProgramFiles"]=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),["ProgramFiles(x86)"]=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                ["DOTNET_ROOT"]=sdk,["DOTNET_ROOT_X64"]=sdk,["DOTNET_HOST_PATH"]=exe,["DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR"]=sdk,
                ["DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR"]=Path.Combine(sdk,"sdk",plan.SdkVersion,"Sdks"),["DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER"]=plan.SdkVersion,
                ["DOTNET_EnableDiagnostics"]="0",["DOTNET_CLI_HOME"]=scratch,["DOTNET_CLI_USE_MSBUILD_SERVER"]="0",["DOTNET_CLI_TELEMETRY_OPTOUT"]="1",
                ["NUGET_PACKAGES"]=packages,["DOTNET_GENERATE_ASPNET_CERTIFICATE"]="false",["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"]="true",["DOTNET_NOLOGO"]="1",["DOTNET_CLI_UI_LANGUAGE"]="en-US"};
            var project=Under(plan.InputRoot,plan.ProjectPath);var artifacts=Path.Combine(scratch,"artifacts");
            var restore=new[]{"restore",project,"--configfile",nuget,"--packages",packages,"--artifacts-path",artifacts,"--disable-build-servers","-p:NuGetAudit=false","-p:RestoreIgnoreFailedSources=false","-p:UseSharedCompilation=false","-v:minimal"};
            var command=new[]{test.Kind=="dotnet_build"?"build":"test",project,"-c","Release","--no-restore","--artifacts-path",artifacts,"--disable-build-servers","-p:UseSharedCompilation=false","-v:minimal"};
            foreach(var (name,args) in new[]{("restore",restore),("validate",command)}){
                token.ThrowIfCancellationRequested();var captureRoot=Path.Combine(run,name);Directory.CreateDirectory(captureRoot);
                var remaining=CombinedLimit-(int)output.Length-(int)error.Length;if(remaining<=0){reason="output_limit";exit=129;break;}
                HostValidationProfileStorage.Verify(profileSeal);
                registryLease?.Verify();
                JsonElement frame;
                try{frame=JsonSerializer.SerializeToElement(await Task.Run(()=>WindowsIsolationNative.Capture(exe,args,scratch,sid,env,remaining,180000,captureRoot,token,"Local\\"+profile+"."+name,process=>registryLease=HostValidationProfileRegistry.Guard(journal,cleanupHash,profileSeal,process,registryLease))));}
                finally{try{registryLease?.Verify();}finally{HostValidationProfileStorage.Verify(profileSeal);}}
                var capturedOutput=File.ReadAllBytes(Path.Combine(captureRoot,"captured.stdout.bin"));
                if(name=="validate")validationOutput=capturedOutput;
                captures.Add(frame);output.Write(capturedOutput);error.Write(File.ReadAllBytes(Path.Combine(captureRoot,"captured.stderr.bin")));
                reason=frame.GetProperty("reason").GetString()!;exit=unchecked((int)frame.GetProperty("exit_code").GetUInt32());
                if(reason!="completed"||exit!=0)break;
            }
            VerifyFiles(plan,lease);
            var after=await AttemptStartingState.CaptureAsync(plan.WindowsRoot,null,CancellationToken.None);
            if(AttemptStartingState.Hash(after)!=AttemptStartingState.Hash(expected))throw new UnauthorizedAccessException("Validation changed the original source state");
            await RequireWorkspaceAsync(journal,plan.WindowsRoot,plan.Rescue,CancellationToken.None);
        }finally{
            try{registryLease?.Dispose();}finally{try{if(created)HostValidationCleanup.Finish(journal,cleanup,cleanupHash,false);}finally{if(sid!=IntPtr.Zero)WindowsIsolationNative.FreeSid(sid);}}
        }
        var raw=new{task_id=journal.TaskId,run,source_state_hash=plan.SourceStateHash,reason,exit_code=exit,hash_scope=reason=="completed"?"complete":"captured_prefix",captures,profile_deleted=created,immutable_inputs_verified=true,source_unchanged=true};
        journal.Record("validation_isolation_result",raw,"host_verified");
        var result=ToolRouter.IsolationResult(test,"windows",reason,exit,output.ToArray(),error.ToArray(),watch.ElapsedMilliseconds,validationOutput);
        journal.Record("required_test_result",result,"host_verified");return result;
    }
}
