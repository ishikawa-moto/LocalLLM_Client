using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

namespace LocalBrain.ClientHost.Ocr;

[SupportedOSPlatform("windows")]
internal sealed class OcrSandbox:IDisposable
{
    private readonly string name,assets,input,scratch;
    private readonly string[] readOnlyRoots;
    private readonly SecurityIdentifier principal;
    private bool disposed;
    private sealed record Identity(string Profile,string Sid,string Assets,string Input,string Scratch,string[] ReadOnlyRoots,int OwnerPid,long OwnerBirth);
    internal string Sid=>principal.Value;
    internal OcrSandbox(string assets,string input,string scratch,string jobId,string[]? extraReadOnly=null){
        if(!Guid.TryParseExact(jobId,"N",out _))throw new OcrException("JOB_ID_INVALID");
        foreach(var path in new[]{assets,input,scratch})CanonicalStore.GuardPath(path);
        this.assets=assets;this.input=input;this.scratch=scratch;
        readOnlyRoots=new[]{assets,input}.Concat(extraReadOnly??[]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach(var path in readOnlyRoots)CanonicalStore.GuardPath(path);
        name="lbocr."+jobId+"."+Guid.NewGuid().ToString("N")[..16];
        var code=WindowsIsolationNative.CreateAppContainerProfile(name,name,"Local offline OCR; network capabilities=0",IntPtr.Zero,0,out var sid);
        if(code<0)Marshal.ThrowExceptionForHR(code);
        try{principal=new SecurityIdentifier(sid);}finally{WindowsIsolationNative.FreeSid(sid);}
        try{
            OcrJobStore.New(Path.Combine(input,"sandbox-identity.json"),new Identity(name,Sid,assets,input,scratch,readOnlyRoots,Environment.ProcessId,System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks));
            foreach(var path in readOnlyRoots)HostValidationCleanup.Grant(path,principal,false);
            HostValidationCleanup.Grant(scratch,principal,true);
            WindowsIsolationNative.LowIntegrity(scratch);
        }catch{Dispose();throw;}
    }
    private void Remove(string path){
        var info=new DirectoryInfo(path);if(!info.Exists)return;CanonicalStore.GuardPath(path);
        var acl=info.GetAccessControl();acl.PurgeAccessRules(principal);info.SetAccessControl(acl);
    }
    public void Dispose(){
        if(disposed)return;disposed=true;
        Exception? failure=null;
        foreach(var path in new[]{scratch}.Concat(readOnlyRoots))try{Remove(path);}catch(Exception e)when(e is IOException or UnauthorizedAccessException){failure??=e;}
        var code=WindowsIsolationNative.DeleteAppContainerProfile(name);
        if(failure is not null)throw new OcrException("SANDBOX_ACL_CLEANUP_PENDING");
        if(code<0)throw new OcrException("SANDBOX_PROFILE_CLEANUP_PENDING");
        OcrJobStore.Atomic(Path.Combine(input,"sandbox-cleaned.json"),new{profile=name,sid=Sid,deleted=true,network_capabilities=0});
    }
    internal static void Recover(OcrConfig config,OcrJobStore.Manifest manifest,string attempt){
        var p=Path.Combine(attempt,"sandbox-identity.json");if(!File.Exists(p)||File.Exists(Path.Combine(attempt,"sandbox-cleaned.json")))return;
        var identity=OcrJobStore.Read<Identity>(p);
        var expectedInput=Path.Combine(config.Root,"jobs",manifest.JobId,"input");
        var roots=new[]{config.AssetRoot,attempt,expectedInput}.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(identity.Assets!=config.AssetRoot||identity.Input!=attempt||identity.Scratch!=Path.Combine(attempt,"scratch")||!identity.ReadOnlyRoots.SequenceEqual(roots,StringComparer.OrdinalIgnoreCase)||!System.Text.RegularExpressions.Regex.IsMatch(identity.Profile,"^lbocr\\."+manifest.JobId+"\\.[a-f0-9]{16}$"))throw new OcrException("SANDBOX_RECOVERY_IDENTITY_MISMATCH");
        // A previous live Host or child is never disturbed. Same-owner completion may finish its own failed cleanup.
        if(identity.OwnerPid!=Environment.ProcessId&&TaskAgentCoordinator.IsAlive(identity.OwnerPid,identity.OwnerBirth))throw new OcrException("SANDBOX_OWNER_STILL_ALIVE");
        var processPath=Path.Combine(attempt,"process.json");if(File.Exists(processPath)){
            var child=OcrJobStore.Read<JsonElement>(processPath);
            if(TaskAgentCoordinator.IsAlive(child.GetProperty("pid").GetInt32(),child.GetProperty("birth").GetInt64()))throw new OcrException("PRIOR_WORKER_STILL_ALIVE_INSPECT");
        }
        var code=WindowsIsolationNative.DeriveAppContainerSidFromAppContainerName(identity.Profile,out var sid);if(code<0)Marshal.ThrowExceptionForHR(code);
        try{
            var principal=new SecurityIdentifier(sid);if(principal.Value!=identity.Sid)throw new OcrException("SANDBOX_RECOVERY_SID_MISMATCH");
            foreach(var path in roots.Append(identity.Scratch)){
                CanonicalStore.GuardPath(path);var info=new DirectoryInfo(path);if(!info.Exists)continue;
                var acl=info.GetAccessControl();acl.PurgeAccessRules(principal);info.SetAccessControl(acl);
            }
            code=WindowsIsolationNative.DeleteAppContainerProfile(identity.Profile);
            if(code<0&&code!=unchecked((int)0x80070002))Marshal.ThrowExceptionForHR(code);
            OcrJobStore.Atomic(Path.Combine(attempt,"sandbox-cleaned.json"),new{profile=identity.Profile,sid=identity.Sid,deleted=true,recovery=true,network_capabilities=0});
        }finally{WindowsIsolationNative.FreeSid(sid);}
    }
}
