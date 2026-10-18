using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.Ocr;

// Actual OS controls with a public, pinned synthetic worker; this does not qualify OCR text accuracy.
internal static class OcrProcessControlTests {
    internal static async Task RunAsync(string assets,string fixtures){
        assets=Path.GetFullPath(assets);fixtures=Path.GetFullPath(fixtures);
        var root=Path.Combine(Path.GetTempPath(),"lbocr-process-controls-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var original=OcrJobStore.Read<JsonElement>(Path.Combine(assets,"runtime-manifest.json")).GetProperty("files").EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetProperty("sha256").GetString()!);
        var worker=Path.Combine(assets,"public-control-"+Guid.NewGuid().ToString("N")+".py");
        var records=new List<object>();
        try{
            foreach(var (name,body,expected) in new[]{
                ("timeout","import time; time.sleep(30)","PAGE_TIMEOUT"),
                ("output","import sys; sys.stdout.write('X'*1000000); sys.stdout.flush()","OUTPUT_LIMIT"),
                ("memory","import json\ntry:\n data=[bytearray(4*1024*1024) for _ in range(256)]\nexcept MemoryError:\n print(json.dumps({'error_code':'WORKER_MEMORY_LIMIT'})); raise SystemExit(2)","WORKER_MEMORY_LIMIT"),
                ("child","import subprocess,sys,json\ntry:\n p=subprocess.Popen([sys.executable,'-I','-B','-c','import time; time.sleep(30)']);p.wait();raise SystemExit(5)\nexcept OSError:\n print(json.dumps({'error_code':'CHILD_LIMIT_ENFORCED'}));raise SystemExit(2)","CHILD_LIMIT_ENFORCED")
            }){
                File.WriteAllText(worker,body);var hashes=new Dictionary<string,string>(original){[Path.GetFileName(worker)]=OcrJobStore.HashFile(worker)};
                var config=new OcrConfig(true,root,assets,Path.Combine(assets,"python","python.exe"),worker,Path.Combine(assets,"models"),[fixtures],hashes,["PRINTED_TEXT"],[],1024L<<20,1024L<<20,128L<<20,50,new string('a',64),PageTimeoutSeconds:5);
                var input=Path.Combine(fixtures,"printed-ja.png");var attempt=Path.Combine(root,name);Directory.CreateDirectory(attempt);
                var result=await OcrWorkerProcess.RunAsync(config,Guid.NewGuid().ToString("N"),input,OcrJobStore.HashFile(input),attempt,"page",0,"PRINTED_TEXT",()=>false,CancellationToken.None);
                if(result.ErrorCode!=expected)throw new Exception("Public control "+name+" expected "+expected+" actual "+result.ErrorCode+" root="+root);
                records.Add(new{name,result.State,result.ErrorCode,result.ElapsedMs,result.PeakWorkingSet,result.PeakPrivateCommit,network_capabilities=0});
            }
            Console.WriteLine(JsonSerializer.Serialize(new{status="PASS_ACTUAL_WINDOWS_OCR_PROCESS_CONTROLS",root,records,synthetic_worker_only=true,actual_ocr_quality_test=false,production_changed=false}));
        }finally{if(File.Exists(worker))File.Delete(worker);}
    }
}
