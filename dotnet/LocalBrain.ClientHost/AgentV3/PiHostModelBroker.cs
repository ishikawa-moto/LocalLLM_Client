using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LocalBrain.ClientHost.AgentV2;

namespace LocalBrain.ClientHost.AgentV3;

// Owned streams connect to the one-task WSL bridge. This process owns admission and dispatch.
internal static class PiHostModelBroker
{
    private const int MaxFrame=12*1024*1024, MaxBody=2*1024*1024, MaxResponse=32*1024*1024;
    private static readonly UTF8Encoding Utf8=new(false,true);

    internal static async Task RunAsync(Stream bridgeStdout,Stream bridgeStdin,ClientConfig config,
        HostTaskJournal journal,SideEffectGate gate,CancellationToken token)
    {
        Slack.RestrictedExecution.DenyAgentTools();
        if(!gate.OwnsJournal(journal))throw new UnauthorizedAccessException("Broker journal does not own gate");
        if(config.BridgePort is <1 or >65535)throw new InvalidDataException("Invalid trusted bridge port");
        var options=config.V3Development??throw new UnauthorizedAccessException("V3 development configuration unavailable");
        options.Validate();
        if(!options.Enabled)throw new UnauthorizedAccessException("V3 development is disabled");
        var reader=new PiJsonlReader(bridgeStdout,MaxFrame);
        await using var counter=new ContinueContextMemory.PayloadCounter(options.Distribution,options.TokenizerPython!,options.TokenizerPath!,options.TokenizerScriptPath!);
        using var http=new HttpClient(new HttpClientHandler {AllowAutoRedirect=false,UseProxy=false}) {BaseAddress=new Uri($"http://{IPAddress.Loopback}:{config.BridgePort}/"),Timeout=Timeout.InfiniteTimeSpan};
        while(true) {
            var frame=await reader.ReadAsync(token);
            if(frame is null)return;
            try {
                var result=await DispatchAsync(frame.Value,http,counter,journal,gate,token);
                await WriteFrameAsync(bridgeStdin,result,token);
            } catch(OperationCanceledException) when(token.IsCancellationRequested) {throw;}
              catch(Exception e) when(e is not OutOfMemoryException) {
                await WriteFrameAsync(bridgeStdin,JsonSerializer.Serialize(new {error="model_relay_failed",cause=e.GetType().Name}),token);
                throw; // No replay after an uncertain dispatch.
            }
        }
    }

    private static async Task<string> DispatchAsync(JsonElement root,HttpClient http,ContinueContextMemory.PayloadCounter counter,
        HostTaskJournal journal,SideEffectGate gate,CancellationToken token)
    {
        EnsureUniqueProperties(root);
        if(root.TryGetProperty("operation",out _))return await DispatchToolAsync(root,gate,token);
        if(root.ValueKind!=JsonValueKind.Object || root.EnumerateObject().Count()!=4 ||
            root.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=4 ||
            !root.TryGetProperty("method",out var method) || method.GetString()!="POST" ||
            !root.TryGetProperty("path",out var path) || path.GetString()!="/v1/chat/completions" ||
            !root.TryGetProperty("contentType",out var type) || type.GetString()!="application/json" ||
            !root.TryGetProperty("bodyBase64",out var encoded) || encoded.ValueKind!=JsonValueKind.String)
            throw new InvalidDataException("Invalid model frame");
        var body=Convert.FromBase64String(encoded.GetString()!);
        if(body.Length is 0 or >MaxBody)throw new InvalidDataException("Model payload exceeds admission bound");
        var payload=Utf8.GetString(body);
        using(var document=JsonDocument.Parse(body,new JsonDocumentOptions{MaxDepth=64}))EnsureUniqueProperties(document.RootElement);
        var request=JsonNode.Parse(payload,new JsonNodeOptions(),new JsonDocumentOptions{MaxDepth=64}) as JsonObject
            ??throw new InvalidDataException("Model payload must be an object");
        if(request["model"]?.GetValue<string>()!="local-qwen38" || request["messages"] is not JsonArray)
            throw new InvalidDataException("Unqualified model payload");
        var measured=await counter.MeasureAsync(request,token);
        var preflight=await gate.HandleHostMeasuredPreflightAsync(new(gate.PipeName,gate.Nonce,"preflight",
            PayloadTokens:measured,ContextLimit:32768,PayloadJson:payload),token);
        if(!preflight.Ok)throw new InvalidOperationException(preflight.Status);
        var hash=CanonicalStore.Hash(body);
        var permit=await gate.HandleAsync(new(gate.PipeName,gate.Nonce,"managed_relay_permit",EvidenceHash:hash),token);
        if(!permit.Ok || permit.Text is null)throw new UnauthorizedAccessException("Provider permit unavailable");
        using var message=new HttpRequestMessage(HttpMethod.Post,"/v1/chat/completions") {Content=new ByteArrayContent(body)};
        message.Content.Headers.ContentType=new MediaTypeHeaderValue("application/json");
        message.Headers.Add("X-LocalBrain-Managed-Permit",permit.Text);
        var requestId=Guid.NewGuid().ToString("D");
        message.Headers.Add("X-LocalBrain-Request-Id",requestId);
        journal.Checkpoint(new {current_gateway_request_id=requestId});
        try {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromMinutes(9));
        using var response=await http.SendAsync(message,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
        await using var input=await response.Content.ReadAsStreamAsync(deadline.Token);
        await using var buffer=new MemoryStream();var chunk=new byte[65536];int count;
        while((count=await input.ReadAsync(chunk,deadline.Token))!=0) {
            if(buffer.Length+count>MaxResponse)throw new InvalidDataException("Model response exceeds Host bound");
            await buffer.WriteAsync(chunk.AsMemory(0,count),deadline.Token);
        }
        return JsonSerializer.Serialize(new {status=(int)response.StatusCode,content_type=response.Content.Headers.ContentType?.ToString()??"application/json",body_base64=Convert.ToBase64String(buffer.ToArray())});
        } finally {
            // The owning task clears this field when cancellation makes the
            // lifecycle outcome unknown; do not race its terminal checkpoint.
            if(!token.IsCancellationRequested)journal.Checkpoint(new {current_gateway_request_id=(string?)null});
        }
    }

