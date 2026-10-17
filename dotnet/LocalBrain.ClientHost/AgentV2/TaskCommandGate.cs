using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalBrain.ClientHost.AgentV2;

internal sealed class TaskCommandGate : IAsyncDisposable
{
    internal sealed record OutputFile(string Path,long Bytes,string Sha256,string? UntrustedTextPreview=null);
    internal sealed record Receipt(string OperationId,string Fingerprint,string Nonce,string State,int? ExitCode,
        string StdoutHash,string StderrHash,long StdoutBytes,long StderrBytes,long ElapsedMs,OutputFile[] Outputs,bool ExpectedOutputsPresent);
    private sealed class Running(TaskCommandRequest request,string directory,string fingerprint,string nonce)
    {
        internal readonly TaskCommandRequest Request=request;internal readonly string Directory=directory,Fingerprint=fingerprint,Nonce=nonce;
        internal readonly Stopwatch Watch=Stopwatch.StartNew();internal readonly CancellationTokenSource Stop=new();
        internal Task Work=Task.CompletedTask;internal Receipt? Result;internal int? Pid;internal long Bytes;
    }
    private readonly HostTaskJournal journal;private readonly TaskCommandContract contract;private readonly TaskCommandAuthorization.Grant grant;
    private readonly Dictionary<string,Running> live=new(StringComparer.Ordinal);private readonly HashSet<string> imported=new();
    private readonly SemaphoreSlim gate=new(1,1);private bool disposed;
    internal string Root {get;}
    internal TaskCommandGate(HostTaskJournal journal,AgentTaskRequest request)
    {
        this.journal=journal;contract=request.CommandExecution??throw new UnauthorizedAccessException("No task command contract");
        grant=TaskCommandAuthorization.Require(journal,request);Root=Path.Combine(journal.Canonical.Root,"command-jobs",journal.TaskId);
        CanonicalStore.GuardPath(Root);Directory.CreateDirectory(Root);CanonicalStore.GuardPath(Root);
        journal.Canonical.PutEvidence(File.ReadAllBytes(grant.Pending.Input));journal.Canonical.PutEvidence(File.ReadAllBytes(grant.Pending.Output));
        journal.Record("task_command_grant_bound",new{task_id=journal.TaskId,grant_input_hash=grant.Pending.InputHash,grant_receipt_hash=grant.ReceiptHash,
            contract,trust="personal trusted project code; not a hostile-code sandbox",lifetime="same exact task until completion/cancellation; finite active execution budget"},"human_confirmed");
    }
    internal async Task<object> StartAsync(TaskCommandRequest request,CancellationToken token)
    {
        await gate.WaitAsync(token);try{
            ObjectDisposedException.ThrowIf(disposed,this);TaskCommandAuthorization.Validate(grant);request.Validate(contract);
            var cwd=ResolveCwd(request.WorkingDirectory);_ = ResolveOutputs(cwd,request.ExpectedOutputs??[]);
            var fingerprint=CanonicalStore.Hash(JsonSerializer.SerializeToUtf8Bytes(new{request.Environment,request.Program,request.Arguments,cwd,request.ExpectedOutputs,request.TimeoutSeconds,grant.ReceiptHash}));
            var directory=Path.Combine(Root,request.OperationId);CanonicalStore.GuardPath(directory);
            if(Directory.Exists(directory)){
                var manifest=ReadJson(Path.Combine(directory,"manifest.json"));
                if(manifest.GetProperty("fingerprint").GetString()!=fingerprint)throw new UnauthorizedAccessException("Operation ID belongs to another command; immutable receipt retained");
                return StatusCore(request.OperationId);
            }
            foreach(var existing in Directory.EnumerateDirectories(Root)){
                var manifest=ReadJson(Path.Combine(existing,"manifest.json"));
                if(manifest.GetProperty("fingerprint").GetString()==fingerprint && !File.Exists(Path.Combine(existing,"receipt.json")) && !live.ContainsKey(Path.GetFileName(existing)))
                    return new{state="outcome_unknown",operation_id=Path.GetFileName(existing),automatic_retry=false,message="A prior identical command has an intent but no confirmed terminal outcome. Inspect its process/output before any new attempt."};
            }
            if(live.Values.Any(r=>!r.Work.IsCompleted))return new{state="busy",automatic_retry=false,message="One owned command is still running; observe it instead of launching a duplicate"};
            if(Directory.EnumerateDirectories(Root).Count()>=contract.MaxCommands)throw new InvalidOperationException("Task command count budget exhausted");
            var stored=Directory.EnumerateFiles(Root,"*",SearchOption.AllDirectories).Sum(p=>new FileInfo(p).Length);
            if(stored+contract.MaxOutputBytes>128L*1024*1024 || new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace<1024L*1024*1024)
                throw new IOException("Task command output storage admission failed");
            Directory.CreateDirectory(directory);var nonce=Guid.NewGuid().ToString("N");
            WriteNew(Path.Combine(directory,"manifest.json"),new{task_id=journal.TaskId,request_hash=grant.Pending.RequestHash,grant_receipt_hash=grant.ReceiptHash,fingerprint,nonce,command=request,cwd,created_at=DateTimeOffset.UtcNow});
            journal.Record("command_launch_intent",new{operation_id=request.OperationId,fingerprint,nonce,environment=request.Environment,cwd,automatic_retry=false},"host_verified");
            var running=new Running(request,directory,fingerprint,nonce);live.Add(request.OperationId,running);running.Work=RunAsync(running,cwd);
            return StatusCore(request.OperationId);
        }finally{gate.Release();}
    }
    internal async Task<object> StatusAsync(string id,bool cancel,CancellationToken token)
    {
        await gate.WaitAsync(token);try{
            ObjectDisposedException.ThrowIf(disposed,this);ValidateId(id);
            if(cancel){TaskCommandAuthorization.Validate(grant);if(live.TryGetValue(id,out var job))job.Stop.Cancel();}
            return StatusCore(id);
        }finally{gate.Release();}
    }
    private object StatusCore(string id)
    {
        ValidateId(id);var dir=Path.Combine(Root,id);CanonicalStore.GuardPath(dir);if(!Directory.Exists(dir))throw new InvalidDataException("Unknown command operation");
        if(live.TryGetValue(id,out var current)&&!current.Work.IsCompleted)return new{operation_id=id,state="running",process_id=current.Pid,elapsed_ms=current.Watch.ElapsedMilliseconds,output_bytes=Interlocked.Read(ref current.Bytes),automatic_retry=false};
        var path=Path.Combine(dir,"receipt.json");if(!File.Exists(path))return new{operation_id=id,state="outcome_unknown",automatic_retry=false,message="The previous Host owner ended without a terminal receipt; the command will not be replayed"};
        var receipt=ReadReceipt(dir);var hash=CanonicalStore.Hash(File.ReadAllBytes(path));
        if(imported.Add(hash))journal.CommitCommandReceipt(id,File.ReadAllBytes(path),File.ReadAllBytes(Path.Combine(dir,"stdout.bin")),File.ReadAllBytes(Path.Combine(dir,"stderr.bin")));
        return new{operation_id=id,state=receipt.State,exit_code=receipt.ExitCode,receipt_hash=hash,stdout_hash=receipt.StdoutHash,stderr_hash=receipt.StderrHash,
            stdout_preview=Preview(Path.Combine(dir,"stdout.bin")),stderr_preview=Preview(Path.Combine(dir,"stderr.bin")),preview_encoding="utf8_lossy; raw bytes retained by hash",receipt.ElapsedMs,
            outputs=receipt.Outputs.Take(4).ToArray(),output_count=receipt.Outputs.Length,receipt.ExpectedOutputsPresent,host_command_progress=receipt.State=="completed"&&receipt.ExitCode==0&&receipt.ExpectedOutputsPresent,automatic_retry=false};
    }
    internal bool VerifyToolProgress(string id,string hash)
    {
        if(id is null || !System.Text.RegularExpressions.Regex.IsMatch(id,@"^[A-Za-z0-9_-]{1,80}$"))return false;
        var path=Path.Combine(Root,id,"receipt.json");return File.Exists(path)&&CanonicalStore.Hash(File.ReadAllBytes(path))==hash&&Successful(ReadReceipt(Path.GetDirectoryName(path)!));
    }
    internal bool HasSuccessfulCommand=>Directory.EnumerateDirectories(Root).Any(d=>File.Exists(Path.Combine(d,"receipt.json"))&&Successful(ReadReceipt(d)));
    internal string InventoryHash()=>InventoryHash(journal.Canonical.Root,journal.TaskId);
    internal static string InventoryHash(string control,string taskId)
    {
        var root=Path.Combine(control,"command-jobs",taskId);
        var records=Directory.Exists(root)?Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal).SelectMany(d=>new[]{"manifest.json","receipt.json","stdout.bin","stderr.bin"}.Select(name=>{
            var path=Path.Combine(d,name);return new{path=Path.GetFileName(d)+"/"+name,hash=File.Exists(path)?FileHash(path):"missing"};})).ToArray():[];
        return CanonicalStore.Hash(JsonSerializer.SerializeToUtf8Bytes(records));
    }
    internal int SuccessfulCommands=>Directory.EnumerateDirectories(Root).Count(d=>File.Exists(Path.Combine(d,"receipt.json"))&&Successful(ReadReceipt(d)));
    internal async Task SettleAsync(CancellationToken token)
    {
        var tasks=live.Values.Select(r=>r.Work).ToArray();await Task.WhenAll(tasks).WaitAsync(token);
        foreach(var id in live.Keys.ToArray())_ = await StatusAsync(id,false,token);
    }
    internal ToolRouter.TestResult Test(string id)
    {
        ValidateId(id);var dir=Path.Combine(Root,id);var receipt=ReadReceipt(dir);var ok=Successful(receipt);
        return new("command_exit","windows_host_owned_command_receipt",ok,receipt.ExitCode??-1,receipt.StdoutBytes,receipt.StderrBytes,receipt.StdoutHash,receipt.StderrHash,receipt.ElapsedMs,null,ok?[]:["command_"+receipt.State]);
    }
    internal string Summary()
    {
        var results=Directory.EnumerateDirectories(Root).Where(d=>File.Exists(Path.Combine(d,"receipt.json"))).Select(d=>ReadReceipt(d)).Take(64);
        var text=JsonSerializer.Serialize(results,ClientConfig.JsonOptions);return text.Length<=12000?text:JsonSerializer.Serialize(new{command_count=Directory.EnumerateDirectories(Root).Count(),detail="Command evidence is available by host_recall; full packet exceeds preview budget"});
    }
    private async Task RunAsync(Running running,string cwd)
    {
        await Task.Yield();var state="launch_error";int? exit=null;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(running.Stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(running.Request.TimeoutSeconds??contract.MaxCommandSeconds));
        try{
            TaskCommandAuthorization.Validate(grant);
            if(running.Request.Environment=="wsl") (state,exit)=await RunWslAsync(running,cwd,deadline.Token);
            else (state,exit)=await RunWindowsAsync(running,cwd,deadline.Token);
        }catch(OperationCanceledException){state=running.Stop.IsCancellationRequested?"cancelled":"timeout";}
        catch(Exception e) when(e is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException){state="launch_or_protocol_error_"+e.GetType().Name;}
        finally{
            foreach(var name in new[]{"stdout.bin","stderr.bin"})if(!File.Exists(Path.Combine(running.Directory,name)))using(File.Create(Path.Combine(running.Directory,name))){}
            var stdout=Path.Combine(running.Directory,"stdout.bin");var stderr=Path.Combine(running.Directory,"stderr.bin");
            var outputs=new List<OutputFile>();var expected=true;long outputBytes=0;
            using var verifyDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            foreach(var path in ResolveOutputs(cwd,running.Request.ExpectedOutputs??[])){
                if(!File.Exists(path)){expected=false;continue;}CanonicalStore.GuardPath(path);
                var info=new FileInfo(path);outputBytes+=info.Length;
                if(info.Length>128L*1024*1024||outputBytes>512L*1024*1024){expected=false;state="expected_output_verification_limit";break;}
                try{using var file=File.OpenRead(path);var hash=Convert.ToHexString(await SHA256.HashDataAsync(file,verifyDeadline.Token)).ToLowerInvariant();
                    string? preview=null;if(outputs.Count<8){file.Position=0;var sample=new byte[(int)Math.Min(256,file.Length)];await file.ReadExactlyAsync(sample,verifyDeadline.Token);preview=Encoding.UTF8.GetString(sample);}
                    outputs.Add(new(path,info.Length,hash,preview));}
                catch(OperationCanceledException){expected=false;state="expected_output_verification_timeout";break;}
            }
            var receipt=new Receipt(running.Request.OperationId,running.Fingerprint,running.Nonce,state,exit,FileHash(stdout),FileHash(stderr),new FileInfo(stdout).Length,new FileInfo(stderr).Length,running.Watch.ElapsedMilliseconds,outputs.ToArray(),expected);
            WriteNew(Path.Combine(running.Directory,"receipt.json"),receipt);running.Result=receipt;
        }
    }
    private async Task<(string,int?)> RunWindowsAsync(Running running,string cwd,CancellationToken token)
    {
        var start=new ProcessStartInfo(running.Request.Program){WorkingDirectory=cwd,UseShellExecute=false,CreateNoWindow=true};foreach(var a in running.Request.Arguments)start.ArgumentList.Add(a);
        using var child=WindowsChildJob.Start(start,allowNoArguments:true);running.Pid=child.Process.Id;
        WriteNew(Path.Combine(running.Directory,"process.json"),new{pid=child.Process.Id,birth=child.Process.StartTime.ToUniversalTime(),nonce=running.Nonce});
        using var overflow=CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task Pump(Stream input,string name){await using var file=new FileStream(Path.Combine(running.Directory,name),FileMode.CreateNew,FileAccess.Write,FileShare.Read);var buffer=new byte[8192];int n;
            while((n=await input.ReadAsync(buffer,overflow.Token))>0){var total=Interlocked.Add(ref running.Bytes,n);var accepted=(int)Math.Min(n,Math.Max(0,contract.MaxOutputBytes-(total-n)));if(accepted>0)await file.WriteAsync(buffer.AsMemory(0,accepted),CancellationToken.None);if(total>contract.MaxOutputBytes){overflow.Cancel();break;}}}
        var outTask=Pump(child.Output.BaseStream,"stdout.bin");var errTask=Pump(child.Error.BaseStream,"stderr.bin");
        var state="completed";int? exit=null;
        try{await child.Process.WaitForExitAsync(overflow.Token);exit=child.Process.ExitCode;}
        catch(OperationCanceledException){state=running.Bytes>contract.MaxOutputBytes?"output_limit":running.Stop.IsCancellationRequested?"cancelled":"timeout";}
        finally{child.Terminate();try{await Task.WhenAll(outTask,errTask).WaitAsync(TimeSpan.FromSeconds(5));}catch(OperationCanceledException){}catch(IOException) when(state!="completed"){} }
        return(state,exit);
    }
    private async Task<(string,int?)> RunWslAsync(Running running,string cwd,CancellationToken token)
    {
        var script=await WslWorkspace.MapFileAsync(Path.Combine(AppContext.BaseDirectory,"scripts","host-command-worker.py"),token);
        var lease=Path.Combine(running.Directory,"lease.json");var nonce=running.Nonce;
        void Renew(bool cancel=false)=>WriteReplace(lease,new{nonce,expires_at=DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeSeconds(),cancel,owner_pid=Environment.ProcessId});
        Renew();var linuxPlan=Path.Combine(running.Directory,"linux-plan.json");
        var plan=new{program=running.Request.Program,arguments=running.Request.Arguments,cwd=await WslWorkspace.MapPathAsync(cwd,token),nonce,
            lease=await WslWorkspace.MapPathAsync(lease,token),stdout=await WslWorkspace.MapPathAsync(Path.Combine(running.Directory,"stdout.bin"),token),
            stderr=await WslWorkspace.MapPathAsync(Path.Combine(running.Directory,"stderr.bin"),token),receipt=await WslWorkspace.MapPathAsync(Path.Combine(running.Directory,"linux-receipt.json"),token),
            started=await WslWorkspace.MapPathAsync(Path.Combine(running.Directory,"linux-started.json"),token),timeout_seconds=running.Request.TimeoutSeconds??contract.MaxCommandSeconds,max_output_bytes=contract.MaxOutputBytes};
        WriteNew(linuxPlan,plan);var mappedPlan=await WslWorkspace.MapFileAsync(linuxPlan,token);
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","wsl.exe")){UseShellExecute=false,CreateNoWindow=true};
        foreach(var a in new[]{"-d","Ubuntu","--exec","/usr/bin/python3","-u",script,mappedPlan})start.ArgumentList.Add(a);
        using var child=WindowsChildJob.Start(start);running.Pid=child.Process.Id;
        var outDrain=child.Output.ReadToEndAsync();var errDrain=child.Error.ReadToEndAsync();
        try{
            while(!child.Process.HasExited){token.ThrowIfCancellationRequested();TaskCommandAuthorization.Validate(grant);Renew();
                running.Bytes=new[]{"stdout.bin","stderr.bin"}.Select(n=>Path.Combine(running.Directory,n)).Where(File.Exists).Sum(p=>new FileInfo(p).Length);await Task.Delay(500,token);}
        }catch(OperationCanceledException){Renew(true);try{await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));}catch(TimeoutException){}throw;}
        finally{child.Terminate();try{await Task.WhenAll(outDrain,errDrain).WaitAsync(TimeSpan.FromSeconds(5));
            WriteNew(Path.Combine(running.Directory,"supervisor-diagnostic.json"),new{stdout=outDrain.Result[..Math.Min(outDrain.Result.Length,4096)],stderr=errDrain.Result[..Math.Min(errDrain.Result.Length,4096)]});}catch(TimeoutException){} }
        var receipt=ReadJson(Path.Combine(running.Directory,"linux-receipt.json"));
        if(receipt.GetProperty("nonce").GetString()!=nonce||receipt.GetProperty("plan_sha256").GetString()!=FileHash(linuxPlan))throw new InvalidDataException("WSL supervisor receipt mismatch");
        return(receipt.GetProperty("state").GetString()!,receipt.GetProperty("exit_code").ValueKind==JsonValueKind.Number?receipt.GetProperty("exit_code").GetInt32():null);
    }
    private string ResolveCwd(string value)
    {
        var full=Path.GetFullPath(value,journal.WindowsRoot);CanonicalStore.GuardPath(full);
        if(!Directory.Exists(full)||!CanonicalStore.Within(journal.WindowsRoot,full)||!contract.WorkingDirectories.Any(p=>CanonicalStore.Within(Path.GetFullPath(p,journal.WindowsRoot),full)))throw new UnauthorizedAccessException("Working directory is outside this task grant");return full;
    }
    private string[] ResolveOutputs(string cwd,string[] paths)
    {
        var roots=new[]{journal.WindowsRoot}.Concat(contract.OutputRoots(journal.WindowsRoot)).ToArray();
        return paths.Select(p=>{var full=Path.GetFullPath(p,cwd);CanonicalStore.GuardPath(full);if(!roots.Any(r=>CanonicalStore.Within(r,full)))throw new UnauthorizedAccessException("Expected output is outside the declared task output roots");return full;}).ToArray();
    }
    private Receipt ReadReceipt(string directory)
    {
        var m=ReadJson(Path.Combine(directory,"manifest.json"));var r=JsonSerializer.Deserialize<Receipt>(File.ReadAllBytes(Path.Combine(directory,"receipt.json")),ClientConfig.JsonOptions)??throw new InvalidDataException("Missing command receipt");
        if(m.GetProperty("task_id").GetString()!=journal.TaskId||m.GetProperty("fingerprint").GetString()!=r.Fingerprint||m.GetProperty("nonce").GetString()!=r.Nonce||
            r.StdoutHash!=FileHash(Path.Combine(directory,"stdout.bin"))||r.StderrHash!=FileHash(Path.Combine(directory,"stderr.bin")))throw new InvalidDataException("Command receipt/log identity changed");
        return r;
    }
    private static bool Successful(Receipt receipt)=>receipt.State=="completed"&&receipt.ExitCode==0&&receipt.ExpectedOutputsPresent&&receipt.Outputs.All(f=>File.Exists(f.Path)&&new FileInfo(f.Path).Length==f.Bytes&&FileHash(f.Path)==f.Sha256);
    private static void ValidateId(string id){if(!System.Text.RegularExpressions.Regex.IsMatch(id??"",@"^[A-Za-z0-9_-]{1,80}$"))throw new InvalidDataException("Invalid command operation ID");}
    private static JsonElement ReadJson(string path){CanonicalStore.GuardPath(path);var bytes=File.ReadAllBytes(path);if(bytes.Length>1024*1024)throw new InvalidDataException("Oversized command record");using var d=JsonDocument.Parse(bytes);return d.RootElement.Clone();}
    private static string Preview(string path){using var f=File.OpenRead(path);var b=new byte[(int)Math.Min(f.Length,512)];f.ReadExactly(b);return Encoding.UTF8.GetString(b);}
    private static string FileHash(string path){CanonicalStore.GuardPath(path);using var f=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();}
    internal static void WriteNew(string path,object value){CanonicalStore.GuardPath(path);var temp=path+".pending";using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){f.Write(JsonSerializer.SerializeToUtf8Bytes(value,ClientConfig.JsonOptions));f.Flush(true);}File.Move(temp,path);}
    internal static void WriteReplace(string path,object value){CanonicalStore.GuardPath(path);var temp=path+".next";using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){f.Write(JsonSerializer.SerializeToUtf8Bytes(value,ClientConfig.JsonOptions));f.Flush(true);}File.Move(temp,path,true);}
    public async ValueTask DisposeAsync(){await gate.WaitAsync();try{if(disposed)return;disposed=true;foreach(var job in live.Values)job.Stop.Cancel();await Task.WhenAll(live.Values.Select(j=>j.Work));foreach(var id in live.Keys.ToArray())_ = StatusCore(id);foreach(var job in live.Values)job.Stop.Dispose();}finally{gate.Release();}}
}
