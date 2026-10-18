using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.AgentV2;
using LocalBrain.ClientHost.Ocr;

internal static class OcrPerformanceTests {
    internal static async Task RunAsync(string assets,string fixtures,string controlRoot,string clientConfig){
        assets=Path.GetFullPath(assets);fixtures=Path.GetFullPath(fixtures);
        var root=Path.Combine(Path.GetTempPath(),"lbocr-performance-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var manifest=OcrJobStore.Read<JsonElement>(Path.Combine(assets,"runtime-manifest.json"));
        var files=manifest.GetProperty("files").EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetProperty("sha256").GetString()!);
        var config=new OcrConfig(true,root,assets,Path.Combine(assets,"python","python.exe"),Path.Combine(assets,"local_ocr_worker.py"),Path.Combine(assets,"models"),[fixtures],files,["PDF_DOCUMENT"],[],1024L<<20,1024L<<20,768L<<20,50,new string('a',64));
        var configPath=Path.Combine(root,"config.json");OcrJobStore.Atomic(configPath,config);
        var oldOcr=Environment.GetEnvironmentVariable("LOCALBRAIN_OCR_CONFIG");var oldClient=Environment.GetEnvironmentVariable("LOCALBRAIN_CLIENT_CONFIG");
        Environment.SetEnvironmentVariable("LOCALBRAIN_CLIENT_CONFIG",clientConfig);
        using var gateway=new GatewayClient(ClientConfig.Load());using var http=new HttpClient(){Timeout=TimeSpan.FromSeconds(10)};
        using var stop=new CancellationTokenSource(TimeSpan.FromMinutes(20));Task scheduler=Task.CompletedTask;
        await using var memory=new TaskMemoryQ8(controlRoot);
        await using var laya=new LayaSupervisor();
        async Task<double> Laya(){var sw=Stopwatch.StartNew();await laya.PredictAsync(new{phase="coding",tool_calls=2,files_read=1,files_changed=1,tests_run=0,tests_passed=false,same_error_count=0,same_command_count=0,replans=0,context_usage=.1,progress="clear",last_action_changed_state=true},stop.Token);return sw.Elapsed.TotalMilliseconds;}
        async Task<double> BridgePing(){var sw=Stopwatch.StartNew();using var reply=await http.GetAsync("http://127.0.0.1:18081/localbrain/supervision/status",stop.Token);reply.EnsureSuccessStatusCode();return sw.Elapsed.TotalMilliseconds;}
        async Task<double> Memory(){var sw=Stopwatch.StartNew();var result=await memory.PredictAsync("Public OCR interference fixture. Return only event_public_ocr.",["event_public_ocr"],token:stop.Token);if(!TaskMemorySelection.Validate("public",[new("public","event_public_ocr",1,"public","unverified",[],"public")],result.Content).EventIds.Contains("event_public_ocr"))throw new Exception("Native memory did not select public event");return sw.Elapsed.TotalMilliseconds;}
        async Task<double> Qwen(){
            using var lease=OcrPriority.Enter("bridge");var sw=Stopwatch.StartNew();
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new{model="local-qwen38",messages=new[]{new{role="user",content="Public OCR performance fixture. Reply exactly OK. /no_think"}},max_tokens=16,temperature=0,stream=false});
            using var body=new MemoryStream(bytes);using var reply=await gateway.SendAsync(HttpMethod.Post,"/v1/chat/completions",body,"application/json",bytes.Length,Guid.NewGuid().ToString(),stop.Token);reply.EnsureSuccessStatusCode();var data=await reply.Content.ReadAsByteArrayAsync(stop.Token);if(data.Length>65536)throw new Exception("Bounded public reply exceeded");return sw.Elapsed.TotalMilliseconds;
        }
        async Task<List<double>> Repeated(Func<Task<double>> action,int n=3){List<double> values=[];for(var i=0;i<n;i++)values.Add(await action());return values;}
        var build=Path.Combine(root,"public-build");Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(build,"Public.csproj"),"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");File.WriteAllText(Path.Combine(build,"Public.cs"),"public static class PublicFixture { public static int Value => 42; }");File.WriteAllText(Path.Combine(build,"NuGet.Config"),"<configuration><packageSources><clear /></packageSources></configuration>");
        async Task<double> Build(bool restore=false){
            using var lease=OcrPriority.Enter("build_test");var sw=Stopwatch.StartNew();var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=build};
            foreach(var arg in new[]{"build","Public.csproj","-v:q"})start.ArgumentList.Add(arg);if(!restore)start.ArgumentList.Add("--no-restore");start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"]="1";
            using var child=Process.Start(start)!;var output=child.StandardOutput.ReadToEndAsync();var error=child.StandardError.ReadToEndAsync();await child.WaitForExitAsync(stop.Token);await Task.WhenAll(output,error);if(child.ExitCode!=0)throw new Exception("Public build failed");return sw.Elapsed.TotalMilliseconds;
        }
        try{
            // Observe before any normal-path model request; do not override a busy gateway or existing backend owner.
            using(var response=await gateway.SendAsync(HttpMethod.Get,"/v1/control/status",null,null,null,Guid.NewGuid().ToString(),stop.Token)){
                response.EnsureSuccessStatusCode();using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(stop.Token));
                if(doc.RootElement.GetProperty("active_jobs").GetInt32()!=0||doc.RootElement.GetProperty("queued_jobs").GetInt32()!=0||doc.RootElement.GetProperty("queue_depth").GetInt32()!=0||!doc.RootElement.GetProperty("resources").GetProperty("accepting_jobs").GetBoolean())throw new Exception("Gateway busy: defer, do not alter settings or start a model");
                OcrJobStore.Atomic(Path.Combine(root,"public-control-before.json"),doc.RootElement);
            }
            var cold=Stopwatch.StartNew();var props=await memory.StartAsync(stop.Token);await Memory();cold.Stop();await Qwen();await Laya();await Build(true);
            OcrJobStore.Atomic(Path.Combine(root,"run-identity.json"),new{host_dll_sha256=OcrJobStore.HashFile(typeof(OcrConfig).Assembly.Location),test_dll_sha256=OcrJobStore.HashFile(typeof(OcrPerformanceTests).Assembly.Location),worker_sha256=OcrJobStore.HashFile(config.Worker),model_config_sha256=OcrJobStore.HashFile(Path.Combine(controlRoot,"taskmemory-q8","config.json")),actual_memory_context=props.GetProperty("default_generation_settings").GetProperty("n_ctx").GetInt32(),private_data_used=false,external_cloud_calls=0});
            var baseline=new{cold_memory_start_and_search_ms=cold.Elapsed.TotalMilliseconds,host_ms=await Repeated(BridgePing,10),memory_ms=await Repeated(Memory),qwen_ms=await Repeated(Qwen),laya_ms=await Repeated(Laya),build_ms=await Repeated(()=>Build())};
            OcrJobStore.Atomic(Path.Combine(root,"baseline-measurements.json"),baseline);
            Environment.SetEnvironmentVariable("LOCALBRAIN_OCR_CONFIG",configPath);scheduler=Task.Run(()=>new OcrScheduler(config).RunAsync(stop.Token));
            var idle=new{host_ms=await Repeated(BridgePing,10),memory_ms=await Repeated(Memory),qwen_ms=await Repeated(Qwen),laya_ms=await Repeated(Laya),build_ms=await Repeated(()=>Build())};
            OcrJobStore.Atomic(Path.Combine(root,"idle-measurements.json"),idle);
            var store=new OcrJobStore(config);var input=Path.Combine(fixtures,"scan-two-pages.pdf");
            var resident=store.Submit(input,OcrJobStore.HashFile(input),"PDF_DOCUMENT","public_perf_resident_admission");
            await Task.Delay(1500,stop.Token);var residentState=store.GetState(resident.JobId);
            OcrJobStore.Atomic(Path.Combine(root,"resident-memory-admission.json"),new{residentState.Status,residentState.ErrorCode,description="Admission while the owned131072 TaskMemory backend is resident; no thresholds reduced"});
            store.Cancel(resident.JobId);
            // Free only this fixture's own backend. Test preemption when the normal backend starts, not unsafe co-residence.
            await memory.DisposeAsync();
            async Task<(string JobId,JsonElement Process)> Active(string request){
                var job=store.Submit(input,OcrJobStore.HashFile(input),"PDF_DOCUMENT",request);var sw=Stopwatch.StartNew();
                while(sw.Elapsed<TimeSpan.FromSeconds(90)){
                    var state=store.GetState(job.JobId);if(state.Status=="failed")throw new Exception(state.ErrorCode);
                    foreach(var path in Directory.EnumerateFiles(Path.Combine(store.DirectoryFor(job.JobId),"attempts"),"process.json",SearchOption.AllDirectories)){
                        var p=OcrJobStore.Read<JsonElement>(path);if(!TaskAgentCoordinator.IsAlive(p.GetProperty("pid").GetInt32(),p.GetProperty("birth").GetInt64()))continue;
                        try{using var process=Process.GetProcessById(p.GetProperty("pid").GetInt32());if(process.PrivateMemorySize64>150L<<20)return(job.JobId,p);}catch(ArgumentException){}
                    }await Task.Delay(10);
                }throw new Exception("No active public OCR page root="+root);
            }
            async Task<double> ExitLatency(JsonElement p){var sw=Stopwatch.StartNew();while(TaskAgentCoordinator.IsAlive(p.GetProperty("pid").GetInt32(),p.GetProperty("birth").GetInt64())){if(sw.Elapsed>TimeSpan.FromSeconds(10))throw new Exception("OCR priority stop timeout");await Task.Delay(5);}return sw.Elapsed.TotalMilliseconds;}
            var memoryJob=await Active("public_perf_memory");var stopMemory=ExitLatency(memoryJob.Process);var search=await Memory();var memoryStop=await stopMemory;OcrJobStore.Atomic(Path.Combine(root,"memory-preemption.json"),new{search,memoryStop});store.Cancel(memoryJob.JobId);await memory.DisposeAsync();
            var buildJob=await Active("public_perf_build");var stopBuild=ExitLatency(buildJob.Process);var compilation=await Build();var buildStop=await stopBuild;OcrJobStore.Atomic(Path.Combine(root,"build-preemption.json"),new{compilation,buildStop});store.Cancel(buildJob.JobId);
            var qwenJob=await Active("public_perf_qwen");var stopQwen=ExitLatency(qwenJob.Process);var generation=await Qwen();var qwenStop=await stopQwen;OcrJobStore.Atomic(Path.Combine(root,"qwen-preemption.json"),new{generation,qwenStop});store.Cancel(qwenJob.JobId);
            var layaJob=await Active("public_perf_laya");var stopLaya=ExitLatency(layaJob.Process);var supervision=await Laya();var layaStop=await stopLaya;store.Cancel(layaJob.JobId);
            var result=new{status="MEASURED_PUBLIC_NATIVE_INTERFERENCE",root,baseline,ocr_idle=idle,active_preemption=new{memory_ms=search,memory_worker_exit_ms=memoryStop,build_ms=compilation,build_worker_exit_ms=buildStop,qwen_ms=generation,qwen_worker_exit_ms=qwenStop,laya_ms=supervision,laya_worker_exit_ms=layaStop},configured_context=memory.Diagnostics,production_config_changed=false,private_data_used=false,external_cloud_calls=0,limits="Small public sample; no general speed guarantee. OCR cannot run concurrently with normal task activity leases."};
            OcrJobStore.Atomic(Path.Combine(root,"performance.json"),result);Console.WriteLine(JsonSerializer.Serialize(result));
        }finally{stop.Cancel();await scheduler;Environment.SetEnvironmentVariable("LOCALBRAIN_OCR_CONFIG",oldOcr);Environment.SetEnvironmentVariable("LOCALBRAIN_CLIENT_CONFIG",oldClient);}
    }
}
