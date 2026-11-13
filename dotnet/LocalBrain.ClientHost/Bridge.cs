using System.Net;
using System.Text.Json;
using System.Diagnostics;

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
        var slackOcr=Task.Run(()=>Chat.SlackOcrWorker.RunConfiguredAsync(config,cancellationToken),cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
                _ = Task.Run(() => ForwardAsync(context, cancellationToken), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); await TryShutdownAsync(); await heartbeat.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);await ocr.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);await chatOcr.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);await slackOcr.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
    }

    private async Task ForwardAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if(context.Request.HttpMethod=="GET" && context.Request.Url?.AbsolutePath=="/localbrain/supervision/status"){
            context.Response.ContentType="application/json";
            try{await JsonSerializer.SerializeAsync(context.Response.OutputStream,supervisor!.Status,cancellationToken:cancellationToken);}
            finally{context.Response.Close();}return;
        }
        supervisor!.RequestStarted();
        var hostShutdown=cancellationToken;
        using var ocrPriority=LocalBrain.ClientHost.Ocr.OcrPriority.Enter("bridge");
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(supervisor.RequestLimit);cancellationToken=lifetime.Token;
        var requestId = context.Request.Headers["X-LocalBrain-Request-Id"];
        if (!Guid.TryParse(requestId, out _)) requestId = Guid.NewGuid().ToString();
        context.Response.Headers["X-LocalBrain-Request-Id"]=requestId;
        var elapsed=Stopwatch.StartNew();var phase="request_body";string? failureReason=null;
        var trace=new ContextAdmissionTrace((name,fields)=>audit.Write(name,new{requestId,metadata=fields}));
        audit.Write("bridge_request_received",new{requestId,received_utc=DateTimeOffset.UtcNow,method=context.Request.HttpMethod,path=context.Request.Url?.AbsolutePath});
        var forwardingStarted=false;
        var preparingContext=false;
        var upstreamResponded=false;
        var responseStarted=false;
        var aborted=false;
        try
        {
            Stream? preparedBody=null;long? preparedLength=null;
            var managedPermit=context.Request.Headers["X-LocalBrain-Managed-Permit"];
            if((memory is not null || managedPermit is not null) && context.Request.HttpMethod=="POST" && context.Request.Url?.AbsolutePath=="/v1/chat/completions"){
                preparingContext=true;
                using var source=new MemoryStream();var chunk=new byte[8192];int count;
                while((count=await ReadWithDeadlineAsync(context.Request.InputStream,chunk,cancellationToken))>0){
                    if(source.Length+count>ContinueContextMemory.MaxBytes)throw new InvalidDataException("Chat payload exceeds the memory bound");
                    source.Write(chunk,0,count);
                }
                var original=source.ToArray();
                var managed=AgentV2.ManagedRelayPermit.Consume(managedPermit,original);
                if(!managed)await supervisor.CheckAsync(original,requestId!,audit,cancellationToken);
                phase="context_admission";
                var prepared=managed?null:await (memory ?? throw new UnauthorizedAccessException("Managed relay capability required")).PrepareAsync(original,cancellationToken,trace);
                if(managed){preparedBody=new MemoryStream(original,false);preparedLength=original.LongLength;
                    audit.Write("host_managed_context_admitted",new{requestId,owner="Host task preflight and one-use body-bound permit; bridge keeps network deadlines"});
                }else{
                preparedBody=new MemoryStream(prepared!.Body,false);preparedLength=prepared.Body.LongLength;
                audit.Write("continue_context_admitted",new{requestId,prompt_tokens=prepared.ForwardedTokens,
                    original_prompt_tokens=prepared.OriginalTokens,source_sha256=prepared.SourceHash,selection_cache_hit=prepared.SelectionCacheHit,
                    fallback_used=prepared.FallbackUsed,selection_failure=prepared.SelectionFailure,physical_actor_context=32768});
                context.Response.Headers["X-LocalBrain-Actor-Prompt-Tokens"]=prepared.ForwardedTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if(prepared.SourceHash is not null)context.Response.Headers["X-LocalBrain-Context-Recall"]=prepared.SourceHash;
                }
                preparingContext=false;
            }
            using var ownedBody=preparedBody;
            forwardingStarted=true;
            phase="upstream_headers";audit.Write("bridge_forward_started",new{requestId,elapsed_ms=elapsed.Elapsed.TotalMilliseconds});
            using var headersDeadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);headersDeadline.CancelAfter(supervisor.IdleLimit);
            using var response = await gateway.SendAsync(new HttpMethod(context.Request.HttpMethod), context.Request.RawUrl ?? "/",
                preparedBody ?? (context.Request.HasEntityBody ? context.Request.InputStream : null), context.Request.ContentType,
                preparedLength ?? (context.Request.HasEntityBody ? context.Request.ContentLength64 : null), requestId!, headersDeadline.Token);
            headersDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
            upstreamResponded=true;
            phase="upstream_stream";audit.Write("bridge_upstream_response",new{requestId,status=(int)response.StatusCode,elapsed_ms=elapsed.Elapsed.TotalMilliseconds});
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
            phase="completed";
        }
        catch(WorkLoopBlockedException error){
            failureReason="work_loop_paused";
            context.Response.StatusCode=422;context.Response.ContentType="application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream,new{error=new{code="work_loop_paused",reason=error.Message,
                message="Repeated work or a Supervisor replan was detected. Inspect the existing results and change the plan; this request was not forwarded. Do not replay prior tools or automatically resend it.",
                request_id=requestId,automatic_retry=false,forwarded=false}},cancellationToken:CancellationToken.None);
        }
        catch (UnauthorizedAccessException) when(!preparingContext)
        {
            failureReason="host_owned_route_required";
            audit.Write("bridge_host_owned_route_rejected",new{requestId});
            context.Response.StatusCode=403;context.Response.ContentType="application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream,new{error="This operation requires Windows Host approval dispatch",request_id=requestId},cancellationToken:cancellationToken);
        }
        catch(Exception error) when(!forwardingStarted && preparingContext && error is (InvalidDataException or InvalidOperationException or IOException or JsonException or UnauthorizedAccessException or TimeoutException or HttpRequestException or OperationCanceledException or ArgumentException or KeyNotFoundException)){
            failureReason=hostShutdown.IsCancellationRequested?"host_shutdown":lifetime.IsCancellationRequested?"host_request_timeout":trace.Reason(error);
            audit.Write("continue_context_handoff_failed",new{requestId,error=error.GetType().Name,reason=failureReason,metadata=trace.Snapshot(),action="preserve_source_no_forward_no_tool_replay"});
            // 424 reports a failed local dependency without OpenAI SDK's automatic 5xx/408/409 retry.
            context.Response.StatusCode=failureReason=="invalid_context_request"?400:
                failureReason is "context_budget_exceeded" or "history_reconstruction_failed"?422:424;
            context.Response.ContentType="application/json";
            using var errorDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await JsonSerializer.SerializeAsync(context.Response.OutputStream,new{error=new{code="context_handoff_unavailable",reason=failureReason,
                message="Context admission could not finish. This request was not forwarded or replayed; existing history archives were not replaced. Check the Host audit before retrying.",automatic_retry=false,forwarded=false,request_id=requestId}},cancellationToken:errorDeadline.Token);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or HttpListenerException)
        {
            failureReason=hostShutdown.IsCancellationRequested?"host_shutdown":lifetime.IsCancellationRequested?"host_request_timeout":
                error is HttpListenerException?"client_request_aborted":error is OperationCanceledException?
                phase=="upstream_headers"?"upstream_timeout":phase=="upstream_stream"?"upstream_stream_timeout":"client_body_timeout":
                phase=="upstream_stream"?"upstream_stream_failed":"upstream_transport_failed";
            audit.Write("bridge_request_failed", new { requestId, error = error.GetType().Name,
                reason=failureReason,phase,elapsed_ms=elapsed.Elapsed.TotalMilliseconds,
                dispatch_state=upstreamResponded?"response_received":forwardingStarted?"unknown_or_dispatched":"not_started",supervision_timeout=error is OperationCanceledException,automatic_retry=false });
            // Never rewrite an already-started SSE stream as a fresh retryable failure.
            if(responseStarted || error is HttpListenerException){aborted=true;context.Response.Abort();}
            else {
                context.Response.StatusCode = 422; context.Response.ContentType = "application/json";
                using var errorDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new { error = new {code="dispatch_outcome_unknown",
                    message="The model request may have been dispatched. Do not replay tools or automatically resend; inspect the existing request first.",
                    request_id=requestId,reason=failureReason,supervision_timeout=error is OperationCanceledException,automatic_retry=false}},cancellationToken:errorDeadline.Token);
            }
        }
        finally { var deadlineElapsed=lifetime.IsCancellationRequested;lifetime.Cancel();supervisor.RequestFinished();audit.Write("bridge_request_settled",new{requestId,forwardingStarted,upstreamResponded,aborted,phase,reason=failureReason,elapsed_ms=elapsed.Elapsed.TotalMilliseconds,deadline_elapsed=deadlineElapsed});if(!aborted)context.Response.Close(); }
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
