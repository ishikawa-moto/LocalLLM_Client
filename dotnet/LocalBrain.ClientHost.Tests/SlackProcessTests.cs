using System.Diagnostics;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.AgentV2;
using LocalBrain.ClientHost.Ocr;
using LocalBrain.ClientHost.Slack;

internal static class SlackProcessTests
{
    internal static async Task RunAsync()
    {
        var checks=0;double ackMilliseconds=0,preemptionMilliseconds=0;void Check(bool ok,string name){if(!ok)throw new Exception("SLACK_PROCESS_TEST_FAILED: "+name);checks++;}
        var root=Directory.CreateTempSubdirectory("slack-public-control-").FullName;
        var options=new SlackOptions(true,Path.Combine(root,"slack"),"TTEST","CTEST","UBOT",Path.Combine(root,"app.token"),Path.Combine(root,"bot.token"));
        using var store=new SlackStore(options);var now=DateTimeOffset.UtcNow;var since=now.AddSeconds(-10);
        var envelope=JsonSerializer.SerializeToElement(new{envelope_id="public-envelope",type="events_api",payload=new{type="event_callback",event_id="EV-PROTOCOL",team_id="TTEST",@event=new{type="app_mention",channel="CTEST",user="UALICE",text="<@UBOT> PUBLIC",ts=now.ToUnixTimeSeconds()+".000000"}}});
        var acknowledgements=0;var timer=Stopwatch.StartNew();
        await SlackSocket.ProcessEnvelope(envelope,options,store,since,(bytes,token)=>{
            Check(JsonSerializer.Deserialize<JsonElement>(bytes.AsSpan()).GetProperty("envelope_id").GetString()=="public-envelope","exact ACK envelope");
            var reply=store.ClaimReply(now);Check(reply is not null&&reply.Text=="受け付けました。","persisted before ACK callback");Check(store.ClaimNext() is null,"no expensive work before receipt");
            store.ReplySent(reply!.Id,"1700000000.000001");acknowledgements++;return Task.CompletedTask;
        },CancellationToken.None);
        ackMilliseconds=timer.Elapsed.TotalMilliseconds;Check(timer.Elapsed<TimeSpan.FromSeconds(3),"bounded intake ACK");
        var job=store.ClaimNext()!;Check(job is not null,"accepted durable job");store.Finish(job!.Id,"PUBLIC",true,false);
        await SlackSocket.ProcessEnvelope(envelope,options,store,since,(bytes,token)=>{acknowledgements++;return Task.CompletedTask;},CancellationToken.None);
        Check(acknowledgements==2&&store.History(job.SessionId).Count==1,"repeated envelopes ACK without duplicate job");
        var ocr=new OcrConfig(true,Path.Combine(root,"ocr"),Path.Combine(root,"assets"),Path.Combine(root,"assets","python.exe"),Path.Combine(root,"assets","worker.py"),Path.Combine(root,"assets","models"),[root],new(){["a"]="a",["b"]="b",["c"]="c"},["AUTO","PDF_DOCUMENT"],[],512L<<20,512L<<20,384L<<20,30,new string('a',64));
        var config=Path.Combine(root,"ocr-config.json");File.WriteAllBytes(config,JsonSerializer.SerializeToUtf8Bytes(ocr,OcrJobStore.Json));
        var staging=Path.Combine(root,"slack-restricted");Directory.CreateDirectory(staging);
        var ownedBytes=new byte[]{1,2,3,4};var ownedHash=SlackHash.Of(ownedBytes);
        var orphan=SlackStagingFiles.PathFor(staging,new string('b',32),"FRECOVER",ownedHash,".png");File.WriteAllBytes(orphan,ownedBytes);
        var changed=SlackStagingFiles.PathFor(staging,new string('c',32),"FCHANGED",ownedHash,".png");File.WriteAllBytes(changed,[9,8,7]);
        var unrelated=Path.Combine(staging,"not-owned.png");File.WriteAllBytes(unrelated,ownedBytes);
        SlackStagingFiles.Reconcile(ocr);
        Check(!File.Exists(orphan)&&File.Exists(changed)&&File.Exists(unrelated),"restart reconciles only hash-matching owned staging files");
        SlackStagingFiles.DeleteIfHashMatches(changed,ownedHash);
        Check(File.Exists(changed),"failure cleanup preserves staging file with changed content");
        using(var legacyStore=new SlackStore(options with{Root=Path.Combine(root,"legacy-store")})){
            var legacyMention=new SlackMention("EV-LEGACY","TTEST","CTEST","UALICE",now.ToUnixTimeSeconds()+".000000",now.ToUnixTimeSeconds()+".000000","PUBLIC legacy fixture",["FLEGACY","FCHANGED"],now);
            var legacyJob=legacyStore.Accept(legacyMention,since,now).JobId!;
            legacyStore.SaveAttachment(legacyJob,"FLEGACY",".png",ownedBytes);legacyStore.BindOcr(legacyJob,"FLEGACY","public-previously-bound-ocr");
            legacyStore.SaveAttachment(legacyJob,"FCHANGED",".png",ownedBytes);
            var legacy=Path.Combine(staging,legacyJob+"-FLEGACY.png");File.WriteAllBytes(legacy,ownedBytes);
            var legacyChanged=Path.Combine(staging,legacyJob+"-FCHANGED.png");File.WriteAllBytes(legacyChanged,[9,8,7]);
            var unknown=Path.Combine(staging,new string('d',32)+"-FUNKNOWN.png");File.WriteAllBytes(unknown,ownedBytes);
            SlackStagingFiles.Reconcile(ocr,legacyStore);
            Check(!File.Exists(legacy),"restart cleans old-format original after an existing OCR binding");
            Check(File.Exists(legacyChanged)&&File.Exists(unknown)&&File.Exists(unrelated),"legacy cleanup preserves changed and unowned originals");
            Check(legacyStore.GetAttachment(legacyJob,"FLEGACY")!.Bytes.SequenceEqual(ownedBytes),"legacy cleanup preserves the durable attachment");
        }
        var old=Environment.GetEnvironmentVariable("LOCALBRAIN_OCR_CONFIG");Environment.SetEnvironmentVariable("LOCALBRAIN_OCR_CONFIG",config);
        try{
            var p=SlackPriority.ForFixture(ocr,()=>new(long.MaxValue,long.MaxValue,0));Check(p.Admission(),"idle background admission");
            using(var high=OcrPriority.Enter("taskmemory")){
                var called=false;try{await p.Run(t=>{called=true;return Task.FromResult(1);},5,CancellationToken.None);throw new Exception("important work not protected");}catch(SlackDeferred){checks++;}
                Check(!called,"important TaskMemory prevents Slack start");
            }
            var fresh=SlackPriority.ForFixture(ocr,()=>new(long.MaxValue,long.MaxValue,0));
            using var started=new SemaphoreSlim(0);var work=fresh.Run(async t=>{started.Release();await Task.Delay(TimeSpan.FromSeconds(20),t);return 1;},25,CancellationToken.None);
            await started.WaitAsync();timer.Restart();using(var high=OcrPriority.Enter("laya")){
                try{await work;throw new Exception("Slack work was not preempted");}catch(OperationCanceledException){preemptionMilliseconds=timer.Elapsed.TotalMilliseconds;Check(timer.Elapsed<TimeSpan.FromSeconds(2),"important Laya cancels running background work");}
            }
            var image=Path.Combine(root,"public.png");File.WriteAllBytes(image,[137,80,78,71,13,10,26,10]);var hash=OcrJobStore.HashFile(image);var os=new OcrJobStore(ocr);
            using(RestrictedExecution.EnterSlack()){
                var a=os.Submit(image,hash,"AUTO","slack_public_fixture",origin:"slack");Check(os.GetManifest(a.JobId).Origin=="slack","authenticated OCR provenance");
                Check(os.Submit(image,hash,"AUTO","slack_public_fixture",origin:"slack").JobId==a.JobId,"OCR recovery does not duplicate");
            }
            try{os.Submit(image,hash,"AUTO","slack_no_host",origin:"slack");throw new Exception("unbound origin");}catch(UnauthorizedAccessException){checks++;}
            var clock=now.AddDays(-8);var aged=new SlackMention("EV-FILE","TTEST","CTEST","UALICE",clock.ToUnixTimeSeconds()+".000000",clock.ToUnixTimeSeconds()+".000000","<@UBOT> public",["FPUBLIC"],clock);
            var fileJob=store.Accept(aged,clock.AddMinutes(-1),clock).JobId!;
            var attachment=store.SaveAttachment(fileJob,"FPUBLIC",".png",[137,80,78,71,13,10,26,10]);
            Check(store.GetAttachment(fileJob,"FPUBLIC")!.Hash==attachment.Hash,"restricted original retained");
            try{store.SaveAttachment(fileJob,"FOTHER",".png",[1]);throw new Exception("foreign attachment accepted");}catch(UnauthorizedAccessException){checks++;}
            while(store.ClaimReply(now) is {} reply)store.ReplySent(reply.Id,"1700000000.000001");
            var claimed=store.ClaimNext()!;store.Finish(claimed.Id,"PUBLIC OCR",true,true);while(store.ClaimReply(now) is {} reply)store.ReplySent(reply.Id,"1700000000.000002");
            var summaryTs=now.AddSeconds(2).ToUnixTimeSeconds()+".000000";var summaryMention=new SlackMention("EV-SUMMARY","TTEST","CTEST","UALICE",summaryTs,summaryTs,"<@UBOT> summary",[],now);
            var summaryId=store.Accept(summaryMention,since,now).JobId!;var acceptedReply=store.ClaimReply(now);Check(acceptedReply?.JobId==summaryId,"summary fixture acceptance reply");store.ReplySent(acceptedReply!.Id,"1700000000.000003");
            var summaryJob=store.ClaimNext()!;store.WaitForSummary(summaryJob.Id);Check(store.GetJob(summaryJob.Id).Phase=="waiting_summary","summary wait fixture");
            Check(store.FailWaitingSummary(summaryJob.SessionId)==1,"disabled compression fails waiting job");
            Check(store.GetJob(summaryJob.Id) is {Phase:"failed",Result:"LOCAL_COMPRESSION_UNAVAILABLE"},"compression failure is terminal");
            var failedReply=store.ClaimReply(now);Check(failedReply?.JobId==summaryJob.Id&&failedReply.Text.Contains("完了できませんでした",StringComparison.Ordinal),"compression failure notification");store.ReplySent(failedReply!.Id,"1700000000.000004");
            Check(store.Cleanup(now,true,()=>true)==1&&store.GetAttachment(fileJob,"FPUBLIC") is null,"session cleanup owns only its attachment rows");
            Check(store.GetJob(job.Id).Result=="PUBLIC","other session survives");
        }finally{Environment.SetEnvironmentVariable("LOCALBRAIN_OCR_CONFIG",old);}
        Console.WriteLine(JsonSerializer.Serialize(new{slack_protocol_priority_ocr_checks=checks,status="PASS",public_local_ack_ms=ackMilliseconds,public_local_preemption_ms=preemptionMilliseconds,real_ocr_model=false,real_slack=false,real_saluki=false}));
        await SlackRetryTests.RunAsync();
    }
}
