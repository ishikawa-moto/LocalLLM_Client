using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalBrain.ClientHost.AgentV2;

internal sealed class SideEffectGate : IAsyncDisposable
{
    internal sealed record Request(string Pipe, string Nonce, string Operation, string? Path = null,
        string? Content = null, string? OldText = null, string? NewText = null,
        int PayloadTokens = 0, int ContextLimit = 32768, string? PayloadJson = null,
        string? EvidenceHash=null,int Offset=0,string? SnapshotJson=null,TaskCommandRequest? Command=null,string? OperationId=null,string? Query=null,
        string? ToolName=null,string? ToolCallId=null);
    internal sealed record Reply(bool Ok, string Status, string? ContentHash = null, bool Handoff = false,
        int Headroom = 0,string? Text=null,object? CommandState=null);
    internal sealed record Budget(bool Handoff, int Headroom);
    private sealed record FusionState(string Fingerprint,string Phase,string? EditHash=null);
    internal static Budget EvaluateHostMeasuredBudget(int payloadTokens, int contextLimit = 32768) =>
        EvaluateBudget(payloadTokens, contextLimit, nextTurn: 0, toolResult: 0, handoffPacket: 0);
    internal static Budget EvaluateBudget(int payloadTokens, int contextLimit = 32768,
        int outputReserve = 2048, int safetyReserve = 2048, int nextTurn = 1024,
        int toolResult = 4096, int handoffPacket = 4096)
    {
        if (contextLimit != 32768 || payloadTokens < 1 || payloadTokens > contextLimit * 4 ||
            new[]{outputReserve,safetyReserve,nextTurn,toolResult,handoffPacket}.Any(n=>n<0))
            throw new InvalidDataException("Invalid context budget measurement");
        var headroom = contextLimit - payloadTokens - outputReserve - safetyReserve;
        return new(headroom < nextTurn + toolResult + handoffPacket, headroom);
    }
    public string PipeName { get; } = "localbrain-sidefx-" + Guid.NewGuid().ToString("N");
    public string Nonce { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public bool HandoffRequested { get; private set; }
    internal bool OwnsJournal(HostTaskJournal owner)=>ReferenceEquals(journal,owner);
    private readonly string root;
    private readonly string[] writeScope;
    private readonly string expectedHead;
    private readonly bool canHandoff;
    private readonly HostTaskJournal? journal;
    private readonly Action<string>? fault;
    private readonly CancellationTokenSource stop = new();
    private readonly Task server;
    private readonly SemaphoreSlim operationLock = new(1,1);
    private string diagnosticStage="request";
    private string? admittedPayloadHash;

    public SideEffectGate(string root, string[] writeScope, string expectedHead, bool canHandoff,
        HostTaskJournal? journal = null,Action<string>? fault=null)
    {
        this.root=System.IO.Path.GetFullPath(root); this.writeScope=writeScope;
        this.expectedHead=expectedHead; this.canHandoff=canHandoff; this.journal=journal;
        this.fault=fault;
        CanonicalStore.GuardPath(this.root);
        server=ServeAsync();
    }

    internal Task<Reply> HandleAsync(Request request, CancellationToken token = default) => HandleCoreAsync(request,false,token);
    internal Task<Reply> HandleHostMeasuredPreflightAsync(Request request, CancellationToken token = default)
    {
        if(request.Operation!="preflight" || request.SnapshotJson is not null)throw new InvalidDataException("Invalid Host measurement");
        if(journal?.Commands is null || !journal.HasApproval)throw new UnauthorizedAccessException("Task command grant unavailable or expired");
        return HandleCoreAsync(request,true,token);
    }
    private async Task<Reply> HandleCoreAsync(Request request,bool hostMeasured,CancellationToken token)
    {
        if(request.Pipe!=PipeName || request.Nonce is null || request.Nonce.Length!=Nonce.Length ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Nonce),Encoding.UTF8.GetBytes(Nonce)))
            throw new UnauthorizedAccessException("Side-effect request lacks Host capability");
        await operationLock.WaitAsync(token);
        try {
            if(HandoffRequested) throw new InvalidOperationException("Frozen Pi session cannot continue");
            if(request.Operation is "command_start" or "command_status" or "command_cancel"){
                if(!canHandoff || journal?.Commands is null)throw new UnauthorizedAccessException("This Actor has no native task command grant");
                var result=request.Operation=="command_start"?await journal.Commands.StartAsync(request.Command??throw new InvalidDataException("Command required"),token):
                    await journal.Commands.StatusAsync(request.OperationId??throw new InvalidDataException("Operation ID required"),request.Operation=="command_cancel",token);
                return new(true,"host_command_observed",CommandState:result);
            }
            if(request.Operation=="managed_relay_permit"){
                if(journal is null||request.EvidenceHash is null||request.EvidenceHash!=admittedPayloadHash)throw new UnauthorizedAccessException("Relay needs this Host's accepted provider preflight");
                return new(true,"host_managed_relay",Text:ManagedRelayPermit.Issue(request.EvidenceHash,journal.TaskId));
            }
            if(request.Operation=="preflight") {
                diagnosticStage="budget";
                var budget=hostMeasured?EvaluateHostMeasuredBudget(request.PayloadTokens,request.ContextLimit):
                    EvaluateBudget(request.PayloadTokens,request.ContextLimit);
                if(request.PayloadJson is null || Encoding.UTF8.GetByteCount(request.PayloadJson)>2*1024*1024)
                    throw new InvalidDataException("Provider payload evidence is unavailable");
                diagnosticStage="payload_parse";
                JsonDocument payloadDocument;
                try {payloadDocument=JsonDocument.Parse(request.PayloadJson);}
                catch(JsonException e) {
                    throw new InvalidDataException("ProviderPayloadParse_"+request.PayloadJson.Length+"_"+e.LineNumber+"_"+e.BytePositionInLine);
                }
                using var payload=payloadDocument;
                diagnosticStage="context_record";
                if(journal is not null) {
                    if(hostMeasured)journal.Record("context_measurement",new {context_limit=request.ContextLimit,estimated_prompt_tokens=request.PayloadTokens,measurement_scope="qualified_total_only",component_metrics_available=false},"runtime_observed");
                    else journal.RecordContext(request.SnapshotJson??throw new InvalidDataException("Context snapshot missing"),request.PayloadTokens);
                }
                journal?.Record("provider_preflight_received",new {request.PayloadTokens,fields=payload.RootElement.EnumerateObject().Select(p=>p.Name).ToArray()},"runtime_observed");
                diagnosticStage="output_reserve";
                var hasLegacy=payload.RootElement.TryGetProperty("max_tokens",out var maximum);
                var hasCurrent=payload.RootElement.TryGetProperty("max_completion_tokens",out var currentMaximum);
                if(hasCurrent)maximum=currentMaximum;
                if(hasLegacy==hasCurrent ||
                    !maximum.TryGetInt32(out var outputTokens) || outputTokens is <1 or >2048)
                    throw new InvalidDataException("Provider output reserve is not verified");
                diagnosticStage="prompt_record";
                journal?.RecordProviderPrompt(request.PayloadJson,request.PayloadTokens);
                journal?.Record("context_budget",new { request.PayloadTokens,request.ContextLimit,budget.Headroom,budget.Handoff },"runtime_observed");
                if(budget.Handoff && canHandoff) {
                    var frozen=await GitEvidence.CaptureAsync(root,writeScope,token);
                    if(frozen.Head!=expectedHead || frozen.UnexpectedFiles.Length!=0)throw new InvalidOperationException("Handoff repository changed outside task scope");
                    journal?.CheckpointRuntime(frozen,"handoff_pending");
                    HandoffRequested=true; return new(false,"context_handoff_required",Handoff:true,Headroom:budget.Headroom);
                }
                if(!budget.Handoff)admittedPayloadHash=CanonicalStore.Hash(Encoding.UTF8.GetBytes(request.PayloadJson));
                return budget.Handoff ? new(false,"fresh_context_exhausted",Headroom:budget.Headroom):
                    new(true,"context_budget_accepted",Headroom:budget.Headroom);
            }
            if(request.Operation=="memory_search") {
                Slack.RestrictedExecution.DenyAgentTools();
                if(journal is null || request.Query is null)throw new InvalidDataException("Task memory unavailable");
                return new(true,"canonical_task_memory",Text:JsonSerializer.Serialize(journal.SearchMemory(request.Query),ClientConfig.JsonOptions));
            }
            if(request.Operation=="recall") {
                if(journal is null || request.EvidenceHash is null)throw new InvalidDataException("Task evidence is unavailable");
                return new(true,"hash_verified_task_evidence",request.EvidenceHash,Text:journal.Recall(request.EvidenceHash,request.Offset));
            }
            if(request.Operation=="tool_observation"){
                if(journal is null || request.Content is null || request.ToolName is null || request.ToolCallId is null)
                    throw new InvalidDataException("Task tool observation is unavailable");
                return new(true,"task_tool_observation_saved",journal.RecordToolObservation(request.ToolName,request.ToolCallId,request.Content));
            }
            var fused=request.Operation=="edit_and_validate";
            if(request.Operation is not ("write" or "edit" or "edit_and_validate")) throw new InvalidDataException("Operation is not enabled");
            if(!fused && journal?.Commands?.AllowsFusion==true)
                throw new UnauthorizedAccessException("This task authorizes the combined operation only");
            journal?.RequireMutationApproval();
            var path=ResolveMutationPath(request.Path);
            TaskCommandGate? commands=null;TaskCommandRequest? validation=null;string? fusionPath=null;string? fusionFingerprint=null;
            if(fused){
                if(!canHandoff || journal?.Commands is not {AllowsFusion:true} owned || writeScope.Length!=1 ||
                    request.Command is not null || request.OperationId is not null)
                    throw new UnauthorizedAccessException("No exact single-file edit and validation grant");
                commands=owned;
                fusionPath=System.IO.Path.Combine(owned.Root,"fused-edit.json");
                CanonicalStore.GuardPath(fusionPath);
                fusionFingerprint=CanonicalStore.Hash(JsonSerializer.SerializeToUtf8Bytes(new{request.Path,request.OldText,request.NewText}));
                if(File.Exists(fusionPath)){
                    var saved=JsonSerializer.Deserialize<FusionState>(File.ReadAllBytes(fusionPath),ClientConfig.JsonOptions)
                        ??throw new InvalidDataException("Missing fused edit state");
                    if(saved.Fingerprint!=fusionFingerprint)throw new UnauthorizedAccessException("Fused edit intent is immutable; inspect the existing outcome");
                    return await ObserveFusedEditAsync(owned,saved,token);
                }
                validation=owned.AdmitFusion();
            }
            var before=await GitEvidence.CaptureAsync(root,writeScope,token);
            if(before.Head!=expectedHead || before.UnexpectedFiles.Length!=0)
                throw new InvalidOperationException("Repository changed outside the assigned task state");
            string content;
            if(request.Operation=="write") content=request.Content ?? throw new InvalidDataException("Write content is required");
            else {
                if(request.OldText is null || request.NewText is null || request.OldText.Length==0 || !File.Exists(path))
                    throw new InvalidDataException("Edit requires existing file and nonempty exact old text");
                if(fused && request.OldText==request.NewText)throw new InvalidDataException("Combined edit cannot be a no-op");
                var existing=File.ReadAllText(path);
                var first=existing.IndexOf(request.OldText,StringComparison.Ordinal);
                if(first<0 || existing.IndexOf(request.OldText,first+request.OldText.Length,StringComparison.Ordinal)>=0)
                    throw new InvalidDataException("Edit old text must have exactly one occurrence");
                content=existing[..first]+request.NewText+existing[(first+request.OldText.Length)..];
            }
            var bytes=Encoding.UTF8.GetBytes(content);
            if(bytes.Length>1024*1024) throw new InvalidDataException("Mutation exceeds Host limit");
            if(fusionPath is not null){
                TaskCommandGate.WriteNew(fusionPath,new FusionState(fusionFingerprint!,"edit_intent"));
                journal!.Record("fused_edit_intent",new{request.Path,operation_id=validation!.OperationId,request_hash=fusionFingerprint,automatic_retry=false},"host_verified");
            }
            journal?.Record("side_effect_proposed",new { request.Operation,path=request.Path,content_hash=CanonicalStore.Hash(bytes) },"candidate");
            journal?.MutationIntent(before,writeScope.Single(p=>string.Equals(p.Replace('\\','/'),request.Path?.Replace('\\','/'),StringComparison.OrdinalIgnoreCase)),bytes,writeScope);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            CanonicalStore.GuardPath(path);
            var staging=System.IO.Path.Combine(root,".localbrain","host-staging");
            CanonicalStore.GuardPath(staging);Directory.CreateDirectory(staging);CanonicalStore.GuardPath(staging);
            var temporary=System.IO.Path.Combine(staging,Guid.NewGuid().ToString("N")+".tmp");
            await GitEvidence.RequireIgnoredAsync(root,System.IO.Path.GetRelativePath(root,temporary),token);
            try {
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,
                    4096,FileOptions.WriteThrough)){stream.Write(bytes);stream.Flush(true);}
                fault?.Invoke("mutation_staging_flushed");
                CanonicalStore.GuardPath(path);
                File.Move(temporary,path,overwrite:true);
                fault?.Invoke("mutation_applied_before_checkpoint");
            } finally {if(File.Exists(temporary))File.Delete(temporary);}
            var hash=CanonicalStore.Hash(File.ReadAllBytes(path));
            journal?.Record("side_effect_applied",new { request.Operation,path=request.Path,content_hash=hash },"host_verified");
            var after=journal is null?null:await GitEvidence.CaptureAsync(root,writeScope,token);
            if(after is not null)journal!.CheckpointRuntime(after);
            if(fusionPath is not null){
                var applied=new FusionState(fusionFingerprint!,"edit_applied",hash);
                TaskCommandGate.WriteReplace(fusionPath,applied);
                try {
                    if(after is null || after.Head!=expectedHead || after.UnexpectedFiles.Length!=0)
                        throw new InvalidOperationException("Repository scope changed before fused validation");
                    var start=JsonSerializer.SerializeToElement(await commands!.StartAsync(validation!,token,fused:true),ClientConfig.JsonOptions);
                    if(start.GetProperty("state").GetString() is "busy" or "outcome_unknown")
                        return new(true,"edit_applied_validation_unsettled",hash,CommandState:start);
                    return await ObserveFusedEditAsync(commands,applied,token);
                }
                catch(Exception error) when(error is IOException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException) {
                    journal!.Record("fused_validation_unsettled",new{operation_id=validation!.OperationId,edit_hash=hash,error=error.GetType().Name,automatic_retry=false},"host_observed");
                    return new(true,"edit_applied_validation_unsettled",hash);
                }
            }
            return new(true,"Host applied scoped "+request.Operation,hash);
        }
        finally {operationLock.Release();}
    }

    private static async Task<Reply> ObserveFusedEditAsync(TaskCommandGate commands,FusionState saved,CancellationToken token)
    {
        if(saved.Phase!="edit_applied" || saved.EditHash is null)return new(true,"edit_outcome_unknown_do_not_replay");
        var id=commands.FusedOperationId;
        if(!Directory.Exists(System.IO.Path.Combine(commands.Root,id)))
            return new(true,"edit_applied_validation_not_started_do_not_replay",saved.EditHash);
        return new(true,"edit_applied_validation_observed",saved.EditHash,
            CommandState:await commands.ObserveFusionAsync(id,token));
    }
    internal string ResolveMutationPath(string? relative)
    {
        if(string.IsNullOrWhiteSpace(relative) || relative.Length>260 ||
            System.IO.Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0'))
            throw new UnauthorizedAccessException("Mutation path is not a relative task file");
        var parts=relative.Split('/','\\');
        if(parts.Any(p=>string.IsNullOrEmpty(p) || p is "." or ".." || p.TrimEnd(' ','.')!=p ||
            p.Equals(".git",StringComparison.OrdinalIgnoreCase) || p.Equals(".localbrain",StringComparison.OrdinalIgnoreCase) ||
            p.Equals("node_modules",StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(p,@"^(?:\.env(?:\..*)?|id_(?:rsa|ed25519)|.*\.(?:pfx|p12|key|pem))$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)))
            throw new UnauthorizedAccessException("Mutation path contains a protected or ambiguous segment");
        var normalized=string.Join('/',parts);
        if(!TaskWriteScope.Contains(writeScope,normalized))
            throw new UnauthorizedAccessException("Mutation file is outside write_scope");
        var full=System.IO.Path.GetFullPath(System.IO.Path.Combine(root,relative));
        if(!CanonicalStore.Within(root,full))throw new UnauthorizedAccessException("Mutation escaped repository identity");
        CanonicalStore.GuardPath(full);
        return full;
    }

    private async Task ServeAsync()
    {
        while(!stop.IsCancellationRequested) {
            try {
                await using var pipe=new NamedPipeServerStream(PipeName,PipeDirection.InOut,1,
                    PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                Reply response;
                try {
                    var line=await ReadBoundedLineAsync(pipe,stop.Token);
                    var request=JsonSerializer.Deserialize<Request>(line,ClientConfig.JsonOptions)
                        ?? throw new InvalidDataException("Missing Host request");
                    response=await HandleAsync(request,stop.Token);
                } catch(Exception e) when(e is not OperationCanceledException) {
                    response=new(false,e is InvalidDataException && e.Message.StartsWith("ProviderPayloadParse_",StringComparison.Ordinal)
                        ? e.Message : diagnosticStage+"_"+e.GetType().Name);
                }
                var bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response,ClientConfig.JsonOptions).Replace("\r","").Replace("\n","")+"\n");
                await pipe.WriteAsync(bytes,stop.Token);
                await pipe.FlushAsync(stop.Token);
            } catch(OperationCanceledException) when(stop.IsCancellationRequested) {break;}
              catch(IOException) when(!stop.IsCancellationRequested) { /* Disconnected relay; accept the next connection. */ }
        }
    }
    private static async Task<string> ReadBoundedLineAsync(Stream stream,CancellationToken token)
    {
        using var memory=new MemoryStream();var buffer=new byte[4096];
        while(true) {
            var n=await stream.ReadAsync(buffer,token);if(n==0)throw new IOException("Host request ended early");
            var end=Array.IndexOf(buffer,(byte)'\n',0,n);
            memory.Write(buffer,0,end<0?n:end);
            if(memory.Length>3*1024*1024)throw new InvalidDataException("Host request exceeds limit");
            if(end>=0)return new UTF8Encoding(false,true).GetString(memory.ToArray());
        }
    }
    public static async Task RunRelayAsync()
    {
        var encoded=await Console.In.ReadToEndAsync();
        if(encoded.Length>4*1024*1024)throw new InvalidDataException("Host request exceeds relay limit");
        var json=new UTF8Encoding(false,true).GetString(Convert.FromBase64String(encoded));
        var request=JsonSerializer.Deserialize<Request>(json,ClientConfig.JsonOptions)??throw new InvalidDataException("Invalid Host request");
        if(!Regex.IsMatch(request.Pipe,@"^localbrain-sidefx-[a-f0-9]{32}$",RegexOptions.CultureInvariant))
            throw new UnauthorizedAccessException("Unexpected Host pipe");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var pipe=new NamedPipeClientStream(".",request.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)+"\n"),timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        Console.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(await ReadBoundedLineAsync(pipe,timeout.Token))));
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();await server;stop.Dispose();operationLock.Dispose();
    }
}