    private static async Task<string> DispatchToolAsync(JsonElement root,SideEffectGate gate,CancellationToken token)
    {
        if(root.ValueKind!=JsonValueKind.Object || !root.TryGetProperty("operation",out var op) || op.ValueKind!=JsonValueKind.String)
            throw new InvalidDataException("Invalid tool frame");
        SideEffectGate.Request request;
        if(op.GetString()=="memory_search" && root.EnumerateObject().Count()==2 && root.TryGetProperty("query",out var query) && query.ValueKind==JsonValueKind.String && query.GetString() is {} q && !string.IsNullOrWhiteSpace(q) && q.Length<=1024)
            request=new(gate.PipeName,gate.Nonce,"memory_search",Query:q);
        else if(op.GetString()=="recall" && root.EnumerateObject().Count()==3 && root.TryGetProperty("evidenceHash",out var evidence) && evidence.ValueKind==JsonValueKind.String && evidence.GetString() is {} h && Regex.IsMatch(h,@"\A[a-f0-9]{64}\z",RegexOptions.CultureInvariant) && root.TryGetProperty("offset",out var offset) && offset.TryGetInt32(out var n) && n>=0)
            request=new(gate.PipeName,gate.Nonce,"recall",EvidenceHash:h,Offset:n);
        else throw new InvalidDataException("Invalid tool frame");
        try {
            var reply=await gate.HandleAsync(request,token);
            var frame=JsonSerializer.Serialize(reply,new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if(Utf8.GetByteCount(frame)+1>32*1024)throw new InvalidDataException("Host tool reply exceeds byte limit");
            return frame;
        } catch(OperationCanceledException) when(token.IsCancellationRequested) {throw;}
          catch(Exception e) when(e is not OutOfMemoryException) {
            return JsonSerializer.Serialize(new {ok=false,status="host_tool_rejected"});
        }
    }

    private static void EnsureUniqueProperties(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object) {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in element.EnumerateObject()) {
                if(!names.Add(property.Name))throw new InvalidDataException("Duplicate payload property");
                EnsureUniqueProperties(property.Value);
            }
        } else if(element.ValueKind==JsonValueKind.Array)foreach(var item in element.EnumerateArray())EnsureUniqueProperties(item);
    }

    private static async Task WriteFrameAsync(Stream output,string frame,CancellationToken token)
    {
        var bytes=Utf8.GetBytes(frame+"\n");await output.WriteAsync(bytes,token);await output.FlushAsync(token);
    }
}
