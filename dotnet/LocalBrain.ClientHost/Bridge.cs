using System.Net;
using System.Text.Json;

namespace LocalBrain.ClientHost;

internal sealed class Bridge(ClientConfig config, GatewayClient gateway, Audit audit,ContinueContextMemory? memoryOverride=null,WorkLoopSupervisor? supervisorOverride=null)
{
    private readonly HttpListener listener = new();
    private ContinueContextMemory? memory;
    private WorkLoopSupervisor? supervisor;
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var contextMemory=memoryOverride??ContinueContextMemory.Load();memory=contextMemory;
        await using var workSupervisor=supervisorOverride??new WorkLoopSupervisor();supervisor=workSupervisor;
        listener.Prefixes.Add($"http://{IPAddress.Loopback}:{config.BridgePort}/"); listener.Start();
        audit.Write("bridge_started", new { address = $"{IPAddress.Loopback}:{config.BridgePort}" });
        var heartbeat = HeartbeatAsync(cancellationToken);
        var ocr=Task.Run(()=>LocalBrain.ClientHost.Ocr.OcrScheduler.RunConfiguredAsync(cancellationToken),cancellationToken);
        var chatOcr=Task.Run(()=>Chat.ChatOcrRelay.RunConfiguredAsync(gateway,cancellationToken),cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
                _ = Task.Run(() => ForwardAsync(context, cancellationToken), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); await TryShutdownAsync(); await heartbeat.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);await ocr.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);await chatOcr.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
    }

    private async Task ForwardAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if(context.Request.HttpMethod=="GET" && context.Request.Url?.AbsolutePath=="/localbrain/supervision/status"){
            context.Response.ContentType="application/json";
            try{await JsonSerializer.SerializeAsync(context.Response.OutputStream,supervisor!.Status,cancellationToken:cancellationToken);}
            finally{context.Response.Close();}return;
        }
        supervisor!.RequestStarted();
        using var ocrPriority=LocalBrain.ClientHost.Ocr.OcrPriority.Enter("bridge");
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(supervisor.RequestLimit);cancellationToken=lifetime.Token;
        var requestId = context.Request.Headers["X-LocalBrain-Request-Id"];
        if (!Guid.TryParse(requestId, out _)) requestId = Guid.NewGuid().ToString();
        var forwardingStarted=false;
        var preparingContext=false;
        var upstreamResponded=false;
        var responseStarted=false;
        var aborted=false;
        try
        {
            Stream? preparedBody=null;long? preparedLength=null;
            if(memory is not null && context.Request.HttpMethod=="POST" && context.Request.Url?.AbsolutePath=="/v1/chat/completions"){
                preparingContext=true;
                using var source=new MemoryStream();var chunk=new byte[8192];int count;
                while((count=await ReadWithDeadlineAsync(context.Request.InputStream,chunk,cancellationToken))>0){
                    if(source.Length+count>ContinueContextMemory.MaxBytes)throw new InvalidDataException("Chat payload exceeds the memory bound");
                    source.Write(chunk,0,count);
                }
                var original=source.ToArray();
                var managed=AgentV2.ManagedRelayPermit.Consume(context.Request.Headers["X-LocalBrain-Managed-Permit"],original);
                if(!managed)await supervisor.CheckAsync(original,requestId!,audit,cancellationToken);
                var prepared=managed?null:await memory.PrepareAsync(original,cancellationToken);
                if(managed){preparedBody=new MemoryStream(original,false);preparedLength=original.LongLength;
                    audit.Write("agent_v2_managed_context_admitted",new{requestId,owner="Agent v2 canonical Laya/TaskMemory/preflight; bridge keeps network deadlines"});
                }else{
                preparedBody=new MemoryStream(prepared!.Body,false);preparedLength=prepared.Body.LongLength;
                audit.Write("continue_context_admitted",new{requestId,prompt_tokens=prepared.ForwardedTokens,
                    original_prompt_tokens=prepared.OriginalTokens,source_sha256=prepared.SourceHash,selection_cache_hit=prepared.SelectionCacheHit,physical_actor_context=32768});
                context.Response.Headers["X-LocalBrain-Actor-Prompt-Tokens"]=prepared.ForwardedTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if(prepared.SourceHash is not null)context.Response.Headers["X-LocalBrain-Context-Recall"]=prepared.SourceHash;
                }
                preparingContext=false;
            }
            using var ownedBody=preparedBody;
            forwardingStarted=true;
            using var headersDeadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);headersDeadline.CancelAfter(supervisor.IdleLimit);
            using var response = await gateway.SendAsync(new HttpMethod(context.Request.HttpMethod), context.Request.RawUrl ?? "/",
                preparedBody ?? (context.Request.HasEntityBody ? context.Request.InputStream : null), context.Request.ContentType,
                preparedLength ?? (context.Request.HasEntityBody ? context.Request.ContentLength64 : null), requestId!, headersDeadline.Token);
            headersDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
            upstreamResponded=true;
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            context.Response.Headers["X-LocalBrain-Request-Id"] = requestId;
            context.Response.Headers["X-LocalBrain-Response-Contract"] = "incomplete-body-is-not-completion; no-tool-replay";
            using var responseIdle=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);responseIdle.CancelAfter(supervisor.IdleLimit);
            // Copy directly: ReadAsStreamAsync may buffer custom content before returning
            // and conceal a partially generated response. This wrapper observes actual
            // writes and never synthesizes completion after a broken SSE response.
            using var output=new SupervisedOutput(context.Response.OutputStream,responseIdle,supervisor.IdleLimit,()=>responseStarted=true);
            await response.Content.CopyToAsync(output,responseIdle.Token).WaitAsync(responseIdle.Token);
        }
        catch(WorkLoopBlockedException error){
            context.Response.StatusCode=422;context.Response.ContentType="application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream,new{error=new{code="work_loop_paused",reason=error.Message,
                message="Repeated work or a Supervisor replan was detected. Inspect the existing results and change the plan; this request was not forwarded. Do not replay prior tools or automatically resend it.",
                request_id=requestId,automatic_retry=false,forwarded=false}},cancellationToken:CancellationToken.None);
        }
        catch (UnauthorizedAccessException) when(!preparingContext)
        {
            audit.Write("bridge_host_owned_route_rejected",new{requestId});
            context.Response.StatusCode=403;context.Response.ContentType="application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream,new{error="This operation requires Windows Host approval dispatch",request_id=requestId},cancellationToken:cancellationToken);
        }
        catch(Exception error) when(!forwardingStarted && preparingContext && error is (InvalidDataException or InvalidOperationException or IOException or JsonException or UnauthorizedAccessException or TimeoutException or HttpRequestException or OperationCanceledException or ArgumentException or KeyNotFoundException)){
            audit.Write("continue_context_handoff_failed",new{requestId,error=error.GetType().Name,action="preserve_source_no_forward_no_tool_replay"});
            // 422 is deliberately not a transient5xx/409: SDKs must not repeat a
            // long CPU retrieval automatically after an admission failure.
            context.Response.StatusCode=422;context.Response.ContentType="application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream,new{error=new{code="context_handoff_unavailable",
                message="Context admission or ClientPC history retrieval could not finish. This request was not forwarded or replayed; existing history archives were not replaced. Check the Host audit before retrying.",automatic_retry=false,request_id=requestId}},cancellationToken:cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException)
        {
            audit.Write("bridge_request_failed", new { requestId, error = error.GetType().Name,
                dispatch_state=upstreamResponded?"response_received":forwardingStarted?"unknown_or_dispatched":"not_started",supervision_timeout=error is OperationCanceledException,automatic_retry=false });
            // Never rewrite an already-started SSE stream as a fresh retryable failure.
            if(responseStarted){aborted=true;context.Response.Abort();}
            else {
                context.Response.StatusCode = 422; context.Response.ContentType = "application/json";
                using var errorDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new { error = new {code="dispatch_outcome_unknown",
                    message="The model request may have been dispatched. Do not replay tools or automatically resend; inspect the existing request first.",
                    request_id=requestId,supervision_timeout=error is OperationCanceledException,automatic_retry=false}},cancellationToken:errorDeadline.Token);
            }
        }
        finally { supervisor.RequestFinished();audit.Write("bridge_request_settled",new{requestId,forwardingStarted,upstreamResponded,aborted,deadline_elapsed=lifetime.IsCancellationRequested});if(!aborted)context.Response.Close(); }
    }

    private async Task<int> ReadWithDeadlineAsync(Stream input,byte[] buffer,CancellationToken token)
    {
        using var idle=CancellationTokenSource.CreateLinkedTokenSource(token);idle.CancelAfter(supervisor!.IdleLimit);
        return await input.ReadAsync(buffer,idle.Token);
    }

    private sealed class SupervisedOutput(Stream inner,CancellationTokenSource idle,TimeSpan limit,Action started):Stream
    {
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken token=default){
            if(buffer.IsEmpty)return;
            idle.Token.ThrowIfCancellationRequested();started();
            await inner.WriteAsync(buffer,idle.Token);await inner.FlushAsync(idle.Token);idle.CancelAfter(limit);
        }
        public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken token)=>WriteAsync(buffer.AsMemory(offset,count),token).AsTask();
        public override void Write(byte[] buffer,int offset,int count)=>WriteAsync(buffer.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
        public override void Flush()=>inner.Flush();public override Task FlushAsync(CancellationToken token)=>inner.FlushAsync(idle.Token);
        public override int Read(byte[] b,int o,int n)=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();
    }

    private async Task HeartbeatAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.HeartbeatSeconds));
        while (await timer.WaitForNextTickAsync(token))
            try { using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(20));await gateway.JsonAsync(HttpMethod.Post, "/v1/control/heartbeat", "{}", deadline.Token); }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            { audit.Write("heartbeat_failed", new { error = error.GetType().Name }); }
    }
    private async Task TryShutdownAsync()
    {
        try { using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));await gateway.JsonAsync(HttpMethod.Post, "/v1/control/client-shutdown", "{}",deadline.Token); } catch { }
    }
}
