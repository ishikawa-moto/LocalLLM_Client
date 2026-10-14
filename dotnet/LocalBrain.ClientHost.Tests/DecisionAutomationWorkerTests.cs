using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.AgentV2;

internal static class DecisionAutomationWorkerTests
{
    private sealed class Peer:HttpMessageHandler
    {
        internal readonly Dictionary<string,JsonElement> Proposals=[];
        internal readonly Dictionary<string,JsonElement> Registrations=[];
        internal readonly Dictionary<string,JsonElement> Results=[];
        internal string PolicyHash="",ReviewState="approved";
        internal bool LoseReply;
        internal bool DeadlineOnce;
        internal int ApplyCalls,RegisterCalls,ProposalCalls,ControlReads;
        internal bool ReadyForReview=true;
        internal byte[]? LastApply;
        internal TaskCompletionSource? BlockStatus,StatusEntered;
        protected override void Dispose(bool disposing){} // Owned by this disposable fixture, across client instances.
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.Method==HttpMethod.Get&&request.RequestUri!.AbsolutePath=="/v1/control/status"){
                ControlReads++;return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{active_task=(string?)null,queue_depth=ReadyForReview?0:1,resources=new{accepting_jobs=ReadyForReview,ram_used_gb=16.25,gpu_free_gb=7.125},last_model_request_age_seconds=42.5}),Encoding.UTF8,"application/json")};
            }
            var body=await request.Content!.ReadAsStringAsync(token);var p=DecisionProtocol.Parse(body);
            var route=request.RequestUri!.AbsolutePath;
            JsonElement response;
            if(route=="/v1/brain/propose-bound-decision"){
                var id=p.GetProperty("request_id").GetString()!;
                ProposalCalls++;
                if(id=="worker_conflict")return new(HttpStatusCode.Conflict){Content=new StringContent("{\"status\":\"rejected\",\"reason_code\":\"legacy_or_partial_proposal\",\"reason\":\"Existing legacy proposal is preserved.\"}",Encoding.UTF8,"application/json")};
                if(id=="worker_foreign_denial")return new(HttpStatusCode.Conflict){Content=new StringContent("{\"status\":\"rejected\",\"reason_code\":\"wrong_proposal\",\"request_id\":\"other_request\"}",Encoding.UTF8,"application/json")};
                if(id=="worker_bad_denial")return new(HttpStatusCode.Conflict){Content=new StringContent("{\"status\":42,\"reason_code\":\"wrong_proposal\"}",Encoding.UTF8,"application/json")};
                var proposal=JsonSerializer.SerializeToElement(new{schema=DecisionProtocol.ProposalSchema,proposal_id=id,kind="decision",project=p.GetProperty("project"),title=p.GetProperty("title"),body=p.GetProperty("body"),references=p.GetProperty("references")});
                Proposals[id]=proposal;var hash=DecisionProtocol.Digest(proposal);
                response=JsonSerializer.SerializeToElement(new{proposal_id=id,proposal_hash=hash,schema=DecisionProtocol.ProposalSchema,status="draft",payload=proposal,content=proposal.GetProperty("body"),path="drafts/pending-review/"+id+".md",file_hashes=new[]{CanonicalStore.Hash(Encoding.UTF8.GetBytes("# "+proposal.GetProperty("title").GetString()+"\n\n"+proposal.GetProperty("body").GetString()+"\n")),new string('b',64),hash}});
            }else if(route=="/v1/brain/decision-review-register"){
                Check(request.Headers.GetValues("X-LocalBrain-Approval-Owner").Single()==DecisionProtocol.Owner,"Register was not Host-owned");
                var registrationId=p.GetProperty("proposal_id").GetString()!;
                if(Registrations.ContainsKey(registrationId)&&ReviewState=="deferred"&&ReadyForReview)ReviewState="approved";
                Registrations[registrationId]=p;RegisterCalls++;response=Review(registrationId);
            }else if(route=="/v1/brain/decision-review-status"){
                if(DeadlineOnce){DeadlineOnce=false;throw new OperationCanceledException("Synthetic per-request deadline; owner disposal was not requested");}
                StatusEntered?.TrySetResult();if(BlockStatus is not null)await BlockStatus.Task.WaitAsync(token);
                response=Review(p.GetProperty("proposal_id").GetString()!);
            }else if(route=="/v1/brain/apply-decision"){
                Check(request.Headers.GetValues("X-LocalBrain-Approval-Owner").Single()==DecisionProtocol.Owner,"Apply was not Host-owned");
                ApplyCalls++;LastApply=Encoding.UTF8.GetBytes(body);var a=p.GetProperty("approval");var id=p.GetProperty("request_id").GetString()!;
                if(id=="worker_apply_denied")return new(HttpStatusCode.Forbidden){Content=new StringContent("{\"detail\":\"Forbidden\"}",Encoding.UTF8,"application/json")};
                response=JsonSerializer.SerializeToElement(new{status="applied",operation="decision",request_id=id,approval_id=a.GetProperty("approval_id"),task_id=a.GetProperty("task_id"),client_certificate_sha256=new string('d',64),git_commit=new string('e',40),audit_id=Guid.NewGuid().ToString(),destination="decisions/pc73-"+id+".md",paths=new[]{"decisions/pc73-"+id+".md","decisions/pc73-"+id+".md.meta.json"},origin_proposal_id=p.GetProperty("origin_proposal_id"),expected_proposal_hash=p.GetProperty("expected_proposal_hash"),consumption_hash=new string('f',64)});
                Results[id]=response;if(LoseReply){LoseReply=false;throw new HttpRequestException("Fixture lost reply after committed result");}
            }else throw new Exception("Unexpected automation route: "+route);
            return new(HttpStatusCode.OK){Content=new StringContent(response.GetRawText(),Encoding.UTF8,"application/json")};
        }
        private JsonElement Review(string id){
            var reg=Registrations[id];var record=JsonSerializer.SerializeToElement(new{schema="localbrain.decision-review.v1",policy=AutomaticDecisionPolicy.Name,model="fixture_only",proposal_id=id,proposal_hash=DecisionProtocol.Digest(Proposals[id]),request_mode=reg.GetProperty("request_mode"),host_policy_hash=PolicyHash,task_id=reg.GetProperty("task_id"),workspace_id=reg.GetProperty("workspace_id"),client_certificate_sha256=new string('d',64),state=ReviewState,review_passes=ReviewState=="approved"?2:0,passes=ReviewState=="approved"?new[]{new[]{new{id=1,source_path="raw/fixture.md",source_quote="Synthetic fixture"}},new[]{new{id=1,source_path="raw/fixture.md",source_quote="Synthetic fixture"}}}:[],issues=ReviewState=="returned"?new[]{new{code="insufficient",message="Fixture evidence missing"}}:[],return_count=ReviewState=="returned"?1:0});
            return JsonSerializer.SerializeToElement(new{schema="localbrain.decision-review.v1",proposal_id=id,proposal_hash=DecisionProtocol.Digest(Proposals[id]),state=ReviewState,review_receipt=record,review_receipt_hash=DecisionProtocol.Digest(record),apply_state=Results.ContainsKey(id)?"terminal":"not_started",apply_result=Results.TryGetValue(id,out var r)?(JsonElement?)JsonSerializer.SerializeToElement(new{http=200,result=r}):null});
        }
    }
    internal static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"decision-worker-fixture-"+Guid.NewGuid().ToString("N"));var repo=Path.Combine(root,"repo");var host=Path.Combine(root,"host");Directory.CreateDirectory(repo);Directory.CreateDirectory(host);
        Git(repo,"init");Git(repo,"-c","user.name=Fixture","-c","user.email=fixture@example.invalid","commit","--allow-empty","-m","Disposable fixture");
        var projects=Path.Combine(root,"projects.json");File.WriteAllText(projects,JsonSerializer.Serialize(new[]{repo}));
        var config=new ClientConfig("https://fixture.invalid",1,"fixture",projects,Path.Combine(root,"audit.jsonl"));
        var policy=JsonSerializer.SerializeToUtf8Bytes(new{version=1,enabled=true,policy=AutomaticDecisionPolicy.Name,workspace=repo,projects=new[]{"demo"},authorization_source="DISPOSABLE_SYNTHETIC_FIXTURE_ONLY"});
        File.WriteAllBytes(Path.Combine(host,"automatic-decision-policy.json"),policy);var peer=new Peer{PolicyHash=CanonicalStore.Hash(policy)};
        DecisionAutomation Worker()=>DecisionAutomation.ForFixture(config,host,()=>new GatewayClient(new Uri("https://fixture.invalid"),peer),new string('d',64));
        string Request(string id,string title="公開fixture")=>JsonSerializer.Serialize(new{request_id=id,project="demo",title,body="合成の根拠 😀\n非本番",references=new[]{new{path="raw/fixture.md",expected_hash=new string('a',64),expected_metadata_hash=new string('b',64),relevant_location="L1"}}});
        await using(var first=Worker()){
            var watch=Stopwatch.StartNew();var queued=DecisionProtocol.Parse(await first.EnqueueAsync(Request("worker_good"),"proposal"));Check(queued.GetProperty("status").GetString()=="queued"&&watch.ElapsedMilliseconds<5000,"Queue did not return promptly");
            await first.StepFixtureAsync("worker_good");Check(DecisionProtocol.Parse(await first.StatusAsync("worker_good")).GetProperty("applied").GetBoolean(),"Worker did not complete normal PASS adoption");
            await first.EnqueueAsync(Request("worker_good"),"proposal");Check(peer.ApplyCalls==1,"Terminal enqueue repeated apply");
            await first.EnqueueAsync(Request("worker_conflict"),"proposal");await first.StepFixtureAsync("worker_conflict");
            var deniedCalls=peer.ProposalCalls;await first.StepFixtureAsync("worker_conflict");
            Check(peer.ProposalCalls==deniedCalls&&DecisionProtocol.Parse(await first.StatusAsync("worker_conflict")).GetProperty("status").GetString()=="rejected","Business conflict retried automatically");
            var refusal=DecisionProtocol.Parse(await first.StatusAsync("worker_conflict")).GetProperty("result");
            Check(refusal.GetProperty("reason_code").GetString()=="legacy_or_partial_proposal"&&refusal.GetProperty("reason").GetString()=="Existing legacy proposal is preserved."&&refusal.GetProperty("http_status").GetInt32()==409&&refusal.GetProperty("backend_response_sha256").GetString()!.Length==64&&!refusal.GetProperty("automatic_retry").GetBoolean(),"Authenticated409 business reason was hidden or caused retry");
            try{await first.EnqueueAsync(Request("worker_good","Changed"),"proposal");throw new Exception("Changed request accepted");}catch(InvalidDataException){}
            foreach(var deniedId in new[]{"worker_foreign_denial","worker_bad_denial"}){
                await first.EnqueueAsync(Request(deniedId),"proposal");await first.StepFixtureAsync(deniedId);var calls=peer.ProposalCalls;
                await first.StepFixtureAsync(deniedId);var rejected=DecisionProtocol.Parse(await first.StatusAsync(deniedId));
                Check(peer.ProposalCalls==calls&&rejected.GetProperty("status").GetString()=="rejected"&&rejected.GetProperty("result").GetProperty("reason_code").GetString()=="backend_request_denied","Malformed or foreign denial was trusted or replayed");
            }
            peer.ReviewState="returned";await first.EnqueueAsync(Request("worker_returned"),"user_request");await first.StepFixtureAsync("worker_returned");
            Check(DecisionProtocol.Parse(await first.StatusAsync("worker_returned")).GetProperty("status").GetString()=="returned"&&peer.ApplyCalls==1,"Returned request was adopted");
            // Simulate a process interruption between its canonical native request and projection update.
            // Synthetic receipt only; no user confirmation or window is created in this fixture.
            var jobPath=Path.Combine(host,"automatic-decision-outbox","worker_returned.json");
            var returnedJob=JsonSerializer.Deserialize<DecisionAutomation.Job>(File.ReadAllBytes(jobPath))!;
            var reviewHash=DecisionProtocol.Parse(returnedJob.ReviewJson!).GetProperty("review_receipt_hash").GetString()!;
            var nonce=Guid.NewGuid().ToString("N");var inputPath=Path.Combine(root,"native-fixture-input.json");var outputPath=Path.Combine(root,"native-fixture-output.json");
            var inputBytes=JsonSerializer.SerializeToUtf8Bytes(new{fixture="SYNTHETIC_ONLY",nonce});File.WriteAllBytes(inputPath,inputBytes);
            using(var store=new CanonicalStore(host)){
                var inputHash=store.PutEvidence(inputBytes);var pending=new DecisionNativeConfirmation.Pending(nonce,inputPath,outputPath,inputHash,returnedJob.IntentHash!,reviewHash);
                store.Append(new(returnedJob.TaskId!,HostTaskJournal.WorkspaceId(repo),"windows_host","fixture_native_request","decision_native_reaffirmation_requested","human_confirmation_request","host_verified",[inputHash],JsonSerializer.SerializeToElement(new{intent_hash=returnedJob.IntentHash,review_receipt_hash=reviewHash,pending})));
            }
            Check(DecisionProtocol.Parse(await first.ReaffirmAsync("worker_returned")).GetProperty("status").GetString()=="human_confirmation_pending","Missing native reply caused duplicate launch");
            File.WriteAllBytes(jobPath,JsonSerializer.SerializeToUtf8Bytes(returnedJob));
            File.WriteAllText(outputPath,JsonSerializer.Serialize(new{source="windows_native_dialog",confirmed=true,nonce,intent_hash=returnedJob.IntentHash,review_receipt_hash=reviewHash,fixture="SYNTHETIC_NOT_HUMAN"}));
            Check(DecisionProtocol.Parse(await first.ReaffirmAsync("worker_returned")).GetProperty("status").GetString()=="human_confirmation_pending","Committed native orphan caused duplicate launch");
            using(var store=new CanonicalStore(host))Check(!store.EventIds(returnedJob.TaskId!).Select(id=>store.ReadTaskEvent(returnedJob.TaskId!,id)).Any(e=>e.Input.EventType=="decision_native_dialog_launch_reserved"),"Orphan recovery launched a new dialog");
            peer.ReviewState="approved";peer.LoseReply=true;await first.EnqueueAsync(Request("worker_reply_loss"),"proposal");
            try{await first.StepFixtureAsync("worker_reply_loss");throw new Exception("Expected reply loss");}catch(HttpRequestException){}
            Check(DecisionProtocol.Parse(await first.StatusAsync("worker_reply_loss")).GetProperty("status").GetString()=="dispatch_pending","Unknown reply was not preserved");
        }
        var oldBytes=peer.LastApply!;
        await using(var restarted=Worker()){
            await restarted.StepFixtureAsync("worker_reply_loss");Check(peer.ApplyCalls==2&&peer.LastApply!.SequenceEqual(oldBytes)&&DecisionProtocol.Parse(await restarted.StatusAsync("worker_reply_loss")).GetProperty("applied").GetBoolean(),"Restart repeated committed side effect");
            peer.StatusEntered=new();peer.BlockStatus=new();await restarted.EnqueueAsync(Request("worker_pending"),"proposal");var step=restarted.StepFixtureAsync("worker_pending");await peer.StatusEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var status=await restarted.StatusAsync("worker_pending").WaitAsync(TimeSpan.FromSeconds(2));Check(DecisionProtocol.Parse(status).GetProperty("status").GetString()=="review_queued","Status blocked behind model/network work");peer.BlockStatus.SetResult();await step;
            await restarted.EnqueueAsync(Request("worker_apply_denied"),"proposal");await restarted.StepFixtureAsync("worker_apply_denied");var deniedCalls=peer.ApplyCalls;
            await restarted.StepFixtureAsync("worker_apply_denied");Check(peer.ApplyCalls==deniedCalls&&DecisionProtocol.Parse(await restarted.StatusAsync("worker_apply_denied")).GetProperty("status").GetString()=="attention_required","Malformed owned403 was replayed or treated as a verified receipt");
        }
        var resumeHost=Path.Combine(root,"deferred-host");Directory.CreateDirectory(resumeHost);File.WriteAllBytes(Path.Combine(resumeHost,"automatic-decision-policy.json"),policy);
        var deferredPeer=new Peer{PolicyHash=peer.PolicyHash,ReviewState="deferred",ReadyForReview=false};
        await using(var deferred=DecisionAutomation.ForFixture(config,resumeHost,()=>new GatewayClient(new Uri("https://fixture.invalid"),deferredPeer),new string('d',64))){
            await deferred.EnqueueAsync(Request("worker_deferred"),"user_request");await deferred.StepFixtureAsync("worker_deferred");
            Check(deferredPeer.ProposalCalls==1&&deferredPeer.RegisterCalls==1&&deferredPeer.ControlReads==1&&deferredPeer.ApplyCalls==0,"Busy deferred request was relaunched or applied");
            await deferred.StepFixtureAsync("worker_deferred");Check(deferredPeer.ControlReads==1,"Deferred recheck ignored60second throttle");
            var deferredStatus=DecisionProtocol.Parse(await deferred.StatusAsync("worker_deferred"));
            Check(deferredStatus.GetProperty("review_state").GetString()=="deferred"&&deferredStatus.GetProperty("next_review_recheck_at").GetInt64()>DateTimeOffset.UtcNow.ToUnixTimeSeconds()&&deferredStatus.GetProperty("next_step").GetString()!.Contains("Do not resubmit"),"Deferred status did not expose actual state/throttle or encouraged duplicate submission");
            var deferredPath=Path.Combine(resumeHost,"automatic-decision-outbox","worker_deferred.json");
            var deferredJob=JsonSerializer.Deserialize<DecisionAutomation.Job>(File.ReadAllBytes(deferredPath))!;
            deferredPeer.ReadyForReview=true;File.WriteAllBytes(deferredPath,JsonSerializer.SerializeToUtf8Bytes(deferredJob with{ReviewRetryAfterUtc=0}));
            await deferred.StepFixtureAsync("worker_deferred");Check(deferredPeer.ProposalCalls==1&&deferredPeer.RegisterCalls==2&&deferredPeer.ApplyCalls==0,"Ready deferral did not resignal existing registration only");
            await deferred.StepFixtureAsync("worker_deferred");Check(DecisionProtocol.Parse(await deferred.StatusAsync("worker_deferred")).GetProperty("applied").GetBoolean()&&deferredPeer.ApplyCalls==1,"Resignaled review did not use normal two-pass adoption");
            await deferred.StepFixtureAsync("worker_deferred");Check(deferredPeer.ApplyCalls==1&&deferredPeer.RegisterCalls==2,"Terminal resumed request was duplicated");
            deferredPeer.ReviewState="review_unresolved";await deferred.EnqueueAsync(Request("worker_unknown_review"),"user_request");await deferred.StepFixtureAsync("worker_unknown_review");
            var controls=deferredPeer.ControlReads;var registrations=deferredPeer.RegisterCalls;await deferred.StepFixtureAsync("worker_unknown_review");
            Check(DecisionProtocol.Parse(await deferred.StatusAsync("worker_unknown_review")).GetProperty("status").GetString()=="review_unresolved"&&deferredPeer.ControlReads==controls&&deferredPeer.RegisterCalls==registrations&&deferredPeer.ApplyCalls==1,"Unknown review was relaunched or inferred approved");
        }
        var loopHost=Path.Combine(root,"loop-host");Directory.CreateDirectory(loopHost);File.WriteAllBytes(Path.Combine(loopHost,"automatic-decision-policy.json"),policy);
        var loopPeer=new Peer{PolicyHash=peer.PolicyHash,DeadlineOnce=true};
        await using(var loop=DecisionAutomation.ForFixture(config,loopHost,()=>new GatewayClient(new Uri("https://fixture.invalid"),loopPeer),new string('d',64))){
            await loop.EnqueueAsync(Request("timeout_job_001"),"proposal");await loop.EnqueueAsync(Request("timeout_job_002"),"proposal");loop.Start();
            var limit=DateTime.UtcNow.AddSeconds(10);while(DecisionProtocol.Parse(await loop.StatusAsync("timeout_job_002")).GetProperty("status").GetString()!="applied"&&DateTime.UtcNow<limit)await Task.Delay(25);
            Check(DecisionProtocol.Parse(await loop.StatusAsync("timeout_job_002")).GetProperty("applied").GetBoolean(),"Per-request deadline stopped the entire drain loop");
            Check(DecisionProtocol.Parse(await loop.StatusAsync("timeout_job_001")).GetProperty("status").GetString()=="review_queued","Timed-out job was discarded rather than preserved");
        }
        await using(var recovery=DecisionAutomation.ForFixture(config,loopHost,()=>new GatewayClient(new Uri("https://fixture.invalid"),loopPeer),new string('d',64))){await recovery.StepFixtureAsync("timeout_job_001");Check(loopPeer.ProposalCalls==2&&loopPeer.RegisterCalls==2&&DecisionProtocol.Parse(await recovery.StatusAsync("timeout_job_001")).GetProperty("applied").GetBoolean(),"Deadline recovery repeated proposal/registration");}
        using(var store=new CanonicalStore(host)){Check(!store.EventIds(store.LatestTask(HostTaskJournal.WorkspaceId(repo))!.TaskId).Select(id=>store.ReadTaskEvent(store.LatestTask(HostTaskJournal.WorkspaceId(repo))!.TaskId,id)).Any(e=>e.Input.Producer=="actor"),"Unexpected Actor");}
        Check(!Directory.GetFiles(repo).Any()&&!Directory.Exists(Path.Combine(repo,".localbrain")),"Repository mutated by administration");
        Console.WriteLine("PASS_DISPOSABLE_WORKER_FLOW: prompt queue, dedicated bound route, owned register/apply, two-pass adoption, once-returned stop, immutable repeat, known business denial no retry, committed-reply-loss restart without second apply, responsive status, pending/confirmed native orphan no second launch; real model/native user/production0.");
    }
    private static void Check(bool value,string reason){if(!value)throw new Exception(reason);}
    private static void Git(string root,params string[] args){var start=new ProcessStartInfo("git"){WorkingDirectory=root,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in args)start.ArgumentList.Add(arg);using var process=Process.Start(start)!;var error=process.StandardError.ReadToEnd();process.StandardOutput.ReadToEnd();process.WaitForExit();if(process.ExitCode!=0)throw new Exception(error);}
}
