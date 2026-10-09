using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalBrain.ClientHost.AgentV2;

// Optional Host-owned CPU backend. No URL, command or tool is accepted from a model/repository.
internal sealed class TaskMemoryQ8 : IAsyncDisposable
{
    internal sealed record InputFile(string Path,long Bytes,string Sha256);
    internal sealed record Options(bool Enabled,string InputDirectory,string ModelFile,string ModelSha256,InputFile[] Files,int ContextLimit=65536);
    internal sealed record Prediction(string Content,int PromptTokens,JsonElement RuntimeResult);
    private readonly string configPath;
    private readonly List<FileStream> inputLocks=[];
    private readonly ConcurrentQueue<string> metrics=new();
    private Process? process;private WindowsChildJob? job;private HttpClient? client;
    private FileStream? backendLease;private string? actualListener;
    private Task? stdout,stderr;private Options? options;private int measuredPromptTokens;
    internal const int ContextLimit=65536,OutputReserve=512,SafetyReserve=2048;
    public TaskMemoryQ8(string controlRoot) {configPath=Path.Combine(controlRoot,"taskmemory-q8","config.json");}
    public bool Configured => File.Exists(configPath);
    public bool Enabled {
        get {
            if(!Configured)return false;
            CanonicalStore.GuardPath(configPath);
            if(new FileInfo(configPath).Length>65536)throw new InvalidDataException("Oversized Host memory config");
            return JsonSerializer.Deserialize<Options>(File.ReadAllText(configPath),ClientConfig.JsonOptions)?.Enabled==true;
        }
    }
    public static void ValidateOptions(Options value) {
        if(value.ContextLimit is not (65536 or 98304 or 131072))throw new InvalidDataException("Host memory context must be64K or a qualified96K/128K candidate");
        if(!Path.IsPathFullyQualified(value.InputDirectory))throw new InvalidDataException("Memory inputs must have an absolute Host-owned path");
        if(value.ModelSha256.ToUpperInvariant()!="FDFCB6A29B11188956DFBFD904223588A6C1B77EB250C3E8A36E1BD269DF91F7" ||
            value.ModelFile!="Qwen3.8-27B-GSQ-RCO-IQ3_XXS.gguf" || value.Files.Length is <3 or >64)
            throw new InvalidDataException("TaskMemory model/input identity differs from verified ServerPC inputs");
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in value.Files) {
            if(!paths.Add(file.Path) || file.Path.Contains("..") || file.Path.Contains('\\') ||
                (file.Path!=value.ModelFile && file.Path!="bin/llama-server.exe" &&
                 !Regex.IsMatch(file.Path,@"^bin/(?:[A-Za-z0-9_.-]+/)*[A-Za-z0-9_.-]+\.dll$")) ||
                file.Bytes<=0 || !Regex.IsMatch(file.Sha256,@"\A[a-fA-F0-9]{64}\z"))
                throw new InvalidDataException("TaskMemory input manifest contains an invalid path/hash");
            if(file.Path.Split('/').Any(p=>p.EndsWith('.') || p is "." or ".."))throw new InvalidDataException("Ambiguous input path");
        }
        if(!paths.Contains(value.ModelFile) || !paths.Contains("bin/llama-server.exe") || !paths.Contains("bin/llama.dll"))
            throw new InvalidDataException("Required native memory input missing");
        if(!value.Files.Single(f=>f.Path==value.ModelFile).Sha256.Equals(value.ModelSha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model manifest identity mismatch");
    }
    private async Task ReadMetricsAsync(StreamReader reader) {
        while(await reader.ReadLineAsync() is {} line) {
            if(line.Length<=4096 && Regex.IsMatch(line,@"q8_0|n_ctx|CPU.*buffer|type_[kv]|offload",RegexOptions.IgnoreCase)) {
                metrics.Enqueue(line);while(metrics.Count>64)metrics.TryDequeue(out _);
            }
        }
    }
    public async Task<JsonElement> StartAsync(CancellationToken token=default) {
        if(process is not null && !process.HasExited)return await GetAsync("props",token);
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("TaskMemory owner must be Windows");
        CanonicalStore.GuardPath(configPath);
        if(new FileInfo(configPath).Length>65536)throw new InvalidDataException("Oversized Host memory config");
        options=JsonSerializer.Deserialize<Options>(File.ReadAllText(configPath),ClientConfig.JsonOptions)??throw new InvalidDataException("Invalid Host memory config");
        if(!options.Enabled)throw new InvalidOperationException("Host native memory backend is disabled");
        ValidateOptions(options);
        CanonicalStore.GuardPath(options.InputDirectory);
        try {
            var leasePath=Path.Combine(options.InputDirectory,"host-backend.lock");CanonicalStore.GuardPath(leasePath);
            backendLease=new FileStream(leasePath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            var actualFiles=Directory.GetFiles(options.InputDirectory,"*",SearchOption.AllDirectories)
                .Select(p=>Path.GetRelativePath(options.InputDirectory,p).Replace('\\','/'))
                .Where(p=>p==options.ModelFile || p.StartsWith("bin/",StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if(!actualFiles.SetEquals(options.Files.Select(f=>f.Path)))throw new InvalidDataException("Native payload changed outside verified manifest");
            foreach(var file in options.Files) {
                var path=Path.GetFullPath(Path.Combine(options.InputDirectory,file.Path));
                if(!CanonicalStore.Within(options.InputDirectory,path))throw new UnauthorizedAccessException();
                CanonicalStore.GuardPath(path);
                var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);inputLocks.Add(stream);
                if(stream.Length!=file.Bytes || !Convert.ToHexString(await SHA256.HashDataAsync(stream,token)).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Native memory input integrity failed");
            }
            var reservation=new TcpListener(IPAddress.Loopback,0);reservation.Start();
            var port=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
            var secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var start=new ProcessStartInfo(Path.Combine(options.InputDirectory,"bin","llama-server.exe")) {
                WorkingDirectory=Path.Combine(options.InputDirectory,"bin"),UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardOutput=true,RedirectStandardError=true,
                StandardOutputEncoding=new UTF8Encoding(false,true),StandardErrorEncoding=new UTF8Encoding(false,true)
            };
            var allowedEnvironment=new HashSet<string>(new[]{"PATH","SYSTEMROOT","WINDIR","TEMP","TMP","NUMBER_OF_PROCESSORS","PROCESSOR_ARCHITECTURE","COMSPEC"},StringComparer.OrdinalIgnoreCase);
            foreach(var key in start.Environment.Keys.Where(k=>!allowedEnvironment.Contains(k)).ToArray())start.Environment.Remove(key);
            start.Environment["LLAMA_API_KEY"]=secret;
            foreach(var arg in new[]{"--model",Path.Combine(options.InputDirectory,options.ModelFile),"--host",IPAddress.Loopback.ToString(),"--port",port.ToString(),
                "--ctx-size",options.ContextLimit.ToString(),"--parallel","1","--n-gpu-layers","0","--threads","6",
                "--cache-type-k","q8_0","--cache-type-v","q8_0","--flash-attn","on","--no-webui",
                "--device","none","--no-op-offload","--fit","off","--offline","--log-verbosity","4","--log-colors","off"})start.ArgumentList.Add(arg);
            job=WindowsChildJob.Start(start);process=job.Process;
            stdout=ReadMetricsAsync(job.Output);stderr=ReadMetricsAsync(job.Error);
            client=new HttpClient(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false}){BaseAddress=new Uri($"http://{IPAddress.Loopback}:{port}/"),Timeout=Timeout.InfiniteTimeSpan};
            client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",secret);
            using var startup=CancellationTokenSource.CreateLinkedTokenSource(token);startup.CancelAfter(TimeSpan.FromMinutes(4));
            while(true) {
                startup.Token.ThrowIfCancellationRequested();
                if(process.HasExited)throw new IOException("Native CPU memory server exited at startup; "+string.Join(" | ",metrics));
                try {
                    var props=await GetAsync("props",startup.Token);
                    if(props.GetProperty("default_generation_settings").GetProperty("n_ctx").GetInt32()!=options.ContextLimit ||
                        props.GetProperty("total_slots").GetInt32()!=1)throw new InvalidDataException("Native memory runtime context differs from Host configuration");
                    using var probe=new HttpRequestMessage(HttpMethod.Get,"props");
                    probe.Headers.Authorization=new AuthenticationHeaderValue("Bearer","invalid-host-probe");
                    using var rejected=await client.SendAsync(probe,HttpCompletionOption.ResponseHeadersRead,startup.Token);
                    if(rejected.StatusCode!=HttpStatusCode.Unauthorized)throw new InvalidDataException("Native memory endpoint did not enforce the Host ephemeral credential");
                    var listeners=IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(p=>p.Port==port).ToArray();
                    if(listeners.Length!=1 || !listeners[0].Address.Equals(IPAddress.Loopback))throw new InvalidDataException("Native backend listener is not exclusively IPv4 loopback");
                    actualListener=listeners[0].ToString();
                    return props;
                }catch(HttpRequestException) {await Task.Delay(500,startup.Token);}
            }
        } catch {await DisposeAsync();throw;}
    }
    private async Task<JsonElement> GetAsync(string endpoint,CancellationToken token) {
        using var request=new HttpRequestMessage(HttpMethod.Get,endpoint);
        return await SendJsonAsync(client!,request,token);
    }
    internal static async Task<JsonElement> SendJsonAsync(HttpClient client,HttpRequestMessage request,CancellationToken token,TimeSpan? timeout=null) {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        var duration=timeout??TimeSpan.FromSeconds(180);
        if(client.Timeout!=Timeout.InfiniteTimeSpan && client.Timeout<duration)duration=client.Timeout;
        deadline.CancelAfter(duration);
        using var result=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
        result.EnsureSuccessStatusCode();return await ReadJsonAsync(result,deadline.Token);
    }
    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage result,CancellationToken token) {
        using var stream=await result.Content.ReadAsStreamAsync(token);
        using var buffer=new MemoryStream();var chunk=new byte[8192];int count;
        while((count=await stream.ReadAsync(chunk,token))>0) {
            if(buffer.Length+count>6*1024*1024)throw new InvalidDataException("Native memory response exceeds Host bound");
            buffer.Write(chunk,0,count);
        }
        using var doc=JsonDocument.Parse(buffer.ToArray());return doc.RootElement.Clone();
    }
    private async Task<JsonElement> PostAsync(string endpoint,object payload,CancellationToken token,TimeSpan? timeout=null) {
        if(process is null || process.HasExited)throw new IOException("Owned native memory process unavailable");
        var bytes=JsonSerializer.SerializeToUtf8Bytes(payload);
        if(bytes.Length>6*1024*1024)throw new InvalidDataException("Native memory request exceeds Host bound");
        using var body=new ByteArrayContent(bytes);body.Headers.ContentType=new MediaTypeHeaderValue("application/json");
        using var request=new HttpRequestMessage(HttpMethod.Post,endpoint){Content=body};
        return await SendJsonAsync(client!,request,token,timeout);
    }
    internal static TimeSpan CompletionTimeout(int promptTokens) => TimeSpan.FromSeconds(Math.Clamp(200+promptTokens/15.0,180,900));
    public async Task<Prediction> PredictAsync(string instruction,string[] eventIds,Action<object,int>? capture=null,CancellationToken token=default) {
        await StartAsync(token);
        var template=await PostAsync("apply-template",new {messages=new[]{new {role="user",content=instruction}}},token);
        var prompt=template.GetProperty("prompt").GetString()??throw new InvalidDataException("Missing rendered model prompt");
        var tokenizer=await PostAsync("tokenize",new {content=prompt,add_special=false,parse_special=true},token);
        var tokens=tokenizer.GetProperty("tokens").EnumerateArray().Select(t=>t.GetInt32()).ToArray();
        measuredPromptTokens=tokens.Length;
        if(tokens.Length+OutputReserve+SafetyReserve>options!.ContextLimit)throw new InvalidDataException("TaskMemory prompt exceeds Host configured token budget");
        var payload=new {prompt=tokens,n_predict=OutputReserve,temperature=0.0,stream=false,cache_prompt=true,
            json_schema=new {type="object",properties=new {event_ids=new {type="array",items=new {type="string",@enum=eventIds},maxItems=8}},required=new[]{"event_ids"},additionalProperties=false}};
        var completionTimeout=CompletionTimeout(tokens.Length);
        capture?.Invoke(new {rendered_prompt=prompt,final_payload=payload,model_sha256=options!.ModelSha256,completion_deadline_seconds=completionTimeout.TotalSeconds},tokens.Length);
        var response=await PostAsync("completion",payload,token,completionTimeout);
        if(response.TryGetProperty("truncated",out var truncated) && truncated.GetBoolean())throw new InvalidDataException("Native memory silently truncated prompt");
        return new(response.GetProperty("content").GetString()??"",tokens.Length,response);
    }
    public object Diagnostics=>new {measured_prompt_tokens=measuredPromptTokens,requested_context_limit=options?.ContextLimit??ContextLimit,requested_cpu_only=true,requested_cache_k="q8_0",requested_cache_v="q8_0",actual_listener=actualListener,model_sha256=options?.ModelSha256,runtime_metric_lines=metrics.ToArray(),process_id=process?.Id};
    public async ValueTask DisposeAsync() {
        try {
        client?.Dispose();client=null;
        job?.Terminate();
        if(process is not null) {
            if(!process.HasExited)process.Kill(entireProcessTree:true);
            await process.WaitForExitAsync();
            try {await Task.WhenAll(stdout??Task.CompletedTask,stderr??Task.CompletedTask);}
            catch(Exception ex) when(ex is IOException or ObjectDisposedException or DecoderFallbackException) { /* Optional output cannot invalidate the canonical task. */ }
            finally{job?.Dispose();job=null;process=null;}
        }
        } finally {foreach(var file in inputLocks)file.Dispose();inputLocks.Clear();backendLease?.Dispose();backendLease=null;}
    }
}
