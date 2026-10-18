using System.Diagnostics;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.AgentV2;
using LocalBrain.ClientHost.Ocr;

internal static class OcrRecoveryTests {
    internal static async Task RunAsync(string assets,string fixtures){
        assets=Path.GetFullPath(assets);fixtures=Path.GetFullPath(fixtures);
        var root=Path.Combine(Path.GetTempPath(),"lbocr-crash-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var files=OcrJobStore.Read<JsonElement>(Path.Combine(assets,"runtime-manifest.json")).GetProperty("files").EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetProperty("sha256").GetString()!);
        var c=new OcrConfig(true,root,assets,Path.Combine(assets,"python","python.exe"),Path.Combine(assets,"local_ocr_worker.py"),Path.Combine(assets,"models"),[fixtures],files,["PDF_DOCUMENT"],[],1024L<<20,1024L<<20,768L<<20,50,new string('a',64));
        var configPath=Path.Combine(root,"config.json");OcrJobStore.Atomic(configPath,c);var store=new OcrJobStore(c);
        var input=Path.Combine(fixtures,"scan-two-pages.pdf");var job=store.Submit(input,OcrJobStore.HashFile(input),"PDF_DOCUMENT","public_crash_recovery");
        Process StartHost(){var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true};foreach(var arg in new[]{typeof(OcrRecoveryTests).Assembly.Location,"--ocr-host-fixture",configPath})start.ArgumentList.Add(arg);return Process.Start(start)!;}
        async Task Wait(Func<bool> condition){var timer=Stopwatch.StartNew();while(timer.Elapsed<TimeSpan.FromSeconds(90)){if(condition())return;var state=store.GetState(job.JobId);if(state.Status=="failed")throw new Exception("Crash fixture failed "+state.ErrorCode+" root="+root);await Task.Delay(30);}throw new Exception("Crash fixture timeout root="+root);}
        JsonElement? active=null;var first=StartHost();Process? second=null;
        try{
            await Wait(()=>{
                var s=store.GetState(job.JobId);if(s.CompletedPages!=1||s.Status!="running")return false;
                foreach(var p in Directory.EnumerateFiles(Path.Combine(store.DirectoryFor(job.JobId),"attempts"),"process.json",SearchOption.AllDirectories)){
                    var value=OcrJobStore.Read<JsonElement>(p);if(TaskAgentCoordinator.IsAlive(value.GetProperty("pid").GetInt32(),value.GetProperty("birth").GetInt64())){active=value;return true;}
                }return false;
            });
            var checkpoint=store.GetState(job.JobId).PageHashes[0];var timer=Stopwatch.StartNew();
            first.Kill();await first.WaitForExitAsync();
            await Wait(()=>!TaskAgentCoordinator.IsAlive(active!.Value.GetProperty("pid").GetInt32(),active.Value.GetProperty("birth").GetInt64()));timer.Stop();
            second=StartHost();await Wait(()=>store.GetState(job.JobId).Status=="complete");
            var final=store.GetState(job.JobId);if(final.CompletedPages!=2||final.PageHashes[0]!=checkpoint)throw new Exception("Committed page replayed");
            var attempts=Directory.EnumerateDirectories(Path.Combine(store.DirectoryFor(job.JobId),"attempts")).ToArray();
            var recovered=attempts.Select(p=>Path.Combine(p,"sandbox-cleaned.json")).Where(File.Exists).Select(p=>OcrJobStore.Read<JsonElement>(p)).Any(e=>e.TryGetProperty("recovery",out var v)&&v.GetBoolean());
            if(!recovered)throw new Exception("Owned profile recovery not evidenced");
            Console.WriteLine(JsonSerializer.Serialize(new{status="PASS_HOST_CRASH_RECOVERY",root,job_id=job.JobId,completed_pages=final.CompletedPages,committed_page_unchanged=true,owned_worker_exit_after_host_kill_ms=timer.ElapsedMilliseconds,owned_profile_recovered=true,actual_pc_reboot_tested=false,production_changed=false}));
        }finally{
            foreach(var p in new[]{first,second})if(p is not null){if(!p.HasExited){p.Kill();await p.WaitForExitAsync();}p.Dispose();}
        }
    }
}
