using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.Ocr;

internal static class OcrStoreTests {
    internal static void Run(){
        var root=Path.Combine(Path.GetTempPath(),"lbocr-store-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var input=Path.Combine(root,"public.png");File.WriteAllBytes(input,[1,2,3]);
        var c=new OcrConfig(true,Path.Combine(root,"state"),Path.Combine(root,"assets"),Path.Combine(root,"assets","python.exe"),Path.Combine(root,"assets","worker.py"),Path.Combine(root,"assets","models"),[root],new(){["a"]="a",["b"]="b",["c"]="c"},["PRINTED_TEXT"],["HANDWRITING"],512L<<20,512L<<20,384L<<20,30,new string('a',64));
        var store=new OcrJobStore(c);var hash=OcrJobStore.HashFile(input);int checks=0;
        void Assert(bool b){if(!b)throw new Exception("OCR durable store check "+checks);checks++;}
        void Reject(string code,Action action){try{action();throw new Exception("Expected "+code);}catch(OcrException e){Assert(e.Code==code);}}
        Reject("INPUT_HASH_MISMATCH",()=>store.Submit(input,new string('0',64),"PRINTED_TEXT","public_hash_retry"));
        var s=store.Submit(input,hash,"PRINTED_TEXT","public_hash_retry");Assert(s.Status=="queued");
        Assert(store.Submit(input,hash,"PRINTED_TEXT","public_hash_retry").JobId==s.JobId);
        Reject("REQUEST_ID_BINDING_MISMATCH",()=>store.Submit(input,hash,"PRINTED_TEXT","public_hash_retry",retainOriginal:true));
        Reject("PROFILE_UNQUALIFIED",()=>store.Submit(input,hash,"HANDWRITING","public_unqualified"));
        store.Transition(s.JobId,"queued",count:2);
        var m=store.GetManifest(s.JobId);
        var page=JsonSerializer.SerializeToElement(new{page=0,input_sha256=hash,external_ai="deny",text="PUBLIC",needs_review=false,spans=Array.Empty<object>()});
        var committed=store.CommitPage(m,0,page);Assert(committed.CompletedPages==1);
        // Simulate crash between page+commit-proof and checkpoint publication.
        OcrJobStore.Atomic(Path.Combine(store.DirectoryFor(s.JobId),"state.json"),committed with{CompletedPages=0,PageHashes=[]});
        store.RecoverPageCommits(m);Assert(store.GetState(s.JobId).PageHashes[0]==committed.PageHashes[0]);
        File.AppendAllText(Path.Combine(store.DirectoryFor(s.JobId),"pages","0.json")," ");
        Reject("PAGE_EVIDENCE_CHANGED",()=>store.VerifyPages(m,store.GetState(s.JobId)));
        Assert(OcrResources.Refusal(c,new(long.MaxValue,long.MaxValue,0),true)=="PRIORITY_WORK_ACTIVE");
        Assert(OcrResources.Refusal(c,new(0,long.MaxValue,0),false)=="RAM_ADMISSION");
        Assert(OcrResources.Refusal(c,new(long.MaxValue,0,0),false)=="COMMIT_ADMISSION");
        Assert(OcrResources.Refusal(c,new(long.MaxValue,long.MaxValue,90),false)=="CPU_ADMISSION");
        Assert(OcrWorkerProcess.Pressure(c,new(0,long.MaxValue,0))=="RAM_PRESSURE");
        Assert(OcrWorkerProcess.Pressure(c,new(long.MaxValue,0,0))=="COMMIT_PRESSURE");
        Assert(File.Exists(input));
        using(var held=store.Lock()){
            var cancel=Task.Run(()=>store.Cancel(s.JobId));Thread.Sleep(80);Assert(!cancel.IsCompleted);Assert(store.GetState(s.JobId).JobId==s.JobId);
            held.Dispose();Assert(cancel.GetAwaiter().GetResult().Status=="cancelling");
        }
        var complete=store.Submit(input,hash,"PRINTED_TEXT","public_result_recovery");var cm=store.GetManifest(complete.JobId);store.Transition(complete.JobId,"queued",count:1);store.CommitPage(cm,0,page);
        var resultPath=Path.Combine(store.DirectoryFor(complete.JobId),"result.json");File.WriteAllText(resultPath,"UNKNOWN");
        Reject("RESULT_EVIDENCE_CHANGED",()=>store.Finish(cm));Assert(File.ReadAllText(resultPath)=="UNKNOWN");File.Delete(resultPath); // remove only our injected public fixture
        using(var heldInput=new FileStream(store.Input(cm),FileMode.Open,FileAccess.Read,FileShare.Read)){
            Assert(store.Finish(cm).Status=="complete");Assert(File.Exists(store.Input(cm)));Assert(store.Result(cm.JobId).GetProperty("text").GetString()=="PUBLIC");
        }
        Assert(store.TryCleanupInput(cm,force:true));Assert(!File.Exists(store.Input(cm))&&File.Exists(input));
        var manifestPath=Path.Combine(store.DirectoryFor(cm.JobId),"manifest.json");
        OcrJobStore.Atomic(manifestPath,cm with{CreatedAt=DateTimeOffset.UtcNow.AddHours(-25)});OcrJobStore.Atomic(manifestPath+".binding",new{sha256=OcrJobStore.HashFile(manifestPath)});
        Assert(store.Expire(cm.JobId));Assert(store.GetState(cm.JobId).Status=="expired"&&File.Exists(input));
        File.AppendAllText(manifestPath," ");Reject("MANIFEST_EVIDENCE_CHANGED",()=>store.GetManifest(cm.JobId));
        Console.WriteLine(JsonSerializer.Serialize(new{status="PASS_OCR_DURABLE_STORE",checks,public_only=true,root}));
    }
}
