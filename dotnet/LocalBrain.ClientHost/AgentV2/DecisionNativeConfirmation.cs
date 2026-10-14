using System.Diagnostics;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

internal static class DecisionNativeConfirmation
{
    internal sealed record Pending(string Nonce,string InputPath,string OutputPath,string InputHash,string IntentHash,string ReviewHash);
    internal static Pending Prepare(CanonicalStore store,HostTaskJournal journal,DecisionAdoption.Intent intent,string intentHash,JsonElement review)
    {
        if(!OperatingSystem.IsWindows()||!Environment.UserInteractive)throw new UnauthorizedAccessException("Reaffirmation needs the user's Windows desktop");
        var nonce=Guid.NewGuid().ToString("N");var folder=Path.Combine(store.Root,"native-decision-confirmations",nonce);CanonicalStore.GuardPath(folder);Directory.CreateDirectory(folder);
        var input=Path.Combine(folder,"input.json");var output=Path.Combine(folder,"result.json");
        var reviewHash=review.GetProperty("review_receipt_hash").GetString()!;
        var value=JsonSerializer.SerializeToElement(new{source="windows_native_dialog_request",nonce,intent_hash=intentHash,review_receipt_hash=reviewHash,
            request_id=intent.payload.GetProperty("request_id"),project=intent.payload.GetProperty("project"),title=intent.payload.GetProperty("title"),content=intent.payload.GetProperty("content"),issues=review.GetProperty("review_receipt").GetProperty("issues"),output_path=output});
        var bytes=DecisionProtocol.Canonical(value);var hash=store.PutEvidence(bytes);File.WriteAllBytes(input,bytes);
        var pending=new Pending(nonce,input,output,hash,intentHash,reviewHash);
        store.Append(new(journal.TaskId,store.GetTask(journal.TaskId)!.WorkspaceId,"windows_host","native_request_"+nonce,"decision_native_reaffirmation_requested","human_confirmation_request","host_verified",[hash],JsonSerializer.SerializeToElement(new{nonce,intent_hash=intentHash,review_receipt_hash=reviewHash,input_hash=hash,pending})));
        return pending;
    }
    internal static void Launch(CanonicalStore store,HostTaskJournal journal,Pending pending)
    {
        var script=Path.Combine(AppContext.BaseDirectory,"Confirm-DecisionReaffirmation.ps1");CanonicalStore.GuardPath(script);
        var qualified=Path.Combine(AppContext.BaseDirectory,"decision-native-code.json");CanonicalStore.GuardPath(qualified);
        using var code=JsonDocument.Parse(File.ReadAllBytes(qualified));
        if(CanonicalStore.Hash(File.ReadAllBytes(script))!=code.RootElement.GetProperty("script_sha256").GetString())throw new InvalidDataException("Native confirmation code differs from its qualified deployment");
        store.Append(new(journal.TaskId,store.GetTask(journal.TaskId)!.WorkspaceId,"windows_host","native_launch_"+pending.Nonce,"decision_native_dialog_launch_reserved","human_confirmation_request","host_verified",[pending.InputHash],JsonSerializer.SerializeToElement(new{pending.Nonce,action="launch_once_no_automatic_relaunch"})));
        var shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe");
        var start=new ProcessStartInfo(shell){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in new[]{"-NoProfile","-NonInteractive","-WindowStyle","Hidden","-ExecutionPolicy","Bypass","-File",script,"-InputPath",pending.InputPath})start.ArgumentList.Add(argument);
        var process=Process.Start(start)??throw new IOException("Native confirmation did not start");process.StandardInput.Close();
        _=process.StandardOutput.ReadToEndAsync();_=process.StandardError.ReadToEndAsync();process.Dispose();
    }
    internal static JsonElement? Read(CanonicalStore store,Pending pending)
    {
        if(!File.Exists(pending.OutputPath))return null;CanonicalStore.GuardPath(pending.InputPath);CanonicalStore.GuardPath(pending.OutputPath);
        if(CanonicalStore.Hash(File.ReadAllBytes(pending.InputPath))!=pending.InputHash||!store.ReadEvidence(pending.InputHash).SequenceEqual(File.ReadAllBytes(pending.InputPath)))throw new InvalidDataException("Native confirmation input changed");
        var value=DecisionProtocol.Parse(File.ReadAllText(pending.OutputPath));
        if(value.GetProperty("source").GetString()!="windows_native_dialog"||value.GetProperty("nonce").GetString()!=pending.Nonce||value.GetProperty("intent_hash").GetString()!=pending.IntentHash||value.GetProperty("review_receipt_hash").GetString()!=pending.ReviewHash||value.GetProperty("confirmed").ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new InvalidDataException("Native confirmation receipt mismatch");
        return value;
    }
}
