using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalBrain.ClientHost.AgentV2;

// Personal-use preflight and disposal of one Host-generated invocation. No historical archive sweep.
internal static class HostValidationStorage
{
    private const long ReserveBytes=2L*1024*1024*1024;
    private static void RequireOwner(HostTaskJournal journal)
    {
        if(HostTaskJournal.Current!=journal || !journal.PersonalValidationEnabled)
            throw new UnauthorizedAccessException("Temporary validation storage needs its current personal-mode Host owner");
    }
    internal static void CheckSpace(HostTaskJournal journal,long inputBytes,long? fixtureFreeBytes=null)
    {
        RequireOwner(journal);if(inputBytes<0)throw new ArgumentOutOfRangeException(nameof(inputBytes));
        var free=fixtureFreeBytes??new DriveInfo(Path.GetPathRoot(journal.Canonical.Root)!).AvailableFreeSpace;
        var required=checked(inputBytes+ReserveBytes);
        journal.Record("validation_storage_preflight",new{input_bytes=inputBytes,free_bytes=free,reserve_bytes=ReserveBytes,required_bytes=required,
            permitted=free>=required,measurement=fixtureFreeBytes is null?"actual_host_drive":"synthetic_test_measurement",hard_scratch_quota=false},"host_verified");
        if(free<required)throw new IOException("Insufficient Host disk space for copied inputs and operational reserve");
    }
    private static string Input(HostTaskJournal journal,string input)
    {
        input=Path.GetFullPath(input);
        if(!string.Equals(Path.GetDirectoryName(input),Path.Combine(journal.Canonical.Root,"validation-inputs"),StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(input),"^[a-f0-9]{12}$"))throw new UnauthorizedAccessException("Only exact fresh Host-generated validation input can be retired");
        CanonicalStore.GuardPath(input);return input;
    }
    internal static void Allocated(HostTaskJournal journal,string input)
    {
        RequireOwner(journal);input=Input(journal,input);
        journal.Record("personal_validation_input_allocated",new{input_root=input,task_id=journal.TaskId},"host_verified");
    }
    private static JsonElement[] Records(HostTaskJournal journal,string kind)=>journal.Canonical.EventIds(journal.TaskId)
        .Select(id=>journal.Canonical.ReadTaskEvent(journal.TaskId,id))
        .Where(e=>e.Input.EventType==kind && e.Input.Producer=="windows_host" && e.Input.VerificationStatus=="host_verified")
        .SelectMany(e=>e.Input.EvidenceRefs.Select(hash=>JsonSerializer.Deserialize<JsonElement>(journal.Canonical.ReadEvidence(hash)))).ToArray();
    private static void Remove(string root)
    {
        CanonicalStore.GuardPath(root);if(!Directory.Exists(root))return;
        var pending=new Stack<string>();pending.Push(root);var count=0;
        while(pending.Count>0)
        {
            foreach(var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if(++count>100000)throw new IOException("Temporary tree exceeds practical cleanup inventory; retained for review");
                var attrs=File.GetAttributes(path);if((attrs&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked temporary descendant; retained for review");
                if((attrs&FileAttributes.Directory)!=0)pending.Push(path);
            }
        }
        // Exact absolute roots have been checked, including every existing descendant.
        Directory.Delete(root,true);
    }
    internal static void Retire(HostTaskJournal journal,string input)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Validation storage is owned by the Windows Host");
        RequireOwner(journal);input=Input(journal,input);
        var allocations=Records(journal,"personal_validation_input_allocated").Where(r=>r.GetProperty("input_root").GetString()==input && r.GetProperty("task_id").GetString()==journal.TaskId).ToArray();
        if(allocations.Length!=1)throw new UnauthorizedAccessException("Input lacks one exact Host invocation allocation");
        var completed=Records(journal,"validation_isolation_cleanup_completed");
        var plans=Records(journal,"validation_isolation_cleanup_planned")
            .Where(r=>r.TryGetProperty("InputRoot",out var value) && value.GetString()==input)
            .Select(r=>r.Deserialize<HostValidationCleanup.Plan>()!).ToArray();
        var scratch=new List<string>();
        foreach(var plan in plans)
        {
            var planHash=CanonicalStore.Hash(JsonSerializer.SerializeToUtf8Bytes(plan));
            if(plan.TaskId!=journal.TaskId || !Regex.IsMatch(plan.Profile,"^LBV\\.[a-f0-9]{12}$") ||
                !plan.Jobs.SequenceEqual(new[]{"Local\\"+plan.Profile+".restore","Local\\"+plan.Profile+".validate"}) ||
                !string.Equals(Path.GetDirectoryName(plan.Run),Path.Combine(journal.Canonical.Root,"validation-runs"),StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(Path.GetFileName(plan.Run),"^[a-f0-9]{12}$"))throw new UnauthorizedAccessException("Cleanup plan identity mismatch");
            if(!completed.Any(r=>r.GetProperty("plan_hash").GetString()==planHash && r.GetProperty("deleted").GetBoolean() &&
                r.GetProperty("owned_acl_removed").GetBoolean() && r.GetProperty("owned_jobs_absent_or_empty").GetBoolean()) ||
                plan.Jobs.Any(job=>!WindowsIsolationNative.JobAbsentOrEmpty(job)))throw new IOException("Owned process/profile cleanup incomplete; temporary data retained");
            scratch.Add(Path.Combine(plan.Run,"box","scratch"));
        }
        // No plan means preparation failed before child creation. Existing publication/archives never have this allocation.
        journal.Record("personal_validation_storage_retire_intent",new{input_root=input,scratch=scratch.ToArray(),canonical_evidence_preserved=true},"host_verified");
        try
        {
            foreach(var path in scratch)Remove(path);Remove(input);
            journal.Record("personal_validation_storage_retired",new{input_root=input,scratch=scratch.ToArray(),canonical_evidence_preserved=true},"host_verified");
        }
        catch(Exception e){journal.Record("personal_validation_storage_retire_failed",new{input_root=input,error_type=e.GetType().Name,retained_for_recovery=true},"host_verified");throw;}
    }
}
