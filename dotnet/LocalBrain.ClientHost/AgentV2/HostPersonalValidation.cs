using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

// Explicit Host opt-in for trusted personal repositories. This is not a hostile-code sandbox.
internal static class HostPersonalValidation
{
    private static readonly SemaphoreSlim execution=new(1,1);
    private const int OutputLimit=2_000_000;
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static async Task<ToolRouter.TestResult> RunAsync(HostTaskJournal journal,string windowsRoot,string wslRoot,
        RequiredTest test,HostValidationIsolation.RescueBinding? rescue,CancellationToken token,Action<Process>? afterStarted=null)
    {
        if(!OperatingSystem.IsWindows() || HostTaskJournal.Current!=journal || !journal.PersonalValidationEnabled)
            throw new UnauthorizedAccessException("Trusted personal validation requires explicit current Windows Host configuration");
        if(test.Kind is not ("git_diff_check" or "npm_test") || test.Target is not null ||
            !AgentTaskRequest.Parse(journal.Canonical.TaskRequest(journal.TaskId)).RequiredTests.Contains(test))
            throw new UnauthorizedAccessException("Only task-requested named personal validations are allowed");
        await execution.WaitAsync(token);
        try
        {
            var root=Path.GetFullPath(windowsRoot);
            await HostValidationIsolation.RequireWorkspaceAsync(journal,root,rescue,token);
            var before=await AttemptStartingState.CaptureAsync(root,null,token);
            if(before.Head!=journal.ExpectedHead)throw new UnauthorizedAccessException("Personal validation source HEAD drift");
            ProcessStartInfo start;
            if(test.Kind=="git_diff_check")
            {
                var git=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Git","cmd","git.exe");
                if(!File.Exists(git))throw new FileNotFoundException("Registered Windows Git installation is missing");
                start=new(git);
                foreach(var arg in new[]{"-c","safe.directory="+root,"-c","core.fsmonitor=false","-C",root,"diff","HEAD","--check","--no-ext-diff","--no-textconv"})start.ArgumentList.Add(arg);
            }
            else
            {
                string resolved;string[] gitEnvironment=[];
                if(rescue is null)resolved=await WslWorkspace.ResolveRegisteredMainAsync(journal.Canonical,HostTaskJournal.WorkspaceId(root),token);
                else
                {
                    var ticket=JsonSerializer.Deserialize<HostRescue.Ticket>(journal.Canonical.ReadEvidence(rescue.TicketHash))??throw new InvalidDataException("Missing Rescue ticket");
                    resolved=await WslWorkspace.ResolveRegisteredRescueAsync(journal.Canonical,ticket.WorkspaceId,token);
                    gitEnvironment=await WslWorkspace.RescueGitEnvironmentAsync(journal.Canonical,ticket.WorkspaceId,token);
                }
                if(!string.Equals(resolved,wslRoot,StringComparison.Ordinal))throw new UnauthorizedAccessException("Personal validation WSL root mismatch");
                start=new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"wsl.exe"));
                string[] arguments=["-d","Ubuntu","--cd",resolved,"--exec","/usr/bin/env",
                    "PATH=/home/worker/.volta/bin:/usr/local/bin:/usr/bin:/bin","CI=1","NPM_CONFIG_UPDATE_NOTIFIER=false",
                    ..gitEnvironment,"/usr/bin/timeout","--kill-after=5s","300s","/home/worker/.volta/bin/npm","test"];
                foreach(var arg in arguments)start.ArgumentList.Add(arg);
            }
            start.WorkingDirectory=root;start.UseShellExecute=false;start.CreateNoWindow=true;
            start.RedirectStandardInput=true;start.RedirectStandardOutput=true;start.RedirectStandardError=true;
            // Do not inherit Git overrides from a shell; Rescue Linux overrides are derived above by the Host.
            foreach(var key in start.Environment.Keys.Where(k=>k.StartsWith("GIT_",StringComparison.OrdinalIgnoreCase)).ToArray())start.Environment.Remove(key);
            journal.Record("trusted_personal_validation_started",new{test.Kind,test.Target,source_state_hash=AttemptStartingState.Hash(before),rescue,
                execution_owner="windows_host",hostile_repository_isolation_guaranteed=false,combined_output_limit=OutputLimit,deadline_seconds=310},"host_verified");
            var watch=Stopwatch.StartNew();
            using var process=Process.Start(start)??throw new IOException("Personal validation process failed to start");process.StandardInput.Close();
            using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(token);cancellation.CancelAfter(TimeSpan.FromSeconds(310));
            using var stdout=new MemoryStream();using var stderr=new MemoryStream();
            long total=0;var outputExceeded=0;var reason="completed";
            async Task Drain(Stream input,MemoryStream output)
            {
                var buffer=new byte[8192];
                while(true)
                {
                    var n=await input.ReadAsync(buffer,cancellation.Token);if(n==0)return;
                    var end=Interlocked.Add(ref total,n);var accepted=(int)Math.Clamp(OutputLimit-(end-n),0,n);
                    if(accepted>0)output.Write(buffer,0,accepted);
                    if(end>OutputLimit){Interlocked.Exchange(ref outputExceeded,1);cancellation.Cancel();return;}
                }
            }
            var outTask=Drain(process.StandardOutput.BaseStream,stdout);var errTask=Drain(process.StandardError.BaseStream,stderr);
            async Task<Exception?> StopOwnedAsync()
            {
                var failures=new List<Exception>();
                try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch(Exception e){failures.Add(e);}
                try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}catch(Exception e){failures.Add(e);}
                return failures.Count==0?null:new AggregateException("Owned validation process stop/reap failed",failures);
            }
            void Failure(Exception original,Exception? cleanup)
            {
                journal.Record("trusted_personal_validation_failed",new{test.Kind,error_type=original.GetType().Name,
                    cleanup_error_type=cleanup?.GetType().Name,owned_process_exit_confirmed=process.HasExited,
                    stdout_evidence=journal.Canonical.PutEvidence(stdout.ToArray()),stderr_evidence=journal.Canonical.PutEvidence(stderr.ToArray())},"host_verified");
            }
            try{afterStarted?.Invoke(process);await Task.WhenAll(outTask,errTask,process.WaitForExitAsync(cancellation.Token));}
            catch(OperationCanceledException)
            {
                reason=outputExceeded!=0?"output_limit":token.IsCancellationRequested?"cancelled":"deadline";
                cancellation.Cancel();var cleanup=await StopOwnedAsync();
                try{await Task.WhenAll(outTask,errTask);}catch(OperationCanceledException){}
                if(cleanup is not null){Failure(new OperationCanceledException(reason),cleanup);throw cleanup;}
            }
            catch(Exception e)
            {
                cancellation.Cancel();var cleanup=await StopOwnedAsync();
                try{await Task.WhenAll(outTask,errTask);}catch(Exception){} // Preserve the original failure; no pipe task remains active.
                Failure(e,cleanup);
                if(cleanup is not null)throw new AggregateException(e,cleanup);
                throw;
            }
            var exit=process.ExitCode;
            if(test.Kind=="npm_test" && exit is (124 or 137) && reason=="completed")reason="deadline";
            await HostValidationIsolation.RequireWorkspaceAsync(journal,root,rescue,CancellationToken.None);
            var after=await AttemptStartingState.CaptureAsync(root,null,CancellationToken.None);
            if(AttemptStartingState.Hash(before)!=AttemptStartingState.Hash(after))reason="source_changed";
            var outBytes=stdout.ToArray();var errBytes=stderr.ToArray();
            var passed=reason=="completed" && exit==0;
            var result=new ToolRouter.TestResult(test.Kind,"trusted_personal_"+(test.Kind=="npm_test"?"wsl":"windows"),passed,exit,
                outBytes.LongLength,errBytes.LongLength,Hash(outBytes),Hash(errBytes),watch.ElapsedMilliseconds,null,
                passed?[]:[reason=="completed"?"process_exit_nonzero":reason]);
            var outHash=journal.Canonical.PutEvidence(outBytes);var errHash=journal.Canonical.PutEvidence(errBytes);
            journal.Record("trusted_personal_validation_result",new{result,stdout_evidence=outHash,stderr_evidence=errHash,reason,
                source_before=AttemptStartingState.Hash(before),source_after=AttemptStartingState.Hash(after),
                stdout_truncated=outputExceeded!=0,wslexecution_note="Trusted scripts only; timeout bounds foreground group, detached Linux processes are not hostile-isolated"},"host_verified");
            journal.Record("required_test_result",result,"host_verified");
            return result;
        }
        finally{execution.Release();}
    }
}
