using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;
using LocalBrain.ClientHost.Ocr;

namespace LocalBrain.ClientHost.Slack;

internal sealed class SlackDeferred : Exception { }
internal sealed class SlackInterrupted : Exception { }
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
        var frozen=store.FreezeInference(route,supplied.GetProperty("request_id").GetString()!,supplied.GetProperty("session_id").GetString()!,supplied.GetRawText());
        var actual=DecisionProtocol.Parse(frozen);ValidateInput(route,actual);
        string response;
        try{response=await gateway.SlackJsonAsync(route,frozen,token);}
        catch(OperationCanceledException){
            // Closing a Client HTTP request alone does not prove the Server model stopped.
            // Cancel only this immutable Session/request using the existing authenticated local route.
            using var cancelDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try{await gateway.SlackJsonAsync("/v1/slack/cancel",JsonSerializer.Serialize(new{request_id=actual.GetProperty("request_id").GetString(),session_id=actual.GetProperty("session_id").GetString(),restriction=SlackRestriction.Instance},SlackOptions.Json),cancelDeadline.Token);}
            catch(Exception e)when(e is HttpRequestException or OperationCanceledException or IOException or JsonException or InvalidDataException or UnauthorizedAccessException){}
            throw;
        }
        if(response.Length>128000)throw new InvalidDataException("SLACK_LOCAL_RESPONSE_SIZE");
        var json=JsonSerializer.Deserialize<JsonElement>(response);
        var state=json.GetProperty("status").GetString();if(state is "deferred" or "busy" && json.TryGetProperty("retry_safe",out var safe)&&safe.ValueKind==JsonValueKind.True)throw new SlackDeferred();
        if(state=="interrupted")throw new SlackInterrupted();
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
    internal SlackPriority(OcrConfig ocr){this.ocr=ocr;}
    private SlackPriority(OcrConfig ocr,Func<OcrResources.Sample> sample){this.ocr=ocr;this.sample=sample;}
    internal static SlackPriority ForFixture(OcrConfig ocr,Func<OcrResources.Sample> sample)=>new(ocr,sample);
    private readonly OcrResources resources=new();
    private long quietAfter;
    internal bool ForegroundActive()=>OcrPriority.Active(ocr)||!OcrPriority.Healthy(ocr)||OcrPriority.LegacyConsumerActive(ocr);
    internal bool Admission()
    {
        if(ForegroundActive()){quietAfter=Environment.TickCount64+5000;return false;}
        if(Environment.TickCount64<quietAfter)return false;
        var r=sample?.Invoke()??resources.Read();return OcrResources.Refusal(ocr,r,false) is null;
    }
    internal async Task<T> Run<T>(Func<CancellationToken,Task<T>> work,int seconds,CancellationToken token)
    {
        if(!Admission())throw new SlackDeferred();
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(token);stop.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var watcherStop=new CancellationTokenSource();
        var watcher=Task.Run(async()=>{try{while(!watcherStop.IsCancellationRequested){await Task.Delay(50,watcherStop.Token);if(ForegroundActive()){stop.Cancel();return;}}}catch(OperationCanceledException){}});
        try{return await work(stop.Token);}finally{watcherStop.Cancel();await watcher;}
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

internal sealed class SlackProcessor(SlackOptions options,SlackStore store,SlackApi api,ISlackLocalInference inference,OcrConfig ocr,SlackPriority priority)
{
    internal async Task Run(CancellationToken token)
    {
        while(!token.IsCancellationRequested){
            SlackResultReturnRequests.Step(options,store);
            foreach(var waiting in store.WaitingForOcr()){
                if(waiting.FileIds.All(f=>store.GetAttachment(waiting.Id,f) is {OcrJob: {} id} && new OcrJobStore(ocr).GetState(id).Status is "complete" or "failed" or "cancelled" or "expired"))store.OcrReady(waiting.Id);
            }
            if(priority.Admission()&&store.ClaimNext() is {} job){
                using var restricted=RestrictedExecution.EnterSlack();
                try{
                    if(job.FileIds.Length>0){
                        var refs=new List<object>();var texts=new List<string>();var ready=true;
                        foreach(var file in job.FileIds){var a=store.GetAttachment(job.Id,file);
                            if(a is null){var downloaded=await api.Download(file,options,token);a=store.SaveAttachment(job.Id,file,downloaded.Extension,downloaded.Bytes);}
                            if(a.Result is null){
                                var os=new OcrJobStore(ocr);
                                if(a.OcrJob is null){
                                    var inbox=Path.Combine(ocr.InputRoots[0],"slack-restricted");CanonicalStore.GuardPath(inbox);Directory.CreateDirectory(inbox);
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
                            refs.Add(new{file_id=file,input_hash=a.Hash,ocr_job=a.OcrJob,result_hash=SlackHash.Of(a.Result!),external_ai="deny"});
                            using var extracted=JsonDocument.Parse(a.Result!);texts.Add(extracted.RootElement.GetProperty("text").GetString()??"");
                        }
                        if(!ready){store.WaitForOcr(job.Id);}else{
                            var raw=string.Join("\n\n",texts);
                            if(SlackResultText.OcrOnly(job.Text))store.Finish(job.Id,JsonSerializer.Serialize(new{ocr=refs,output_kind="ocr_text",restriction=SlackRestriction.Instance}),true,true);
                            else{
                                if(System.Text.Encoding.UTF8.GetByteCount(raw)>48000)throw new InvalidDataException("SLACK_PDF_ANALYSIS_REQUIRES_SMALLER_SECTION");
                                var session=store.GetSession(job.SessionId);var history=store.History(job.SessionId).Where(x=>x.Id!=job.Id&&x.Phase=="complete").ToArray();
                                var document=SlackConversation.CurrentDocument(store,job);
                                var context=SlackConversation.Context(store,session,history,job.Text,options.CompressionThreshold,document);
                                if(context is null){store.WaitForSummary(job.Id,SlackConversation.JobBudget(store,job));continue;}
                                var interpreted=await priority.Run(t=>inference.Chat(job.Id,job.SessionId,context,job.Text,options.OutputTokens,options.InferenceSeconds,t),options.InferenceSeconds+5,token);
                                store.Finish(job.Id,JsonSerializer.Serialize(new{ocr=refs,interpretation=interpreted,restriction=SlackRestriction.Instance}),true,true);
                            }
                        }
                    }else{
                        var session=store.GetSession(job.SessionId);var history=store.History(job.SessionId).Where(x=>x.Id!=job.Id&&x.Phase=="complete").ToArray();
                        var context=SlackConversation.Context(store,session,history,job.Text,options.CompressionThreshold);
                        if(context is null){store.WaitForSummary(job.Id,SlackConversation.Budget(job.Text));continue;}
                        var result=await priority.Run(t=>inference.Chat(job.Id,job.SessionId,context,job.Text,options.OutputTokens,options.InferenceSeconds,t),options.InferenceSeconds+5,token);
                        var sensitive=history.Any(x=>x.FileIds.Length>0);store.Finish(job.Id,result,true,sensitive);
                    }
                }catch(SlackDeferred){store.Defer(job.Id);}
                catch(OperationCanceledException)when(!token.IsCancellationRequested){store.Defer(job.Id);}
                catch(SlackRateLimit rate){store.Defer(job.Id);await Task.Delay(TimeSpan.FromSeconds(rate.Seconds),token);}
                catch(Exception e)when(e is SlackRejected or SlackInterrupted or IOException or JsonException or HttpRequestException or InvalidDataException or UnauthorizedAccessException or OcrException){
                    store.Finish(job.Id,"LOCAL_PROCESSING_FAILED",false,true);
                }
            }
            await Task.Delay(1000,token);
        }
    }
    private async Task Compress(string sessionId,CancellationToken token)
    {
        if(!priority.Admission())return;
        await using var memory=new TaskMemoryQ8(CanonicalStore.DefaultRoot,background:true);
        if(!memory.Enabled){store.SummaryState(sessionId,"summary_unavailable");store.FailWaitingSummary(sessionId);return;}
        try{
            store.SummaryState(sessionId,"summary_started");
            var completed=await SlackConversation.Compress(store,sessionId,store.WaitingSummaryBudget(sessionId),
                (source,budget,t)=>priority.Run(ct=>memory.SummarizeSlackAsync(source,ct,budget),TaskMemoryQ8.SlackSummaryOperationSeconds,t),
                ()=>priority.Admission()&&!store.HasPendingJobs(),token);
            store.SummaryState(sessionId,completed?"summary_ready":"summary_deferred");
        }catch(IOException){store.SummaryState(sessionId,"summary_deferred");/* Preserve another owner and the existing checkpoint. */}
        catch(OperationCanceledException)when(!token.IsCancellationRequested){store.SummaryState(sessionId,"summary_deferred");}
        catch(Exception e)when(e is InvalidDataException or JsonException or UnauthorizedAccessException){store.SummaryState(sessionId,"summary_unavailable");store.FailWaitingSummary(sessionId);}
    }
    internal static SlackStore.Session[] MaintenanceOrder(SlackStore store)=>store.Sessions().OrderByDescending(s=>store.HasSummaryWaiter(s.Id)).ToArray();
    internal async Task Maintenance(CancellationToken token)
    {
        var nextCleanup=DateTimeOffset.MinValue;
        while(!token.IsCancellationRequested){
            using var restricted=RestrictedExecution.EnterSlack();
            var now=DateTimeOffset.UtcNow;
            if(now>=nextCleanup){store.Cleanup(now,false);nextCleanup=now.AddDays(1);}
            // Release derived cache before any early deletion. CPU load alone never deletes a session.
            if(store.StoredBytes()>384L*1024*1024){store.DropSummaryCache();store.Cleanup(now,true,()=>store.StoredBytes()>384L*1024*1024);}
            if(!store.HasPendingJobs()&&priority.Admission()){
                foreach(var s in MaintenanceOrder(store)){
                    if(!priority.Admission()||store.HasPendingJobs())break;
                    var history=store.History(s.Id).Where(x=>x.Phase=="complete").ToArray();
                    try{
                        if(SlackConversation.NeedsCompression(store,s,options.CompressionThreshold))await Compress(s.Id,token);
                        if(store.HasPendingJobs())break;
                        if(history.Length==0)continue; // Unacknowledged/uncertain intake is not review evidence.
                        var source=JsonSerializer.Serialize(history.Select(x=>new{message_id=x.EventId,user=x.Text,result=x.Result}));
                        var sourceHash=SlackHash.Of(source);if(System.Text.Encoding.UTF8.GetByteCount(source)>16000||store.ExtractionDone(s.Id,sourceHash))continue;
                        var candidate=await priority.Run(t=>inference.Candidate(s.Id,source,t),65,token);
                        if(candidate is not null){
                            var registered=await inference.RegisterProposal(SlackHash.Of(s.Id+sourceHash)[..32],candidate.RequestId,s.Id,token);
                            store.BindServerProposal(s.Id,registered,candidate.ReviewJson,now);
                            OcrJobStore.Atomic(Path.Combine(options.Root,"notification.json"),new{pending_count=store.Proposals().Count(p=>p.State=="pending"),revision=SlackHash.Of(registered)});
                        }
                        store.ExtractionComplete(s.Id,sourceHash);
                    }catch(SlackInterrupted){store.ExtractionComplete(s.Id,SlackHash.Of(JsonSerializer.Serialize(history.Select(x=>new{message_id=x.EventId,user=x.Text,result=x.Result}))));}
                    catch(Exception e)when(e is SlackDeferred or IOException or HttpRequestException or JsonException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException){if(token.IsCancellationRequested)throw new OperationCanceledException(token);}
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(60),token);
        }
    }
}
