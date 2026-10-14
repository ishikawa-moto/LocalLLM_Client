using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

// An MCP owner queues requests; its background worker performs only durable Host dispatch.
// Local outbox files are recovery projections, never human approval evidence.
internal sealed class DecisionAutomation:IAsyncDisposable
{
    private readonly ClientConfig config;
    private readonly string hostRoot;
    private readonly Func<GatewayClient> gatewayFactory;
    private readonly Func<string> certificateHash;
    private readonly bool fixture;
    internal DecisionAutomation(ClientConfig config):this(config,CanonicalStore.DefaultRoot,()=>new GatewayClient(config),()=>{using var cert=config.LoadCertificate();return cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256).ToLowerInvariant();},false){}
    private DecisionAutomation(ClientConfig config,string hostRoot,Func<GatewayClient> gatewayFactory,Func<string> certificateHash,bool fixture){this.config=config;this.hostRoot=hostRoot;this.gatewayFactory=gatewayFactory;this.certificateHash=certificateHash;this.fixture=fixture;}
    // Test-owned root/HTTP handler; no MCP argument or environment variable selects this path.
    internal static DecisionAutomation ForFixture(ClientConfig config,string hostRoot,Func<GatewayClient> gatewayFactory,string certificateHash)=>new(config,hostRoot,gatewayFactory,()=>certificateHash,true);
    internal Task StepFixtureAsync(string id){if(!fixture)throw new UnauthorizedAccessException("Fixture stepping is unavailable in production");return AdvanceAsync(Read(id));}
    private readonly CancellationTokenSource stop=new();private readonly SemaphoreSlim gate=new(1,1);private Task? worker;
    private string QueueRoot=>Path.Combine(hostRoot,"automatic-decision-outbox");
    internal sealed record Job(string RequestId,string Root,string Mode,string PolicyHash,string InputHash,string ProposalHash,string Phase,
        string? TaskId=null,string? IntentHash=null,string? ReviewEventId=null,string? ReviewJson=null,string? ApprovalId=null,string? ResultJson=null,
        DecisionNativeConfirmation.Pending? Native=null,string? NativeEventId=null,long ReviewRetryAfterUtc=0);
    private string PathFor(string id){DecisionProtocol.Id(id);CanonicalStore.GuardPath(QueueRoot);return Path.Combine(QueueRoot,id+".json");}
    private void Save(Job job){var path=PathFor(job.RequestId);CanonicalStore.GuardPath(path);Directory.CreateDirectory(QueueRoot);var temp=path+".pending";if(File.Exists(temp))throw new IOException("Unknown pending outbox projection; inspect before recovery");File.WriteAllBytes(temp,JsonSerializer.SerializeToUtf8Bytes(job));File.Move(temp,path,true);}
    private Job Read(string id){var path=PathFor(id);CanonicalStore.GuardPath(path);using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);var j=JsonSerializer.Deserialize<Job>(input)??throw new InvalidDataException("Outbox missing");if(j.RequestId!=id)throw new InvalidDataException("Outbox identity differs");return j;}
    internal void Start(){worker??=Task.Run(LoopAsync);}
    internal async Task<string> EnqueueAsync(string requestJson,string mode)
    {
        var loaded=AutomaticDecisionPolicy.Load(config,hostRoot)??throw new UnauthorizedAccessException("Automatic adoption policy is disabled");
        if(mode is not ("proposal" or "user_request"))throw new ArgumentException("Unknown request origin");
        var request=DecisionProtocol.Parse(requestJson);var id=DecisionProtocol.Id(request.GetProperty("request_id").GetString()!);
        if(!loaded.Policy.projects.Contains(request.GetProperty("project").GetString(),StringComparer.Ordinal))throw new UnauthorizedAccessException("Project is outside installed adoption policy");
        if(HostEgress.Redact(requestJson).Count!=0)throw new UnauthorizedAccessException("Protected material cannot enter decision automation");
        var proposal=JsonSerializer.SerializeToElement(new{schema=DecisionProtocol.ProposalSchema,proposal_id=id,kind="decision",project=request.GetProperty("project"),title=request.GetProperty("title"),body=request.GetProperty("body"),references=request.GetProperty("references")});
        var inputBytes=DecisionProtocol.Utf8.GetBytes(requestJson);var inputHash=CanonicalStore.Hash(inputBytes);var proposalHash=DecisionProtocol.Digest(proposal);
        await gate.WaitAsync(stop.Token);try{
            var path=PathFor(id);
            if(File.Exists(path)){var old=Read(id);if(old.InputHash!=inputHash||old.ProposalHash!=proposalHash||old.Mode!=mode||old.PolicyHash!=loaded.Hash||old.Root!=loaded.Policy.workspace)throw new InvalidDataException("Request ID already binds different content/mode/policy");return Summary(old);}
            Directory.CreateDirectory(QueueRoot);var input=Path.Combine(QueueRoot,inputHash+".input");CanonicalStore.GuardPath(input);
            if(File.Exists(input)){if(!File.ReadAllBytes(input).SequenceEqual(inputBytes))throw new InvalidDataException("Outbox input differs");}else File.WriteAllBytes(input,inputBytes);
            var job=new Job(id,loaded.Policy.workspace,mode,loaded.Hash,inputHash,proposalHash,"queued");Save(job);if(!fixture)Start();return Summary(job);
        }finally{gate.Release();}
    }
    internal async Task<string> StatusAsync(string id){await gate.WaitAsync(stop.Token);try{return Summary(Read(id));}finally{gate.Release();}}
    internal async Task<string> ReaffirmAsync(string id){await gate.WaitAsync(stop.Token);try{
        var j=Read(id);if(j.Phase=="human_confirmation_pending")return Summary(j);
        if(j.Phase!="returned"||j.Mode!="user_request"||j.TaskId is null||j.IntentHash is null||j.ReviewJson is null)throw new UnauthorizedAccessException("Only a returned explicit request can ask for human reaffirmation");
        var loaded=AutomaticDecisionPolicy.Load(config,hostRoot)??throw new UnauthorizedAccessException("Policy disabled");if(loaded.Hash!=j.PolicyHash)throw new InvalidDataException("Policy changed");
        using var store=new CanonicalStore(hostRoot);using var journal=Journal(store,j);var intent=new DecisionAdoption(store,j.TaskId).RequireIntent(j.IntentHash);
        var reviewHash=DecisionProtocol.Parse(j.ReviewJson).GetProperty("review_receipt_hash").GetString();
        var orphan=store.EventIds(j.TaskId).Select(id=>store.ReadTaskEvent(j.TaskId,id)).LastOrDefault(e=>e.Input.Producer=="windows_host"&&e.Input.EventType=="decision_native_reaffirmation_requested"&&e.Input.Metadata.GetProperty("intent_hash").GetString()==j.IntentHash&&e.Input.Metadata.GetProperty("review_receipt_hash").GetString()==reviewHash);
        if(orphan is not null){
            var previous=orphan.Input.Metadata.GetProperty("pending").Deserialize<DecisionNativeConfirmation.Pending>()!;
            var prior=DecisionNativeConfirmation.Read(store,previous);
            // Recover an already-confirmed orphan as well as a still-open dialog.
            // A prior decline permits only this new explicit invocation to ask again.
            if(prior is null||prior.Value.GetProperty("confirmed").GetBoolean()){
                Save(j with{Native=previous,Phase="human_confirmation_pending"});return Summary(Read(id));
            }
        }
        var native=DecisionNativeConfirmation.Prepare(store,journal,intent,j.IntentHash,DecisionProtocol.Parse(j.ReviewJson));
        j=j with{Native=native,Phase="human_confirmation_pending"};Save(j);
        DecisionNativeConfirmation.Launch(store,journal,native);return Summary(j);
    }finally{gate.Release();}}
    private static HostTaskJournal Journal(CanonicalStore store,Job job){var task=store.GetTask(job.TaskId!)??throw new InvalidDataException("Canonical job owner missing");if(task.WorkspaceId!=HostTaskJournal.WorkspaceId(job.Root))throw new UnauthorizedAccessException("Job belongs to another workspace");var request=AgentTaskRequest.Parse(store.TaskRequest(task.TaskId));using var state=JsonDocument.Parse(task.StateJson);return new(store,job.Root,task.TaskId,request,state.RootElement.GetProperty("git_head").GetString()!);}
    private async Task LoopAsync(){while(!stop.IsCancellationRequested){
        try{if(Directory.Exists(QueueRoot)){
            // Only one MCP instance drains this outbox. Status reads never wait on network/model work.
            using var writer=new FileStream(Path.Combine(QueueRoot,"worker.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            foreach(var path in Directory.EnumerateFiles(QueueRoot,"*.json").Order(StringComparer.Ordinal)){
            try{var job=Read(Path.GetFileNameWithoutExtension(path));if(job.Phase is not ("applied" or "rejected" or "returned" or "review_unresolved" or "attention_required"))await AdvanceAsync(job);}catch(Exception error) when(error is not OperationCanceledException||!stop.IsCancellationRequested){Console.Error.WriteLine(JsonSerializer.Serialize(new{component="decision_automation",error_type=error.GetType().Name,action="preserve_for_recovery"}));}
            }
        }}catch(OperationCanceledException) when(stop.IsCancellationRequested){break;}catch(IOException){/* Another live owner or atomic projection; do not steal its lease. */}await Task.Delay(TimeSpan.FromSeconds(30),stop.Token);
    }}
    private async Task AdvanceAsync(Job job)
    {
        try{await StepAsync(job);}
        catch(HttpRequestException error) when(error.StatusCode is { } code && (int)code is >=400 and <500 && (int)code is not (408 or 429)){
            // A business/auth/conflict denial is not a lost transport response.
            // Before proposal creation rejection is known; after it, preserve ownership
            // and do not infer that an unknown apply did or did not commit.
            var current=Read(job.RequestId);var phase=current.Phase=="queued"?"rejected":"attention_required";
            var denial=BackendDenial(error.Message,job.RequestId);
            Save(current with{Phase=phase,ResultJson=JsonSerializer.Serialize(new{status=phase,reason_code=denial.Code,reason=denial.Reason,http_status=(int)error.StatusCode!.Value,request_id=job.RequestId,backend_response_sha256=CanonicalStore.Hash(DecisionProtocol.Utf8.GetBytes(error.Message)),automatic_retry=false})});
        }
    }
    private static (string Code,string? Reason) BackendDenial(string response,string requestId)
    {
        // Preserve an authenticated business rejection as data, never approval.
        // Unstructured, oversized, foreign-request or protected replies stay generic.
        if(DecisionProtocol.Utf8.GetByteCount(response)>16000||HostEgress.Redact(response).Count!=0)return ("backend_request_denied",null);
        try{
            using var document=JsonDocument.Parse(response);var value=document.RootElement;
            if(value.ValueKind!=JsonValueKind.Object||value.EnumerateObject().Any(p=>p.Name is not ("status" or "reason_code" or "reason" or "request_id" or "audit_id")))return ("backend_request_denied",null);
            if(!value.TryGetProperty("status",out var status)||status.GetString()!="rejected"||!value.TryGetProperty("reason_code",out var code)||code.ValueKind!=JsonValueKind.String)return ("backend_request_denied",null);
            if(value.TryGetProperty("request_id",out var id)&&(id.ValueKind!=JsonValueKind.String||id.GetString()!=requestId))return ("backend_request_denied",null);
            var text=code.GetString()!;if(!System.Text.RegularExpressions.Regex.IsMatch(text,@"\A[a-z][a-z0-9_]{0,79}\z"))return ("backend_request_denied",null);
            string? reason=null;if(value.TryGetProperty("reason",out var message)){if(message.ValueKind!=JsonValueKind.String||message.GetString()!.Length>1000)return ("backend_request_denied",null);reason=message.GetString();}
            return (text,reason);
        }catch(JsonException){return ("backend_request_denied",null);}catch(InvalidOperationException){return ("backend_request_denied",null);}
    }
    private async Task StepAsync(Job job)
    {
        var loaded=AutomaticDecisionPolicy.Load(config,hostRoot);if(loaded is null||loaded.Value.Hash!=job.PolicyHash)return;
        var policy=loaded.Value.Policy;if(policy.workspace!=job.Root)throw new UnauthorizedAccessException("Outbox root differs from policy");
        var inputPath=Path.Combine(QueueRoot,job.InputHash+".input");CanonicalStore.GuardPath(inputPath);var bytes=File.ReadAllBytes(inputPath);if(CanonicalStore.Hash(bytes)!=job.InputHash)throw new InvalidDataException("Outbox input changed");
        var request=DecisionProtocol.Parse(DecisionProtocol.Utf8.GetString(bytes));
        var expected=JsonSerializer.SerializeToElement(new{schema=DecisionProtocol.ProposalSchema,proposal_id=job.RequestId,kind="decision",project=request.GetProperty("project"),title=request.GetProperty("title"),body=request.GetProperty("body"),references=request.GetProperty("references")});
        if(request.GetProperty("request_id").GetString()!=job.RequestId||DecisionProtocol.Digest(expected)!=job.ProposalHash||!policy.projects.Contains(request.GetProperty("project").GetString(),StringComparer.Ordinal))throw new InvalidDataException("Outbox input/content/scope differs");
        using var store=new CanonicalStore(hostRoot);
        if(job.TaskId is null){var owner=DecisionAdoptionWorkspace.Open(store,job.Root,job.RequestId,job.ProposalHash,ProjectAllowlist.CurrentHead(job.Root));using var initial=owner.Journal;store.PutEvidence(bytes);store.PutEvidence(loaded.Value.Bytes);job=job with{TaskId=owner.TaskId};Save(job);}
        using var journal=Journal(store,job);var adapter=new DecisionAdoption(store,job.TaskId!);
        if(job.Phase=="queued"){
            using var gateway=gatewayFactory();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var raw=await gateway.JsonAsync(HttpMethod.Post,"/v1/brain/propose-bound-decision",DecisionProtocol.Utf8.GetString(bytes),timeout.Token);
            var receipt=DecisionProtocol.Parse(raw);var payload=DecisionProtocol.Proposal(receipt);
            if(receipt.GetProperty("proposal_hash").GetString()!=job.ProposalHash)throw new InvalidDataException("Backend proposal hash differs from exact queued content");
            var intent=adapter.PrepareProposal(raw,job.RequestId);job=job with{IntentHash=intent,Phase="register_pending"};Save(job);
        }
        if(job.Phase=="register_pending"){
            var registration=JsonSerializer.Serialize(new{proposal_id=job.RequestId,request_mode=job.Mode,host_policy_hash=job.PolicyHash,task_id=job.TaskId,workspace_id=HostTaskJournal.WorkspaceId(job.Root)});
            using var gateway=gatewayFactory();var response=await gateway.SendOwnedDecisionReviewAsync(journal,"/v1/brain/decision-review-register",registration,stop.Token);
            RecordReview(store,journal,job,response);job=job with{Phase="review_queued"};Save(job);
        }
        if(job.Phase is "review_queued" or "human_confirmation_pending"){
            using var gateway=gatewayFactory();using var statusDeadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);statusDeadline.CancelAfter(TimeSpan.FromSeconds(20));var response=await gateway.JsonAsync(HttpMethod.Post,"/v1/brain/decision-review-status",JsonSerializer.Serialize(new{proposal_id=job.RequestId}),statusDeadline.Token);
            var element=DecisionProtocol.Parse(response);var review=element.GetProperty("review_receipt");
            if(review.GetProperty("proposal_hash").GetString()!=job.ProposalHash||review.GetProperty("task_id").GetString()!=job.TaskId||review.GetProperty("host_policy_hash").GetString()!=job.PolicyHash)throw new InvalidDataException("Backend review belongs to another owner/content/policy");
            if(review.GetProperty("client_certificate_sha256").GetString()!=certificateHash())throw new UnauthorizedAccessException("Review belongs to another certificate");
            var eventId=RecordReview(store,journal,job,response);var state=element.GetProperty("state").GetString();job=job with{ReviewEventId=eventId,ReviewJson=response};
            if(job.Phase=="human_confirmation_pending"){
                if(job.Native is null)throw new InvalidDataException("Native intent missing");var confirmation=DecisionNativeConfirmation.Read(store,job.Native);if(confirmation is null){Save(job);return;}
                if(!confirmation.Value.GetProperty("confirmed").GetBoolean()){Save(job with{Phase="returned",Native=null});return;}
                var h=store.PutEvidence(DecisionProtocol.Utf8.GetBytes(confirmation.Value.GetRawText()));var human=store.Append(new(job.TaskId!,HostTaskJournal.WorkspaceId(job.Root),"windows_host","native_confirmed_"+job.Native.Nonce,"decision_native_reaffirmation_confirmed","human_statement","human_explicit",[h],JsonSerializer.SerializeToElement(new{intent_hash=job.IntentHash,review_receipt_hash=job.Native.ReviewHash})));
                job=job with{NativeEventId=human.EventId,Phase="ready"};
            }else if(state=="approved")job=job with{Phase="ready"};else if(state=="returned")job=job with{Phase="returned"};else if(state is "needs_evidence" or "review_unresolved")job=job with{Phase="review_unresolved"};
            Save(job);
            if(job.Phase=="review_queued"&&state=="deferred"&&DateTimeOffset.UtcNow.ToUnixTimeSeconds()>=job.ReviewRetryAfterUtc){
                // Only a known model-free preflight deferral is eligible. Register is
                // idempotent and signals the existing coalesced server dispatcher;
                // it never steals a lease, directly launches a model, or replays apply.
                job=job with{ReviewRetryAfterUtc=DateTimeOffset.UtcNow.ToUnixTimeSeconds()+60};Save(job);
                using var retryDeadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);retryDeadline.CancelAfter(TimeSpan.FromSeconds(20));
                // Resource telemetry includes fractional RAM/GPU readings; it is not
                // a canonical decision/approval document and must not use that digest parser.
                var statusJson=await gateway.JsonAsync(HttpMethod.Get,"/v1/control/status","",retryDeadline.Token);
                if(DecisionProtocol.Utf8.GetByteCount(statusJson)>16000)throw new InvalidDataException("Control status exceeds read budget");
                using var statusDocument=JsonDocument.Parse(statusJson,new JsonDocumentOptions{MaxDepth=16});var status=statusDocument.RootElement;
                if(status.TryGetProperty("active_task",out var active)&&active.ValueKind==JsonValueKind.Null&&status.TryGetProperty("queue_depth",out var depth)&&depth.TryGetInt32(out var queued)&&queued==0&&status.TryGetProperty("resources",out var resources)&&resources.TryGetProperty("accepting_jobs",out var accepting)&&accepting.ValueKind==JsonValueKind.True){
                    var registration=JsonSerializer.Serialize(new{proposal_id=job.RequestId,request_mode=job.Mode,host_policy_hash=job.PolicyHash,task_id=job.TaskId,workspace_id=HostTaskJournal.WorkspaceId(job.Root)});
                    var signaled=await gateway.SendOwnedDecisionReviewAsync(journal,"/v1/brain/decision-review-register",registration,retryDeadline.Token);
                    RecordReview(store,journal,job,signaled);Save(job with{ReviewJson=signaled});
                }
            }
        }
        if(job.Phase=="ready"){
            // Find a committed grant first, covering a crash before the outbox projection was updated.
            var existing=store.EventIds(job.TaskId!).Select(id=>store.ReadTaskEvent(job.TaskId!,id)).SingleOrDefault(e=>e.Input.Producer=="windows_host"&&e.Input.EventType=="decision_adoption_policy_approved"&&e.Input.EvidenceRefs[0]==job.IntentHash&&e.Input.Metadata.GetProperty("review_event_id").GetString()==job.ReviewEventId&&e.Input.Metadata.GetProperty("native_event_id").GetString()==job.NativeEventId);
            var approval=existing?.Input.Metadata.GetProperty("approval_id").GetString()??adapter.RecordReviewerApproved(job.IntentHash!,job.PolicyHash,job.ReviewEventId!,job.NativeEventId);
            job=job with{ApprovalId=approval,Phase="dispatch_pending"};Save(job);
        }
        if(job.Phase=="dispatch_pending"){
            using var gateway=gatewayFactory();using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var observed=DecisionProtocol.Parse(await gateway.JsonAsync(HttpMethod.Post,"/v1/brain/decision-review-status",JsonSerializer.Serialize(new{proposal_id=job.RequestId}),deadline.Token));
            if(observed.GetProperty("apply_state").GetString()=="in_progress")return;
            string raw;
            if(observed.GetProperty("apply_result").ValueKind!=JsonValueKind.Null){
                var envelope=observed.GetProperty("apply_result");var cached=envelope.TryGetProperty("result",out var inner)?inner:envelope;
                var code=envelope.TryGetProperty("http",out var http)?http.GetInt32():cached.GetProperty("status").GetString()=="applied"?200:409;
                var certHash=certificateHash();
                raw=await adapter.DispatchAsync(job.ApprovalId!,certHash,(_,_)=>Task.FromResult((code,cached.GetRawText())),stop.Token);
            }else raw=await adapter.DispatchAsync(job.ApprovalId!,certificateHash(),(reservation,token)=>gateway.SendOwnedDecisionAsync(journal,reservation,token),stop.Token);
            var result=DecisionProtocol.Parse(raw);job=job with{Phase=result.GetProperty("status").GetString()=="applied"?"applied":"rejected",ResultJson=raw};Save(job);
        }
    }
    private static string RecordReview(CanonicalStore store,HostTaskJournal journal,Job job,string response){var hash=store.PutEvidence(DecisionProtocol.Utf8.GetBytes(response));return store.Append(new(journal.TaskId,HostTaskJournal.WorkspaceId(job.Root),"windows_host","decision_review_"+hash,"decision_review_receipt_received","server_review_receipt","authenticated_server_response",[hash],JsonSerializer.SerializeToElement(new{review_response_hash=hash,policy_hash=job.PolicyHash}))).EventId;}
    private static string Summary(Job job){JsonElement? issues=null;string? reviewState=null;if(job.ReviewJson is not null){var r=DecisionProtocol.Parse(job.ReviewJson);reviewState=r.GetProperty("state").GetString();issues=r.GetProperty("review_receipt").GetProperty("issues").Clone();}return JsonSerializer.Serialize(new{status=job.Phase,request_id=job.RequestId,applied=job.Phase=="applied",issues,review_state=reviewState,next_review_recheck_at=reviewState=="deferred"?(long?)job.ReviewRetryAfterUtc:null,result=job.ResultJson is null?(JsonElement?)null:DecisionProtocol.Parse(job.ResultJson),next_step=job.Phase=="review_queued"&&reviewState=="deferred"?"Known model-free preflight deferral. The Host automatically rechecks acceptance at most once per60seconds and signals the existing coalesced reviewer only when idle. Do not resubmit this request.":job.Phase=="queued"?"Accepted in the Host outbox; the background worker has not yet registered review. Check status after30seconds; do not resubmit.":job.Phase=="attention_required"?"Automatic retry stopped. Inspect the preserved backend denial and existing owned receipts before recovery; do not infer saved or replay an unknown apply.":job.Phase=="returned"?"Reviewer returned this exact explicit request once. If the human repeats the save request after seeing these issues, call brain_reaffirm_decision; the Host native dialog records the human reaffirmation. Never self-reaffirm.":job.Phase=="human_confirmation_pending"?"Await the existing native Windows confirmation. If no window exists after interruption, preserve this intent for Host recovery; no automatic repeated launch.":null});}
    public async ValueTask DisposeAsync(){stop.Cancel();if(worker is not null)try{await worker;}catch(OperationCanceledException){}stop.Dispose();gate.Dispose();}
}
