using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

// Publisher authority is a Host registration, never a project/model supplied manifest.
internal static class HostValidationPolicy
{
    internal sealed record Tree(string Root,HostValidationIsolation.InputFile[] Files);
    internal sealed record Policy(string RepositoryId,string SdkVersion,Tree Sdk,Tree Packages,Tree Feed);
    private sealed record Reference(string RegistrationTask,string EventId,string PolicyHash);
    private static string Config(HostTaskJournal journal)=>Path.Combine(journal.Canonical.Root,"validation-policy",HostTaskJournal.RepositoryId(journal.WindowsRoot)+".json");
    private static string Relative(string value){
        if(string.IsNullOrWhiteSpace(value)||Path.IsPathRooted(value)||value.Contains(':')||value.Split('/',(char)92).Any(p=>p is "" or "." or ".."))throw new InvalidDataException("Invalid trusted input path");
        return value.Replace((char)92,'/');
    }
    private static void Verify(HostTaskJournal journal,Tree tree,HostValidationInputLease lease){
        if(!CanonicalStore.Within(journal.Canonical.Root,Path.GetFullPath(tree.Root)))throw new UnauthorizedAccessException("Trusted validation inputs must be protected Host-local state");
        CanonicalStore.GuardPath(tree.Root);
        if(tree.Files.Length>10000||tree.Files.Sum(f=>f.Bytes)>1536L*1024*1024)throw new InvalidDataException("Trusted input budget exceeded");
        var expected=tree.Files.Select(f=>Relative(f.Path)).Order(StringComparer.Ordinal).ToArray();
        if(expected.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=expected.Length)throw new InvalidDataException("Duplicate trusted input");
        var actual=new List<string>();var pending=new Stack<string>();pending.Push(tree.Root);
        while(pending.Count>0){var dir=pending.Pop();lease.LockDirectory(dir);foreach(var path in Directory.EnumerateFileSystemEntries(dir)){
            var attrs=File.GetAttributes(path);if((attrs&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked trusted input");
            if((attrs&FileAttributes.Directory)!=0)pending.Push(path);else actual.Add(Path.GetRelativePath(tree.Root,path).Replace((char)92,'/'));
        }}
        if(!actual.Order(StringComparer.Ordinal).SequenceEqual(expected,StringComparer.Ordinal))throw new UnauthorizedAccessException("Trusted publisher file set changed");
        foreach(var file in tree.Files){
            var relative=Relative(file.Path);
            if(file.Bytes is <0 or >128L*1024*1024||file.Sha256.Length!=64||System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(relative),@"^(?:\.env(?:\..*)?|id_(?:rsa|ed25519)|.*\.(?:pem|pfx|p12|key))$",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new UnauthorizedAccessException("Unqualified trusted input");
            var bytes=lease.Read(Path.Combine(tree.Root,relative));
            if(bytes.LongLength!=file.Bytes||!CanonicalStore.Hash(bytes).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Trusted publisher bytes changed");
        }
    }
    internal static string Register(HostTaskJournal journal,Policy policy){
        if(policy.RepositoryId!=HostTaskJournal.RepositoryId(journal.WindowsRoot)||!System.Text.RegularExpressions.Regex.IsMatch(policy.SdkVersion,@"^\d+\.\d+\.\d+$"))throw new UnauthorizedAccessException("Trusted publisher registration identity mismatch");
        using var lease=new HostValidationInputLease();foreach(var tree in new[]{policy.Sdk,policy.Packages,policy.Feed})Verify(journal,tree,lease);
        if(!policy.Sdk.Files.Any(f=>Relative(f.Path)=="dotnet.exe")||!policy.Sdk.Files.Any(f=>Relative(f.Path).StartsWith("sdk/"+policy.SdkVersion+"/",StringComparison.Ordinal)))throw new UnauthorizedAccessException("Named SDK publisher is incomplete");
        var hash=journal.Canonical.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(policy));var path=Config(journal);CanonicalStore.GuardPath(path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var receipt=journal.Canonical.RegisterValidationPublisher(journal.TaskId,HostTaskJournal.WorkspaceId(journal.WindowsRoot),policy.RepositoryId,hash);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new Reference(journal.TaskId,receipt.EventId,hash));
        var staging=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        using(var output=new FileStream(staging,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes);output.Flush(true);}File.Move(staging,path,true);return hash;
    }
    private static Policy Load(HostTaskJournal journal){
        var path=Config(journal);CanonicalStore.GuardPath(path);if(!File.Exists(path))throw new UnauthorizedAccessException("No Host-registered validation publisher policy");
        if(new FileInfo(path).Length>4096)throw new InvalidDataException("Publisher reference exceeds budget");
        var reference=JsonSerializer.Deserialize<Reference>(File.ReadAllBytes(path))??throw new InvalidDataException("Invalid publisher reference");
        var current=journal.Canonical.CurrentValidationPublisher(HostTaskJournal.RepositoryId(journal.WindowsRoot));
        if(current is null||current.RegistrationTask!=reference.RegistrationTask||current.EventId!=reference.EventId||current.PolicyHash!=reference.PolicyHash)
            throw new UnauthorizedAccessException("Validation publisher reference is stale against canonical current registration");
        var e=journal.Canonical.ReadTaskEvent(reference.RegistrationTask,reference.EventId);
        if(e.Input.Producer!="windows_host"||e.Input.VerificationStatus!="host_verified"||e.Input.EventType!="validation_publisher_policy_registered"||
            e.Input.Metadata.GetProperty("new_hash").GetString()!=reference.PolicyHash||!e.Input.EvidenceRefs.Contains(reference.PolicyHash,StringComparer.Ordinal))throw new UnauthorizedAccessException("Publisher lacks a canonical Host registration");
        var policy=JsonSerializer.Deserialize<Policy>(journal.Canonical.ReadEvidence(reference.PolicyHash))??throw new InvalidDataException("Invalid publisher policy");
        if(policy.RepositoryId!=HostTaskJournal.RepositoryId(journal.WindowsRoot)||e.Input.Metadata.GetProperty("repository_id").GetString()!=policy.RepositoryId)throw new UnauthorizedAccessException("Publisher repository binding mismatch");return policy;
    }
    internal static async Task<string?> PrepareDotnetAsync(HostTaskJournal journal,string windowsRoot,RequiredTest test,CancellationToken token=default,HostValidationIsolation.RescueBinding? rescue=null){
        token.ThrowIfCancellationRequested();
        if(test.Kind is not ("dotnet_build" or "dotnet_test"))return null;
        await HostValidationIsolation.RequireWorkspaceAsync(journal,Path.GetFullPath(windowsRoot),rescue,token);
        var request=AgentTaskRequest.Parse(journal.Canonical.TaskRequest(journal.TaskId));if(!request.RequiredTests.Contains(test))throw new UnauthorizedAccessException("Unrequested isolated validation");
        var policy=Load(journal);using var lease=new HostValidationInputLease();foreach(var tree in new[]{policy.Sdk,policy.Packages,policy.Feed})Verify(journal,tree,lease);
        var source=await AttemptStartingState.CaptureAsync(windowsRoot,null,token);
        if(source.Head!=journal.ExpectedHead)throw new UnauthorizedAccessException("Validation source HEAD drift");
        if(policy.Sdk.Files.Length+policy.Packages.Files.Length+policy.Feed.Files.Length+source.Files.Length>10000||
            policy.Sdk.Files.Sum(f=>f.Bytes)+policy.Packages.Files.Sum(f=>f.Bytes)+policy.Feed.Files.Sum(f=>f.Bytes)+48L*1024*1024>1536L*1024*1024)
            throw new InvalidDataException("Combined prepared input exceeds admission budget");
        if(journal.PersonalValidationEnabled)HostValidationStorage.CheckSpace(journal,policy.Sdk.Files.Sum(f=>f.Bytes)+policy.Packages.Files.Sum(f=>f.Bytes)+policy.Feed.Files.Sum(f=>f.Bytes)+source.Files.Where(f=>f.Hash is not null).Sum(f=>new FileInfo(Path.Combine(windowsRoot,Relative(f.Path))).Length));
        var input=Path.Combine(journal.Canonical.Root,"validation-inputs",Guid.NewGuid().ToString("N")[..12]);CanonicalStore.GuardPath(input);if(Directory.Exists(input))throw new IOException("Fresh prepared input tree required");Directory.CreateDirectory(input);
        if(journal.PersonalValidationEnabled)HostValidationStorage.Allocated(journal,input);
        try{
        var files=new List<HostValidationIsolation.InputFile>();
        void Copy(string name,byte[] bytes){token.ThrowIfCancellationRequested();var dest=Path.Combine(input,Relative(name));Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.WriteAllBytes(dest,bytes);files.Add(new(name,bytes.LongLength,CanonicalStore.Hash(bytes)));}
        foreach(var (prefix,tree) in new[]{("sdk",policy.Sdk),("packages",policy.Packages),("offline-feed",policy.Feed)}){
            Directory.CreateDirectory(Path.Combine(input,prefix));foreach(var file in tree.Files)Copy(prefix+"/"+Relative(file.Path),lease.Read(Path.Combine(tree.Root,Relative(file.Path))));
        }
        foreach(var file in source.Files.Where(f=>f.Hash is not null)){
            var bytes=lease.Read(Path.Combine(windowsRoot,Relative(file.Path)));if(CanonicalStore.Hash(bytes)!=file.Hash)throw new UnauthorizedAccessException("Source changed while Host prepared input");Copy("repo/"+Relative(file.Path),bytes);
        }
        foreach(var tree in new[]{policy.Sdk,policy.Packages,policy.Feed})Verify(journal,tree,lease);
        if(AttemptStartingState.Hash(await AttemptStartingState.CaptureAsync(windowsRoot,null,token))!=AttemptStartingState.Hash(source))throw new UnauthorizedAccessException("Source changed before admission");
        await HostValidationIsolation.AdmitDotnetAsync(journal,windowsRoot,test,input,files.ToArray(),policy.SdkVersion,token,rescue);
        journal.Record("validation_inputs_prepared",new{policy_hash=journal.Canonical.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(policy)),input_root=input,file_count=files.Count,source_state_hash=AttemptStartingState.Hash(source)},"host_verified");
        return input;
        }catch(Exception original){
            // The publisher source lease stays alive here, but never locks its newly generated destination.
            if(journal.PersonalValidationEnabled)try{HostValidationStorage.Retire(journal,input);}catch(Exception cleanup){throw new AggregateException(original,cleanup);}
            throw;
        }
    }
    internal static Task<ToolRouter.TestResult> RunAsync(HostTaskJournal journal,string windowsRoot,string wslRoot,RequiredTest test,CancellationToken token=default)=>RunCoreAsync(journal,windowsRoot,wslRoot,test,null,token);
    internal static Task<ToolRouter.TestResult> RunRescueAsync(HostTaskJournal journal,string ticketHash,string candidateHash,string windowsRoot,string wslRoot,RequiredTest test,CancellationToken token=default)=>RunCoreAsync(journal,windowsRoot,wslRoot,test,new(ticketHash,candidateHash),token);
    private static async Task<ToolRouter.TestResult> RunCoreAsync(HostTaskJournal journal,string windowsRoot,string wslRoot,RequiredTest test,HostValidationIsolation.RescueBinding? rescue,CancellationToken token){
        await HostValidationIsolation.RequireWorkspaceAsync(journal,Path.GetFullPath(windowsRoot),rescue,token);
        if(journal.PersonalValidationEnabled && test.Kind is ("git_diff_check" or "npm_test"))
            return await HostPersonalValidation.RunAsync(journal,windowsRoot,wslRoot,test,rescue,token);
        if(test.Kind is not ("dotnet_build" or "dotnet_test"))return await ToolRouter.RunAsync(windowsRoot,wslRoot,test,token);
        var policy=Load(journal);var hash=CanonicalStore.Hash(JsonSerializer.SerializeToUtf8Bytes(policy));
        using var dispatch=journal.Canonical.AcquireValidationPublisher(policy.RepositoryId,hash);
        var input=await PrepareDotnetAsync(journal,windowsRoot,test,token,rescue);
        Exception? original=null;
        try{return await ToolRouter.RunAsync(windowsRoot,wslRoot,test,token);}
        catch(Exception e){original=e;throw;}
        finally{
            if(journal.PersonalValidationEnabled && input is not null)
                try{HostValidationStorage.Retire(journal,input);}catch(Exception cleanup){if(original is not null)throw new AggregateException(original,cleanup);throw;}
        }
    }
}
