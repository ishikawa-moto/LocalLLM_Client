using System.Diagnostics;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

internal static class TaskCommandAuthorization
{
    internal sealed record Pending(string Input,string Output,string InputHash,string Nonce,string TaskId,string Root,string RequestHash,string Head);
    internal sealed record Grant(Pending Pending,string ReceiptHash);
    private static readonly AsyncLocal<Grant?> ambient=new();
    internal static Grant? Current=>ambient.Value;
    private sealed class Scope(Grant? prior):IDisposable{public void Dispose()=>ambient.Value=prior;}
    internal static IDisposable Enter(Grant grant){Validate(grant);var prior=ambient.Value;ambient.Value=grant;return new Scope(prior);}
    internal static Pending Prepare(string directory,string root,string taskId,AgentTaskRequest request,string head)
    {
        if(request.CommandExecution is null)throw new InvalidDataException("Task command contract required");request.CommandExecution.Validate();
        CanonicalStore.GuardPath(directory);Directory.CreateDirectory(directory);
        var input=Path.Combine(directory,"authorization-input.json");var output=Path.Combine(directory,"authorization-result.json");
        if(File.Exists(input)||File.Exists(output))throw new IOException("Authorization intent already exists; observe it");
        var nonce=Guid.NewGuid().ToString("N");var hash=AgentTaskRunner.RequestHash(request);
        var value=new{source="windows_task_command_grant_request",nonce,task_id=taskId,root=Path.GetFullPath(root),request_hash=hash,git_head=head,
            requirement=request.Requirement,acceptance_criteria=request.AcceptanceCriteria,allowed_files=request.AllowedFiles,risk=request.EffectiveRisk,
            contract=request.CommandExecution,output_path=output,trust="trusted personal project code; working-directory scope is not a hostile-code filesystem/network sandbox",
            lifetime="This exact task and its verified resumptions until completion/cancellation; bounded active execution budget. No individual command confirmations within this scope."};
        var bytes=JsonSerializer.SerializeToUtf8Bytes(value,ClientConfig.JsonOptions);File.WriteAllBytes(input,bytes);
        return new(input,output,CanonicalStore.Hash(bytes),nonce,taskId,Path.GetFullPath(root),hash,head);
    }
    internal static Grant? Read(Pending pending)
    {
        CanonicalStore.GuardPath(pending.Input);CanonicalStore.GuardPath(pending.Output);
        if(CanonicalStore.Hash(File.ReadAllBytes(pending.Input))!=pending.InputHash)throw new UnauthorizedAccessException("Task grant input changed");
        if(!File.Exists(pending.Output))return null;
        var bytes=File.ReadAllBytes(pending.Output);if(bytes.Length>16000)throw new InvalidDataException("Oversized task grant receipt");using var d=JsonDocument.Parse(bytes);var r=d.RootElement;
        if(r.GetProperty("source").GetString()!="windows_native_task_command_grant"||r.GetProperty("nonce").GetString()!=pending.Nonce||
            r.GetProperty("input_hash").GetString()!=pending.InputHash||r.GetProperty("task_id").GetString()!=pending.TaskId||
            r.GetProperty("request_hash").GetString()!=pending.RequestHash||r.GetProperty("confirmed").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new UnauthorizedAccessException("Task grant receipt binding mismatch");
        if(!r.GetProperty("confirmed").GetBoolean())throw new UnauthorizedAccessException("Task command execution was declined");
        return new(pending,CanonicalStore.Hash(bytes));
    }
    internal static void Validate(Grant grant)
    {
        var current=Read(grant.Pending);if(current?.ReceiptHash!=grant.ReceiptHash)throw new UnauthorizedAccessException("Task command grant is absent or changed");
    }
    internal static Grant Require(HostTaskJournal journal,AgentTaskRequest request)
    {
        var grant=Current??throw new UnauthorizedAccessException("A genuine one-time Host task command grant is required");Validate(grant);
        if(grant.Pending.TaskId!=journal.TaskId||grant.Pending.RequestHash!=AgentTaskRunner.RequestHash(request)||grant.Pending.Head!=journal.ExpectedHead||
            !string.Equals(grant.Pending.Root,journal.WindowsRoot,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Grant belongs to another task, request, HEAD or workspace");
        return grant;
    }
    internal static void Launch(Pending pending)
    {
        if(!OperatingSystem.IsWindows()||!Environment.UserInteractive)throw new UnauthorizedAccessException("Task command grant needs the Windows desktop");
        var script=Path.Combine(AppContext.BaseDirectory,"Confirm-TaskCommands.ps1");CanonicalStore.GuardPath(script);
        var qualified=Path.Combine(AppContext.BaseDirectory,"task-command-native-code.json");CanonicalStore.GuardPath(qualified);
        using var manifest=JsonDocument.Parse(File.ReadAllBytes(qualified));
        if(CanonicalStore.Hash(File.ReadAllBytes(script))!=manifest.RootElement.GetProperty("script_sha256").GetString())throw new UnauthorizedAccessException("Unqualified task command confirmation code");
        var reservation=pending.Input+".launch-intent";
        using(var file=new FileStream(reservation,FileMode.CreateNew,FileAccess.Write,FileShare.None)){var bytes=System.Text.Encoding.UTF8.GetBytes(pending.InputHash);file.Write(bytes);file.Flush(true);}
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true};
        using var self=Process.GetCurrentProcess();
        foreach(var a in new[]{"-NoProfile","-NonInteractive","-STA","-WindowStyle","Hidden","-ExecutionPolicy","Bypass","-File",script,"-InputPath",pending.Input,"-OwnerPid",self.Id.ToString(),"-OwnerBirth",self.StartTime.ToUniversalTime().Ticks.ToString()})start.ArgumentList.Add(a);
        using var process=Process.Start(start)??throw new IOException("Task grant dialog did not start");
    }
}
