using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;
using LocalBrain.ClientHost.Ocr;

namespace LocalBrain.ClientHost.Slack;

internal sealed class SlackDeferred : Exception { }
internal sealed class SlackInterrupted : Exception { }
// Remains an OperationCanceledException so existing deferral handling is unchanged; Code is a fixed identifier.
internal sealed class SlackPriorityYield(string code,CancellationToken token):OperationCanceledException("SLACK_PRIORITY_YIELD",null,token){internal string Code{get;}=code;}
internal sealed record SlackReviewedCandidate(string RequestId,string ReviewJson);
internal interface ISlackLocalInference
{
    Task<string> Chat(string requestId,string session,string context,string prompt,int outputTokens,int seconds,CancellationToken token);
    Task<SlackReviewedCandidate?> Candidate(string session,string source,CancellationToken token);
    Task<string> RegisterProposal(string id,string reviewRequestId,string session,CancellationToken token);
}

// Fixed authenticated local routes only. A message/model response cannot select a URL, executable or tool.
internal sealed class SlackLocalInference(GatewayClient gateway,SlackStore store):ISlackLocalInference
{
    private async Task<JsonElement> Invoke(string route,object body,CancellationToken token)
    {
        if(!RestrictedExecution.Active)throw new UnauthorizedAccessException("SLACK_HOST_CONTEXT_REQUIRED");
        var supplied=JsonSerializer.SerializeToElement(body,SlackOptions.Json);
        ValidateInput(route,supplied);
        var existed=store.HasFrozenInference(supplied.GetProperty("request_id").GetString()!);
        var frozen=store.FreezeInference(route,supplied.GetProperty("request_id").GetString()!,supplied.GetProperty("session_id").GetString()!,supplied.GetRawText());
        var root=supplied.GetProperty("request_id").GetString()!;
        SlackStore.ChatPlan? plan=null;
        if(route=="/v1/slack/chat"){
            plan=store.ChatProgress(root,supplied.GetProperty("session_id").GetString()!,frozen,existed);
            if(plan.State=="complete")return DecisionProtocol.Parse(plan.Result!);
            if(plan.State=="held"){
                if(!store.BeginHeldProbe(plan,DateTimeOffset.UtcNow))throw new SlackDeferred();
                using var probeDeadline=CancellationTokenSource.CreateLinkedTokenSource(token);probeDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                try{
                    var status=await gateway.SlackJsonAsync("/v1/slack/status",JsonSerializer.Serialize(new{operation="chat",request_id=plan.Request,session_id=plan.Session,request_hash=plan.Hash,restriction=SlackRestriction.Instance},SlackOptions.Json),probeDeadline.Token);
                    store.ObserveHeldChat(plan,DecisionProtocol.Parse(status));
                }catch(Exception e)when(e is HttpRequestException or IOException or OperationCanceledException){store.HeldProbeUnresolved(plan);throw new SlackDeferred();}
                plan=store.ChatProgress(root,plan.Session,frozen,true);
                if(plan.State=="complete")return DecisionProtocol.Parse(plan.Result!);
                // A resumed priority yield still goes through the existing gated
                // retry endpoint after its persisted delay; unknown never sends.
                throw new SlackDeferred();
            }
            if(plan.State=="probing"&&plan.NextCheck<=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()){
                string status;
                try{status=await gateway.SlackJsonAsync("/v1/slack/status",JsonSerializer.Serialize(new{operation="chat",request_id=plan.Request,session_id=plan.Session,request_hash=plan.Hash,restriction=SlackRestriction.Instance},SlackOptions.Json),token);}
                catch(Exception e)when(e is HttpRequestException or IOException){store.ChatProbeFailed(plan);throw new SlackDeferred();}
                store.ObserveChat(plan,DecisionProtocol.Parse(status));
                plan=store.ChatProgress(root,plan.Session,frozen,true);
                if(plan.State=="complete")return DecisionProtocol.Parse(plan.Result!);
            }
            plan=store.MarkChatSending(plan);frozen=plan.Json;
        }
        var actual=DecisionProtocol.Parse(frozen);ValidateInput(route,actual);
        string response;
        try{response=await gateway.SlackJsonAsync(plan?.Attempt>0?"/v1/slack/retry":route,plan?.Attempt>0?store.RetryBody(plan):frozen,token);}
        catch(HttpRequestException)when(plan is not null){store.ChatAwaiting(root,"transport_disconnected");throw new SlackDeferred();}
        catch(IOException)when(plan is not null){store.ChatAwaiting(root,"transport_disconnected");throw new SlackDeferred();}
        catch(OperationCanceledException){
            if(plan is not null)store.ChatAwaiting(root,"cancellation_pending");
            // Closing a Client HTTP request alone does not prove the Server model stopped.
            // Cancel only this immutable Session/request using the existing authenticated local route.
            using var cancelDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try{await gateway.SlackJsonAsync("/v1/slack/cancel",JsonSerializer.Serialize(new{request_id=actual.GetProperty("request_id").GetString(),session_id=actual.GetProperty("session_id").GetString(),restriction=SlackRestriction.Instance},SlackOptions.Json),cancelDeadline.Token);}
            catch(Exception e)when(e is HttpRequestException or OperationCanceledException or IOException or JsonException or InvalidDataException or UnauthorizedAccessException){}
            throw;
        }
        if(response.Length>128000)throw new InvalidDataException("SLACK_LOCAL_RESPONSE_SIZE");
        var json=JsonSerializer.Deserialize<JsonElement>(response);
        var state=json.GetProperty("status").GetString();if(state is "deferred" or "busy" && json.TryGetProperty("retry_safe",out var safe)&&safe.ValueKind==JsonValueKind.True){if(plan is not null)store.ChatDeferred(plan,json.TryGetProperty("reason",out var reason)&&reason.ValueKind==JsonValueKind.String?reason.GetString():null);throw new SlackDeferred();}
        if(state is "running" or "unresolved"){if(plan is not null){store.ChatAwaiting(root,"server_nonterminal");throw new SlackDeferred();}throw new SlackInterrupted();}
        if(state=="interrupted"){if(plan is not null){store.ChatAwaiting(root,"server_interrupted");throw new SlackDeferred();}throw new SlackInterrupted();}
        if(state=="complete"&&plan is not null)store.ChatCompleted(plan,response);
        if(state!="complete")throw new InvalidDataException("SLACK_LOCAL_RESULT_INVALID");return json;
    }
    private static void ValidateInput(string route,JsonElement value){
        var fits=route=="/v1/slack/chat"?SlackConversation.Fits(value.GetProperty("context").GetString()!,value.GetProperty("prompt").GetString()!):SlackConversation.Bytes(value.GetProperty("source").GetString()!)<=SlackConversation.InputBytes;
        if(!fits)throw new InvalidDataException("SLACK_INFERENCE_INPUT_BUDGET");
    }
    public async Task<string> Chat(string requestId,string session,string context,string prompt,int outputTokens,int seconds,CancellationToken token)
    {
        var result=await Invoke("/v1/slack/chat",new{request_id=requestId,session_id=session,context,prompt,max_tokens=outputTokens,timeout_seconds=seconds,restriction=SlackRestriction.Instance},token);
        var text=result.GetProperty("text").GetString()!;if(text.Length>16000)throw new InvalidDataException("SLACK_CHAT_SIZE");return text;
    }
    public async Task<SlackReviewedCandidate?> Candidate(string session,string source,CancellationToken token)
    {
        if(System.Text.Encoding.UTF8.GetByteCount(source)>16000)throw new InvalidDataException("SLACK_REVIEW_INPUT_BUDGET");
        var requestId=SlackHash.Of(session+source)[..32];
        var result=await Invoke("/v1/slack/review",new{request_id=requestId,session_id=session,source,max_tokens=1024,timeout_seconds=60,restriction=SlackRestriction.Instance},token);
        if(!result.TryGetProperty("candidate",out var candidate)||candidate.ValueKind==JsonValueKind.Null)return null;
        return new(requestId,result.GetRawText());
    }
    public async Task<string> RegisterProposal(string id,string reviewRequestId,string session,CancellationToken token)=>await gateway.SlackJsonAsync("/v1/slack/proposal",JsonSerializer.Serialize(new{proposal_id=id,review_request_id=reviewRequestId,session_id=session,restriction=SlackRestriction.Instance},SlackOptions.Json),token);
}

