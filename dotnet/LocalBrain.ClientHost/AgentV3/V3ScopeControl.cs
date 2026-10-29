using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LocalBrain.ClientHost.AgentV3;

// Experimental request construction only. Distinct generation names prevent
// accidental reuse but do not prove ownership or cgroup emptiness. The Host
// must verify its owner token and pinned scope before issuing a stop request,
// terminate the separate Windows relay, and reconcile unknown outcomes.
internal static class V3ScopeControl
{
    internal static string ScopeName(string taskId, string generationId)
    {
        if(!Guid.TryParseExact(taskId,"D",out var parsed) || parsed.ToString("D")!=taskId)
            throw new ArgumentException("Task ID must be a canonical UUID",nameof(taskId));
        if(!Guid.TryParseExact(generationId,"D",out var generation) || generation.ToString("D")!=generationId)
            throw new ArgumentException("Generation ID must be a canonical UUID",nameof(generationId));
        return "lb-v3-"+taskId+"-"+generationId+".scope";
    }

    internal static string[] StopArguments(string distro,string taskId,string generationId)
    {
        if(string.IsNullOrEmpty(distro) || !Regex.IsMatch(distro,@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,63}\z",RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid WSL distribution name",nameof(distro));
        return ["-d",distro,"--exec","/usr/bin/systemctl","--user","stop","--",ScopeName(taskId,generationId)];
    }

    internal static async Task RequestStopAsync(string distro,string taskId,string generationId,CancellationToken token)
    {
        var arguments=StopArguments(distro,taskId,generationId);
        token.ThrowIfCancellationRequested();
        if(!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WSL Host control requires Windows");
        var executable=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"wsl.exe");
        var start=new ProcessStartInfo(executable) {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,
            RedirectStandardOutput=true,RedirectStandardError=true
        };
        foreach(var argument in arguments)start.ArgumentList.Add(argument);
        // A stop command needs no Host capability or model credentials.
        start.Environment.Remove("WSLENV");
        start.Environment.Remove("LOCALBRAIN_SIDEFX_NONCE");
        start.Environment.Remove("LOCALBRAIN_SIDEFX_PIPE");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var process=new Process { StartInfo=start };
        var started=false;
        Task output=Task.CompletedTask;
        Task error=Task.CompletedTask;
        try {
            deadline.Token.ThrowIfCancellationRequested();
            started=process.Start();
            if(!started)throw new IOException("WSL scope stop could not start");
            process.StandardInput.Close();
            output=DrainAsync(process.StandardOutput.BaseStream,deadline.Token);
            error=DrainAsync(process.StandardError.BaseStream,deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(output,error).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if(process.ExitCode!=0)throw new IOException("WSL scope stop was not confirmed");
        } catch {
            deadline.Cancel();
            using var cleanupDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // Cleanup is best effort and must not replace the original failure.
            if(started) {
                try { if(!process.HasExited)process.Kill(entireProcessTree:true); }
                catch { }
                try {
                    await process.WaitForExitAsync(cleanupDeadline.Token).ConfigureAwait(false);
                } catch { }
            }
            // Observe both reads even if cleanup times out. Disposing the process
            // also closes its redirected streams when this method leaves.
            var drains=Task.WhenAll(ObserveFailureAsync(output),ObserveFailureAsync(error));
            try {
                await drains.WaitAsync(cleanupDeadline.Token).ConfigureAwait(false);
            } catch(OperationCanceledException) { }
            // A failed or cancelled stop has unknown outcome. Cleanup does not
            // confirm Linux scope termination or stop the separate Windows relay.
            // The caller must reconcile the task rather than report a confirmed cancellation.
            throw;
        }
    }

    private static async Task ObserveFailureAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
    }

    private static async Task DrainAsync(Stream stream,CancellationToken token)
    {
        var buffer=new byte[4096];
        while(await stream.ReadAsync(buffer,token).ConfigureAwait(false)!=0) { }
    }
}
