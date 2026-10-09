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
        string? EvidenceHash=null,int Offset=0,string? SnapshotJson=null);
    internal sealed record Reply(bool Ok, string Status, string? ContentHash = null, bool Handoff = false,
        int Headroom = 0,string? Text=null);
    internal sealed record Budget(bool Handoff, int Headroom);
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

    public SideEffectGate(string root, string[] writeScope, string expectedHead, bool canHandoff,
        HostTaskJournal? journal = null,Action<string>? fault=null)
    {
        this.root=System.IO.Path.GetFullPath(root); this.writeScope=writeScope;
        this.expectedHead=expectedHead; this.canHandoff=canHandoff; this.journal=journal;
        this.fault=fault;
        CanonicalStore.GuardPath(this.root);
        server=ServeAsync();
    }

    internal async Task<Reply> HandleAsync(Request request, CancellationToken token = default)
    {
        if(request.Pipe!=PipeName || request.Nonce is null || request.Nonce.Length!=Nonce.Length ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Nonce),Encoding.UTF8.GetBytes(Nonce)))
            throw new UnauthorizedAccessException("Side-effect request lacks Host capability");
        await operationLock.WaitAsync(token);
        try {
            if(HandoffRequested) throw new InvalidOperationException("Frozen Pi session cannot continue");
            if(request.Operation=="preflight") {
                diagnosticStage="budget";
                var budget=EvaluateBudget(request.PayloadTokens,request.ContextLimit);
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
                if(journal is not null)journal.RecordContext(request.SnapshotJson??throw new InvalidDataException("Context snapshot missing"),request.PayloadTokens);
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
                return budget.Handoff ? new(false,"fresh_context_exhausted",Headroom:budget.Headroom):
                    new(true,"context_budget_accepted",Headroom:budget.Headroom);
            }
            if(request.Operation=="recall") {
                if(journal is null || request.EvidenceHash is null)throw new InvalidDataException("Task evidence is unavailable");
                return new(true,"hash_verified_task_evidence",request.EvidenceHash,Text:journal.Recall(request.EvidenceHash,request.Offset));
            }
            if(request.Operation is not ("write" or "edit")) throw new InvalidDataException("Operation is not enabled");
            journal?.RequireMutationApproval();
            var path=ResolveMutationPath(request.Path);
            var before=await GitEvidence.CaptureAsync(root,writeScope,token);
            if(before.Head!=expectedHead || before.UnexpectedFiles.Length!=0)
                throw new InvalidOperationException("Repository changed outside the assigned task state");
            string content;
            if(request.Operation=="write") content=request.Content ?? throw new InvalidDataException("Write content is required");
            else {
                if(request.OldText is null || request.NewText is null || request.OldText.Length==0 || !File.Exists(path))
                    throw new InvalidDataException("Edit requires existing file and nonempty exact old text");
                var existing=File.ReadAllText(path);
                var first=existing.IndexOf(request.OldText,StringComparison.Ordinal);
                if(first<0 || existing.IndexOf(request.OldText,first+request.OldText.Length,StringComparison.Ordinal)>=0)
                    throw new InvalidDataException("Edit old text must have exactly one occurrence");
                content=existing[..first]+request.NewText+existing[(first+request.OldText.Length)..];
            }
            var bytes=Encoding.UTF8.GetBytes(content);
            if(bytes.Length>1024*1024) throw new InvalidDataException("Mutation exceeds Host limit");
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
            journal?.CheckpointRuntime(await GitEvidence.CaptureAsync(root,writeScope,token));
            return new(true,"Host applied scoped "+request.Operation,hash);
        }
        finally {operationLock.Release();}
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
        if(!writeScope.Any(p=>string.Equals(p.Replace('\\','/'),normalized,StringComparison.OrdinalIgnoreCase)))
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