internal sealed class SlackPriority
{
    private readonly OcrConfig ocr;
    private readonly Func<OcrResources.Sample>? sample;
    private readonly Func<string?>? foreground;
    internal SlackPriority(OcrConfig ocr){this.ocr=ocr;}
    private SlackPriority(OcrConfig ocr,Func<OcrResources.Sample> sample,Func<string?>? foreground){this.ocr=ocr;this.sample=sample;this.foreground=foreground;}
    internal static SlackPriority ForFixture(OcrConfig ocr,Func<OcrResources.Sample> sample,Func<string?>? foreground=null)=>new(ocr,sample,foreground);
    private readonly OcrResources resources=new();
    private long quietAfter;
    internal string? ForegroundReason(){
        try{return foreground is not null?foreground():(OcrPriority.Active(ocr)?"lease":!OcrPriority.Healthy(ocr)?"probe_failed":OcrPriority.LegacyConsumerReason(ocr));}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or OcrException or System.ComponentModel.Win32Exception){return "probe_failed";}
    }
    internal bool ForegroundActive()=>ForegroundReason() is not null;
    internal bool Admission()
    {
        if(ForegroundActive()){quietAfter=Environment.TickCount64+5000;return false;}
        if(Environment.TickCount64<quietAfter)return false;
        var r=sample?.Invoke()??resources.Read();return OcrResources.Refusal(ocr,r,false) is null;
    }
    internal async Task<T> Run<T>(Func<CancellationToken,Task<T>> work,int seconds,CancellationToken token,Func<bool>? shouldYield=null)
    {
        if(!Admission())throw new SlackDeferred();
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(token);stop.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var watcherStop=new CancellationTokenSource();string? reason=null;
        var watcher=Task.Run(async()=>{try{while(!watcherStop.IsCancellationRequested){await Task.Delay(50,watcherStop.Token);string? found;
                try{found=ForegroundReason()??(shouldYield?.Invoke()==true?"caller":null);}
                catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException){found="probe_failed";}if(found is not null){Interlocked.CompareExchange(ref reason,"priority_yield_"+found,null);stop.Cancel();return;}}}catch(OperationCanceledException){}});
        try{return await work(stop.Token);}
        catch(OperationCanceledException e)when(e is not SlackPriorityYield&&!token.IsCancellationRequested&&stop.IsCancellationRequested){
            throw new SlackPriorityYield(Volatile.Read(ref reason)??"priority_outer_deadline",stop.Token);
        }
        finally{watcherStop.Cancel();await watcher;}
    }
}

