using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

// Complete non-ignored filesystem parity is retained locally. Only selected export evidence may leave the Host.
internal static class AttemptStartingState
{
    internal sealed record FileState(string Path,string? Hash);
    internal sealed record Snapshot(string Head,string IndexTree,string StartingDiffHash,FileState[] Files,string[] ParityDeviation);
    internal sealed record Saved(Snapshot State,string StateHash,string EventId);
    internal static async Task<byte[]> GitAsync(string root,string[] arguments,CancellationToken token=default) {
        CanonicalStore.GuardPath(root);
        var start=new ProcessStartInfo("git") {WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        var hooks=Path.Combine(Path.GetTempPath(),"localbrain-empty-hooks-"+Guid.NewGuid().ToString("N"));CanonicalStore.GuardPath(hooks);Directory.CreateDirectory(hooks);
        foreach(var arg in new[]{"-c","safe.directory="+root,"-c","core.hooksPath="+hooks,"-c","core.fsmonitor=false","-C",root})start.ArgumentList.Add(arg);
        foreach(var arg in arguments)start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("Git did not start");process.StandardInput.Close();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(30));
        async Task<byte[]> Read(Stream stream) {using var output=new MemoryStream();var buffer=new byte[8192];int count;while((count=await stream.ReadAsync(buffer,timeout.Token))>0){if(output.Length+count>48*1024*1024)throw new InvalidDataException("Git response exceeds parity budget");output.Write(buffer,0,count);}return output.ToArray();}
        try {var stdout=Read(process.StandardOutput.BaseStream);var stderr=Read(process.StandardError.BaseStream);await process.WaitForExitAsync(timeout.Token);var bytes=await stdout;_ = await stderr;if(process.ExitCode!=0)throw new InvalidDataException("Git parity operation failed");return bytes;}
        catch{if(!process.HasExited)process.Kill(true);throw;}
    }
    internal static async Task<Snapshot> CaptureAsync(string root,CanonicalStore? store,CancellationToken token=default) {
        root=Path.GetFullPath(root);CanonicalStore.GuardPath(root);
        var head=Encoding.UTF8.GetString(await GitAsync(root,["rev-parse","HEAD"],token)).Trim();
        var indexTree=Encoding.UTF8.GetString(await GitAsync(root,["write-tree"],token)).Trim();
        var stage=Encoding.UTF8.GetString(await GitAsync(root,["ls-files","--stage","-z"],token));
        var tracked=new List<string>();
        foreach(var line in stage.Split('\0',StringSplitOptions.RemoveEmptyEntries)) {
            var tab=line.IndexOf('\t');if(tab<0 || !line[..tab].EndsWith(" 0",StringComparison.Ordinal) || line.StartsWith("120000 ") || line.StartsWith("160000 "))
                throw new UnauthorizedAccessException("Linked/submodule/conflicted starting state is not qualified");
            tracked.Add(line[(tab+1)..]);
        }
        var untracked=Encoding.UTF8.GetString(await GitAsync(root,["ls-files","--others","--exclude-standard","-z"],token)).Split('\0',StringSplitOptions.RemoveEmptyEntries);
        var paths=tracked.Concat(untracked).Select(p=>HostEgress.NormalizePath(root,p)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        if(paths.Length>4096)throw new InvalidDataException("Starting state exceeds4096-file parity budget");
        long total=0;var files=new List<FileState>();
        foreach(var relative in paths) {
            var path=Path.Combine(root,relative);CanonicalStore.GuardPath(path);
            if(!File.Exists(path)){files.Add(new(relative,null));continue;}
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);total=checked(total+input.Length);
            if(total>48*1024*1024 || input.Length>16*1024*1024)throw new InvalidDataException("Starting state exceeds local parity byte budget");
            using var memory=new MemoryStream();await input.CopyToAsync(memory,token);var bytes=memory.ToArray();
            files.Add(new(relative,store?.PutEvidence(bytes)??CanonicalStore.Hash(bytes)));
        }
        var diff=await GitAsync(root,["diff","HEAD","--binary","--no-ext-diff","--no-textconv","--"],token);
        var diffHash=store?.PutEvidence(diff)??CanonicalStore.Hash(diff);
        var finalHead=Encoding.UTF8.GetString(await GitAsync(root,["rev-parse","HEAD"],token)).Trim();
        var finalIndex=Encoding.UTF8.GetString(await GitAsync(root,["write-tree"],token)).Trim();
        var finalNames=Encoding.UTF8.GetString(await GitAsync(root,["ls-files","--cached","--others","--exclude-standard","-z"],token)).Split('\0',StringSplitOptions.RemoveEmptyEntries)
            .Select(p=>HostEgress.NormalizePath(root,p)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        if(finalHead!=head || finalIndex!=indexTree || !paths.SequenceEqual(finalNames,StringComparer.Ordinal))throw new InvalidDataException("Repository changed during starting-state capture");
        foreach(var file in files) {
            var path=Path.Combine(root,file.Path);CanonicalStore.GuardPath(path);
            if(file.Hash!=(File.Exists(path)?CanonicalStore.Hash(File.ReadAllBytes(path)):null))throw new InvalidDataException("Source changed during starting-state capture");
        }
        return new(head,indexTree,diffHash,files.ToArray(),["Git-ignored environment/build/cache files are excluded from parity and export"]);
    }
    internal static string Hash(Snapshot snapshot)=>CanonicalStore.Hash(JsonSerializer.SerializeToUtf8Bytes(snapshot));
    internal static async Task<Saved> SaveAsync(HostTaskJournal journal,int attempt,CancellationToken token=default) {
        var state=await CaptureAsync(journal.WindowsRoot,journal.Canonical,token);
        if(state.Head!=journal.ExpectedHead)throw new InvalidDataException("Attempt starting HEAD drift");
        var hash=journal.Canonical.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(state));
        var fileHashes=state.Files.Select(f=>f.Hash).Where(h=>h is not null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
        foreach(var chunk in fileHashes.Chunk(100))journal.Canonical.Append(new(journal.TaskId,HostTaskJournal.WorkspaceId(journal.WindowsRoot),"windows_host",Guid.NewGuid().ToString("N"),"attempt_starting_files","local_parity","host_verified",chunk,JsonSerializer.SerializeToElement(new{attempt,starting_state_hash=hash})));
        var receipt=journal.Canonical.Append(new(journal.TaskId,HostTaskJournal.WorkspaceId(journal.WindowsRoot),"windows_host",Guid.NewGuid().ToString("N"),"attempt_starting_state","local_parity","host_verified",[hash,state.StartingDiffHash],JsonSerializer.SerializeToElement(new{attempt,base_commit=state.Head,starting_state_hash=hash,starting_diff_hash=state.StartingDiffHash})));
        return new(state,hash,receipt.EventId);
    }
    internal static async Task ReconstructAsync(string mainRoot,string rescueRoot,Snapshot snapshot,CanonicalStore store,CancellationToken token=default) {
        rescueRoot=Path.GetFullPath(rescueRoot);CanonicalStore.GuardPath(rescueRoot);
        if(CanonicalStore.Within(mainRoot,rescueRoot) || CanonicalStore.Within(rescueRoot,mainRoot) || Directory.Exists(rescueRoot))throw new UnauthorizedAccessException("Rescue must be a new independent worktree");
        Directory.CreateDirectory(Path.GetDirectoryName(rescueRoot)!);CanonicalStore.GuardPath(rescueRoot);
        await GitAsync(mainRoot,["worktree","add","--detach","--no-checkout",rescueRoot,snapshot.Head],token);
        await GitAsync(rescueRoot,["read-tree",snapshot.IndexTree],token);
        foreach(var file in snapshot.Files) {
            var relative=HostEgress.NormalizePath(rescueRoot,file.Path);var path=Path.Combine(rescueRoot,relative);CanonicalStore.GuardPath(path);
            if(file.Hash is null)continue;
            var bytes=store.ReadEvidence(file.Hash);Directory.CreateDirectory(Path.GetDirectoryName(path)!);CanonicalStore.GuardPath(path);
            using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);output.Write(bytes);output.Flush(true);
        }
        var actual=await CaptureAsync(rescueRoot,null,token);
        if(Hash(actual)!=Hash(snapshot))throw new InvalidDataException("Reconstructed rescue parity differs from attempt starting state");
    }
}