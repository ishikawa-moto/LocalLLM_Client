using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

// Recovery consumes only canonical Host evidence and never worker-provided cleanup commands.
[SupportedOSPlatform("windows")]
internal static class HostValidationCleanup
{
    internal sealed record Plan(string TaskId,string Run,string InputRoot,string Profile,string[] Jobs,string OriginalInputDacl);
    internal static void Grant(string path,SecurityIdentifier principal,bool writable){
        var info=new DirectoryInfo(path);var acl=info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(principal,writable?FileSystemRights.Modify:FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        if(!writable)acl.AddAccessRule(new FileSystemAccessRule(principal,FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Deny));
        info.SetAccessControl(acl);
    }
    internal static string Prepare(HostTaskJournal journal,Plan plan){
        var hash=journal.Canonical.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(plan));
        journal.Canonical.Append(new(journal.TaskId,HostTaskJournal.WorkspaceId(journal.WindowsRoot),"windows_host",Guid.NewGuid().ToString("N"),
            "validation_isolation_cleanup_planned","host_cleanup","host_verified",[hash],JsonSerializer.SerializeToElement(new{cleanup_plan_hash=hash})));
        File.WriteAllText(Path.Combine(plan.Run,"profile-cleanup-journal.json"),JsonSerializer.Serialize(new{plan_hash=hash,plan}));return hash;
    }
    internal static void Created(HostTaskJournal journal,Plan plan,string hash,IntPtr sid)=>
        journal.Record("validation_isolation_profile_created",new{plan_hash=hash,profile=plan.Profile,sid=new SecurityIdentifier(sid).Value},"host_verified");
    internal static void CreateProfile(HostTaskJournal journal,Plan plan,string hash,out IntPtr sid,out bool ownsProfile){
        var code=WindowsIsolationNative.CreateAppContainerProfile(plan.Profile,plan.Profile,"Host isolated validation",IntPtr.Zero,0,out sid);
        ownsProfile=code>=0;
        if(!ownsProfile){NotCreated(journal,plan,hash,code);Marshal.ThrowExceptionForHR(code);}
        Created(journal,plan,hash,sid);
    }
    internal static void NotCreated(HostTaskJournal journal,Plan plan,string hash,int code){
        var result=new{plan_hash=hash,profile=plan.Profile,created=false,profile_preserved=true,code};
        journal.Record("validation_isolation_profile_not_created",result,"host_verified");
        File.WriteAllText(Path.Combine(plan.Run,"profile-cleanup-result.json"),JsonSerializer.Serialize(result));
    }
    private static void Validate(HostTaskJournal journal,Plan plan){
        if(plan.TaskId!=journal.TaskId||Path.GetDirectoryName(plan.Run)!=Path.Combine(journal.Canonical.Root,"validation-runs")||
            Path.GetDirectoryName(plan.InputRoot)!=Path.Combine(journal.Canonical.Root,"validation-inputs")||
            !System.Text.RegularExpressions.Regex.IsMatch(plan.Profile,@"^LBV\.[a-f0-9]{12}$")||plan.Jobs.Length!=2||
            !plan.Jobs.SequenceEqual(new[]{"Local\\"+plan.Profile+".restore","Local\\"+plan.Profile+".validate"}))
            throw new UnauthorizedAccessException("Invalid Host cleanup identity");
        CanonicalStore.GuardPath(plan.Run);CanonicalStore.GuardPath(plan.InputRoot);
    }
    internal static void Finish(HostTaskJournal journal,Plan plan,string planHash,bool recovery){
        Validate(journal,plan);
        // Kill-on-close is atomic and the Job handle is not inherited. A reused PID is never consulted.
        if(plan.Jobs.Any(j=>!WindowsIsolationNative.JobAbsentOrEmpty(j)))throw new IOException("Owned validation Job still has live members; cleanup remains pending");
        var code=WindowsIsolationNative.DeriveAppContainerSidFromAppContainerName(plan.Profile,out var sid);
        if(code<0)Marshal.ThrowExceptionForHR(code);
        try{
            var principal=new SecurityIdentifier(sid);
            foreach(var path in new[]{plan.InputRoot,Path.Combine(plan.Run,"box"),Path.Combine(plan.Run,"box","scratch")}){
                if(!Directory.Exists(path))continue;CanonicalStore.GuardPath(path);
                var info=new DirectoryInfo(path);var current=info.GetAccessControl();
                // Remove only this random profile's ACEs. Preserve unrelated concurrent Host ACL edits.
                current.PurgeAccessRules(principal);info.SetAccessControl(current);
                if(info.GetAccessControl().GetAccessRules(true,false,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(r=>r.IdentityReference.Equals(principal)))
                    throw new IOException("Owned isolation ACL was not removed");
            }
            code=WindowsIsolationNative.DeleteAppContainerProfile(plan.Profile);
            if(code<0&&code!=unchecked((int)0x80070002))Marshal.ThrowExceptionForHR(code);
            var result=new{profile=plan.Profile,plan_hash=planHash,deleted=true,owned_acl_removed=true,owned_jobs_absent_or_empty=true,recovery};
            journal.Record("validation_isolation_cleanup_completed",result,"host_verified");
            File.WriteAllText(Path.Combine(plan.Run,"profile-cleanup-result.json"),JsonSerializer.Serialize(result));
        }finally{if(sid!=IntPtr.Zero)WindowsIsolationNative.FreeSid(sid);}
    }
    internal static void Recover(HostTaskJournal journal){
        var ids=journal.Canonical.EventIds(journal.TaskId);
        var completed=new HashSet<string>(StringComparer.Ordinal);var owned=new HashSet<string>(StringComparer.Ordinal);
        foreach(var id in ids){var e=journal.Canonical.ReadTaskEvent(journal.TaskId,id);
            if(e.Input.Producer=="windows_host"&&e.Input.VerificationStatus=="host_verified"&&e.Input.EventType is "validation_isolation_profile_not_created" or "validation_isolation_profile_created"){
                foreach(var reference in e.Input.EvidenceRefs){using var proof=JsonDocument.Parse(journal.Canonical.ReadEvidence(reference));var hash=proof.RootElement.GetProperty("plan_hash").GetString()!;
                    if(e.Input.EventType=="validation_isolation_profile_created")owned.Add(hash);
                    else if(!proof.RootElement.GetProperty("created").GetBoolean()&&proof.RootElement.GetProperty("profile_preserved").GetBoolean())completed.Add(hash);
                }
            }
            if(e.Input.EventType!="validation_isolation_cleanup_completed"||e.Input.Producer!="windows_host"||e.Input.VerificationStatus!="host_verified")continue;
            foreach(var reference in e.Input.EvidenceRefs){using var proof=JsonDocument.Parse(journal.Canonical.ReadEvidence(reference));
                if(proof.RootElement.GetProperty("deleted").GetBoolean()&&proof.RootElement.GetProperty("owned_acl_removed").GetBoolean()&&proof.RootElement.GetProperty("owned_jobs_absent_or_empty").GetBoolean())
                    completed.Add(proof.RootElement.GetProperty("plan_hash").GetString()!);
            }
        }
        foreach(var id in ids){
            var e=journal.Canonical.ReadTaskEvent(journal.TaskId,id);
            if(e.Input.EventType!="validation_isolation_cleanup_planned"||e.Input.Producer!="windows_host"||e.Input.VerificationStatus!="host_verified")continue;
            var hash=e.Input.Metadata.GetProperty("cleanup_plan_hash").GetString()!;
            if(!e.Input.EvidenceRefs.Contains(hash,StringComparer.Ordinal))throw new UnauthorizedAccessException("Cleanup plan is not canonical evidence");
            var plan=JsonSerializer.Deserialize<Plan>(journal.Canonical.ReadEvidence(hash))??throw new InvalidDataException("Invalid Host cleanup plan");Validate(journal,plan);
            if(completed.Contains(hash))continue;
            FileStream hostLease;
            try{hostLease=new FileStream(Path.Combine(plan.Run,"host-lease.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException ex)when((ex.HResult&65535)==32){continue;}
            using(hostLease){
                if(!owned.Contains(hash))throw new IOException("Profile creation receipt is absent; ownership is ambiguous and recovery must not alter it");
                Finish(journal,plan,hash,true);
            }
        }
    }
}