internal static class SlackStagingFiles
{
    private static readonly System.Text.RegularExpressions.Regex OwnedFileName=new(
        @"^[a-f0-9]{32}-F[A-Z0-9]{2,32}-([a-f0-9]{64})\.(?:png|jpg|webp|pdf)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex LegacyFileName=new(
        @"^([a-f0-9]{32})-(F[A-Z0-9]{2,32})(\.(?:png|jpg|webp|pdf))$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static string PathFor(string directory,string job,string file,string hash,string extension)=>
        Path.Combine(directory,$"{job}-{file}-{hash}{extension}");

    internal static void DeleteIfHashMatches(string path,string expectedHash)
    {
        if(!File.Exists(path))return;
        CanonicalStore.GuardPath(path);
        if(OcrJobStore.HashFile(path)==expectedHash)File.Delete(path);
    }

    internal static void Reconcile(OcrConfig config,SlackStore? store=null)
    {
        var directory=Path.Combine(config.InputRoots[0],"slack-restricted");
        CanonicalStore.GuardPath(directory);
        if(!Directory.Exists(directory))return;
        foreach(var path in Directory.EnumerateFiles(directory))
        {
            CanonicalStore.GuardPath(path);
            var match=OwnedFileName.Match(Path.GetFileName(path));
            if(match.Success){DeleteIfHashMatches(path,match.Groups[1].Value);continue;}
            // Old releases did not put a hash in the filename. Require the retained
            // job/file binding and exact extension/hash; an unknown original stays intact.
            var legacy=LegacyFileName.Match(Path.GetFileName(path));
            if(!legacy.Success||store?.GetAttachment(legacy.Groups[1].Value,legacy.Groups[2].Value) is not {} attachment||attachment.Extension!=legacy.Groups[3].Value)continue;
            DeleteIfHashMatches(path,attachment.Hash);
        }
    }
}

internal sealed class SlackProcessor(SlackOptions options,SlackStore store,SlackApi api,ISlackLocalInference inference,OcrConfig? ocr,SlackPriority? priority,SlackRemoteOcr? remote=null)
{
    internal async Task Run(CancellationToken token)
    {
        while(!token.IsCancellationRequested){
            store.FinishUnrecoverableChats(DateTimeOffset.UtcNow);
            store.ReleaseSummaryWaiters();
            SlackResultReturnRequests.Step(options,store);
            foreach(var waiting in store.WaitingForOcr()){
                if(remote is null){
                    if(waiting.FileIds.All(f=>store.GetAttachment(waiting.Id,f) is {OcrJob: {} id} && new OcrJobStore(ocr??throw new InvalidOperationException("SLACK_LOCAL_OCR_REQUIRED")).GetState(id).Status is "complete" or "failed" or "cancelled" or "expired"))store.OcrReady(waiting.Id);
                }else try {
                    using var restricted=RestrictedExecution.EnterSlack();var done=true;
                    foreach(var file in waiting.FileIds){var a=store.GetAttachment(waiting.Id,file);if(a?.OcrJob is null){done=false;break;}var state=await remote.State(waiting.Id,file,token);if(state is not ("complete" or "failed" or "cancelled" or "expired")){done=false;break;}}
                    if(done)store.OcrReady(waiting.Id);
                }catch(Exception e)when(e is SlackDeferred or HttpRequestException or IOException or JsonException or InvalidDataException or OperationCanceledException){if(token.IsCancellationRequested)throw;}
            }
            if((remote is not null || (priority??throw new InvalidOperationException("SLACK_LOCAL_OCR_REQUIRED")).Admission())&&store.ClaimNext() is {} job){
                using var restricted=RestrictedExecution.EnterSlack();
                try{
                    if(job.FileIds.Length==0){
                        string[] files;
                        try{var session=store.GetSession(job.SessionId);files=await api.RootFiles(session.Channel,session.Thread,options,token);}
                        catch(Exception e)when(e is SlackRejected or UnauthorizedAccessException or InvalidDataException or HttpRequestException or IOException or JsonException){store.Finish(job.Id,"SLACK_ROOT_FILE_REQUIRED",false,true);continue;}
                        if(files.Length==0){store.Finish(job.Id,"SLACK_ROOT_FILE_REQUIRED",false,true);continue;}
                        job=store.BindRootFiles(job.Id,files);
                    }
                    if(job.FileIds.Length>0){
                        var refs=new List<object>();var texts=new List<string>();var ready=true;
                        foreach(var file in job.FileIds){var a=store.GetAttachment(job.Id,file);
                            if(a is null){var downloaded=await api.Download(file,options,token);a=store.SaveAttachment(job.Id,file,downloaded.Extension,downloaded.Bytes);}
                            if(a.Result is null){
                                if(remote is not null){
                                    var expected=SlackRemoteOcr.JobId(job.Id,file);
                                    if(a.OcrJob is null){await remote.Submit(job.Id,a,token);store.BindOcr(job.Id,file,expected);a=store.GetAttachment(job.Id,file)!;}
                                    if(a.OcrJob!=expected)throw new InvalidDataException("SLACK_OCR_JOB_CHANGED");
                                    var remoteState=await remote.State(job.Id,file,token);
                                    if(remoteState is "failed" or "expired" or "cancelled")throw new InvalidDataException("SLACK_OCR_FAILED");
                                    if(remoteState!="complete"){ready=false;continue;}
                                    store.SaveOcrResult(job.Id,file,await remote.Result(job.Id,file,a.Hash,token));a=store.GetAttachment(job.Id,file)!;
                                }else{
                                var localOcr=ocr??throw new InvalidOperationException("SLACK_LOCAL_OCR_REQUIRED");
                                var os=new OcrJobStore(localOcr);
                                if(a.OcrJob is null){
                                    var inbox=Path.Combine(localOcr.InputRoots[0],"slack-restricted");CanonicalStore.GuardPath(inbox);Directory.CreateDirectory(inbox);
                                    var input=SlackStagingFiles.PathFor(inbox,job.Id,file,a.Hash,a.Extension);CanonicalStore.GuardPath(input);
                                    try{
                                        if(File.Exists(input)){if(OcrJobStore.HashFile(input)!=a.Hash)throw new InvalidDataException("SLACK_INPUT_CHANGED");}else{using var f=new FileStream(input,FileMode.CreateNew,FileAccess.Write,FileShare.None);f.Write(a.Bytes);f.Flush(true);}
                                        var registered=os.Submit(input,a.Hash,a.Extension==".pdf"?"PDF_DOCUMENT":"AUTO","slack_"+job.Id+"_"+file,origin:"slack");
                                        store.BindOcr(job.Id,file,registered.JobId);a=store.GetAttachment(job.Id,file)!;
                                    }finally{SlackStagingFiles.DeleteIfHashMatches(input,a.Hash);}
                                }
                                var state=os.GetState(a.OcrJob!);if(state.Status is "failed" or "expired" or "cancelled")throw new InvalidDataException("SLACK_OCR_FAILED");
                                if(state.Status!="complete"){ready=false;continue;}
                                store.SaveOcrResult(job.Id,file,os.Result(a.OcrJob!).GetRawText());a=store.GetAttachment(job.Id,file)!;
                                }
                            }
                            refs.Add(new{file_id=file,input_hash=a.Hash,ocr_job=a.OcrJob,result_hash=SlackHash.Of(a.Result!),external_ai="deny"});
                            using var extracted=JsonDocument.Parse(a.Result!);texts.Add(extracted.RootElement.GetProperty("text").GetString()??"");
                        }
                        if(!ready){store.WaitForOcr(job.Id);}else{
                            var raw=string.Join("\n\n",texts);
                            if(SlackResultText.OcrOnly(job.Text))store.Finish(job.Id,JsonSerializer.Serialize(new{ocr=refs,output_kind="ocr_text",restriction=SlackRestriction.Instance}),true,true);
                            else{
                                if(System.Text.Encoding.UTF8.GetByteCount(raw)>48000)throw new InvalidDataException("SLACK_PDF_ANALYSIS_REQUIRES_SMALLER_SECTION");
                                var document=SlackConversation.CurrentDocument(store,job);
                                var context=JsonSerializer.Serialize(document);
                                if(!SlackConversation.Fits(context,job.Text))throw new InvalidDataException("SLACK_INFERENCE_INPUT_BUDGET");
                                using var inferenceDeadline=CancellationTokenSource.CreateLinkedTokenSource(token);
                                inferenceDeadline.CancelAfter(TimeSpan.FromSeconds(options.InferenceSeconds+5));
                                var interpreted=await inference.Chat(job.Id,job.SessionId,context,job.Text,options.OutputTokens,options.InferenceSeconds,inferenceDeadline.Token);
                                store.Finish(job.Id,JsonSerializer.Serialize(new{ocr=refs,interpretation=interpreted,restriction=SlackRestriction.Instance}),true,true);
                            }
                        }
                    }
                }catch(SlackDeferred){store.Defer(job.Id);}
                catch(SlackPriorityYield yielded)when(!token.IsCancellationRequested){store.SuspendChat(job.Id,yielded.Code);store.Defer(job.Id,yielded.Code);}
                catch(OperationCanceledException)when(!token.IsCancellationRequested){store.Defer(job.Id);}
                catch(SlackRateLimit rate){store.Defer(job.Id);await Task.Delay(TimeSpan.FromSeconds(rate.Seconds),token);}
                catch(Exception e)when(e is SlackRejected or SlackInterrupted or IOException or JsonException or HttpRequestException or InvalidDataException or UnauthorizedAccessException or OcrException){
                    store.Finish(job.Id,"LOCAL_PROCESSING_FAILED",false,true);
                }
            }
            await Task.Delay(1000,token);
        }
    }
    internal async Task Maintenance(CancellationToken token)
    {
        var nextCleanup=DateTimeOffset.MinValue;
        while(!token.IsCancellationRequested){
            using var restricted=RestrictedExecution.EnterSlack();
            var now=DateTimeOffset.UtcNow;
            if(now>=nextCleanup){store.Cleanup(now,false);nextCleanup=now.AddDays(1);}
            // Release derived cache before any early deletion. CPU load alone never deletes a session.
            if(store.StoredBytes()>384L*1024*1024){store.DropSummaryCache();store.Cleanup(now,true,()=>store.StoredBytes()>384L*1024*1024);}
            await Task.Delay(TimeSpan.FromSeconds(60),token);
        }
    }
}
