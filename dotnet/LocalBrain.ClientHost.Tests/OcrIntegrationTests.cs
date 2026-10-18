using System.Diagnostics;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.AgentV2;
using LocalBrain.ClientHost.Ocr;

internal static class OcrIntegrationTests
{
    internal static async Task PublicAsync(string assets,string fixtures){
        assets=Path.GetFullPath(assets);fixtures=Path.GetFullPath(fixtures);
        var root=Path.Combine(Path.GetTempPath(),"localbrain-ocr-integration-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var files=OcrJobStore.Read<JsonElement>(Path.Combine(assets,"runtime-manifest.json")).GetProperty("files").EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetProperty("sha256").GetString()!);
        var config=new OcrConfig(true,root,assets,Path.Combine(assets,"python","python.exe"),Path.Combine(assets,"local_ocr_worker.py"),Path.Combine(assets,"models"),[fixtures],files,["PRINTED_TEXT","PDF_DOCUMENT","TABLE_LAYOUT","AUTO"],["HANDWRITING"],1024L*1024*1024,1024L*1024*1024,768L*1024*1024,50,new string('a',64));
        var path=Path.Combine(root,"config.json");OcrJobStore.Atomic(path,config);
        var old=Environment.GetEnvironmentVariable("LOCALBRAIN_OCR_CONFIG");Environment.SetEnvironmentVariable("LOCALBRAIN_OCR_CONFIG",path);
        using var stop=new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var store=new OcrJobStore(config);var scheduler=new OcrScheduler(config);
        var input=Path.Combine(fixtures,"printed-ja.png");var hash=OcrJobStore.HashFile(input);
        var first=store.Submit(input,hash,"PRINTED_TEXT","public_printed_0001");
        var duplicate=store.Submit(input,hash,"PRINTED_TEXT","public_printed_0001");
        Check(first.JobId==duplicate.JobId,"submit retry exact same job");
        var running=Task.Run(()=>scheduler.RunAsync(stop.Token));
        async Task<OcrJobStore.State> Until(string id,Func<OcrJobStore.State,bool> predicate,int seconds=45){
            var timer=Stopwatch.StartNew();
            while(timer.Elapsed<TimeSpan.FromSeconds(seconds)){
                var s=store.GetState(id);if(s.Status=="failed")throw new Exception("Public OCR failed "+s.ErrorCode+" root="+root);
                if(predicate(s))return s;await Task.Delay(50);
            }throw new Exception("Public OCR state timeout root="+root);
        }
        try{
            await Until(first.JobId,s=>s.Status=="complete");var result=store.Result(first.JobId);
            Check(result.GetProperty("text").GetString()!.Contains("OCR公開テスト"),"real Japanese public inference");
            Check(!File.Exists(store.Input(store.GetManifest(first.JobId))),"default owned snapshot removed after completion; user source retained");
            var pages=result.GetProperty("pages");Check(pages.GetArrayLength()==1&&pages[0].GetProperty("spans").GetArrayLength()>0,"actual polygons and spans available");
            var pdf=Path.Combine(fixtures,"scan-two-pages.pdf");var document=store.Submit(pdf,OcrJobStore.HashFile(pdf),"PDF_DOCUMENT","public_pdf_0001");
            await Until(document.JobId,s=>s.PageCount==2&&s.CompletedPages>=1&&s.Status=="running");
            var before=store.GetState(document.JobId);var preempt=Stopwatch.StartNew();
            using(OcrPriority.Enter("build_test")){
                await Until(document.JobId,s=>s.Status=="paused");preempt.Stop();
                var paused=store.GetState(document.JobId);Check(paused.CompletedPages>=1,"completed page checkpoint retained on priority pause");
                foreach(var item in before.PageHashes)Check(paused.PageHashes[item.Key]==item.Value,"already committed page is immutable");
                await Task.Delay(200);Check(store.GetState(document.JobId).Status=="paused","priority work keeps OCR stopped");
            }
            await Until(document.JobId,s=>s.Status=="complete");
            var final=store.GetState(document.JobId);Check(final.CompletedPages==2,"automatic page resume finishes same job");
            var cancel=store.Submit(pdf,OcrJobStore.HashFile(pdf),"PDF_DOCUMENT","public_cancel_0001");
            await Until(cancel.JobId,s=>s.Status=="running");store.Cancel(cancel.JobId);await Until(cancel.JobId,s=>s.Status=="cancelled");
            Check(!File.Exists(store.Input(store.GetManifest(cancel.JobId))),"cancel releases owned input by default");
            var events=File.ReadAllText(Path.Combine(root,"events.jsonl"));Check(!events.Contains("試験")&&!events.Contains("printed-ja")&&!events.Contains("OCR公開"),"normal metadata events contain no source filenames or OCR body");
            Console.WriteLine(JsonSerializer.Serialize(new{status="PASS_REAL_OCR_HOST_PAGE_PAUSE_RESUME_CANCEL",root,first_job=first.JobId,pdf_job=document.JobId,cancel_job=cancel.JobId,priority_pause_ms=preempt.ElapsedMilliseconds,final_completed_pages=final.CompletedPages,network_capabilities=0,model_calls=0,private_data_used=false,source_original_retained=File.Exists(input),production_config_changed=false}));
        }finally{stop.Cancel();await running;Environment.SetEnvironmentVariable("LOCALBRAIN_OCR_CONFIG",old);}
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
}
