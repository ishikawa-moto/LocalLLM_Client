using System.Diagnostics;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class TaskMemoryNativeTests
{
    private sealed class LargeResponseStream : Stream {
        public int BytesRead;public bool WasDisposed;
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>BytesRead;set=>throw new NotSupportedException();}
        public override int Read(byte[] buffer,int offset,int count) {
            var n=Math.Min(count,20*1024*1024-BytesRead);Array.Fill(buffer,(byte)'a',offset,n);BytesRead+=n;return n;
        }
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long length)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){WasDisposed=true;base.Dispose(disposing);}
    }
    private sealed class LargeResponseHandler(Stream stream) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StreamContent(stream)});
    }
    private sealed class StalledResponseStream : Stream {
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>0;set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default) {await Task.Delay(Timeout.Infinite,token);return 0;}
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long length)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
    public static Task OwnerFixtureAsync(string receipt) {
        var start=new ProcessStartInfo("dotnet") {UseShellExecute=false,CreateNoWindow=true};
        start.ArgumentList.Add(typeof(TaskMemoryNativeTests).Assembly.Location);start.ArgumentList.Add("--memory-job-target");
        using var job=WindowsChildJob.Start(start,pid=> {
            File.WriteAllText(receipt,pid.ToString());
            Environment.FailFast("TaskMemory backend owner failed immediately after OS creation");
        });
        throw new InvalidOperationException("Crash fixture returned");
    }
    public static async Task RunAsync() {
        foreach(var verb in new[]{HttpMethod.Get,HttpMethod.Post}) {
            var stream=new LargeResponseStream();using var handler=new LargeResponseHandler(stream);using var http=new HttpClient(handler);
            using var packet=new HttpRequestMessage(verb,$"http://{System.Net.IPAddress.Loopback}/fixture");
            try {await TaskMemoryQ8.SendJsonAsync(http,packet,CancellationToken.None);throw new Exception("Oversized response accepted");}
            catch(InvalidDataException ex) when(ex.Message.Contains("Host bound")) {}
            if(stream.BytesRead>6*1024*1024+8192 || !stream.WasDisposed)throw new Exception("Response buffered before Host limit or not closed");
        }
        using(var stalled=new StalledResponseStream())
        using(var handler=new LargeResponseHandler(stalled))
        using(var http=new HttpClient(handler){Timeout=TimeSpan.FromMilliseconds(80)})
        using(var packet=new HttpRequestMessage(HttpMethod.Get,$"http://{System.Net.IPAddress.Loopback}/fixture")) {
            try {await TaskMemoryQ8.SendJsonAsync(http,packet,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));throw new Exception("Stalled body accepted");}
            catch(OperationCanceledException) {}
        }
        if(TaskMemoryQ8.CompletionTimeout(9212)<=TimeSpan.FromSeconds(180) || TaskMemoryQ8.CompletionTimeout(62976)>TimeSpan.FromMinutes(15))throw new Exception("Measured completion deadline is not bounded");
        const string hash="FDFCB6A29B11188956DFBFD904223588A6C1B77EB250C3E8A36E1BD269DF91F7";
        var input=Path.Combine(Path.GetTempPath(),"memory-inputs");
        var model="Qwen3.8-27B-GSQ-RCO-IQ3_XXS.gguf";
        var options=new TaskMemoryQ8.Options(true,input,model,hash,[new(model,100,hash),new("bin/llama-server.exe",100,hash),new("bin/llama.dll",100,hash)]);
        TaskMemoryQ8.ValidateOptions(options);
        void Reject(TaskMemoryQ8.Options value) {try{TaskMemoryQ8.ValidateOptions(value);}catch(InvalidDataException){return;}throw new InvalidOperationException("Invalid memory inputs accepted");}
        Reject(options with {ModelSha256=new string('0',64)});
        foreach(var context in new[]{0,32768,65537,98303,98305,262144,int.MaxValue})Reject(options with {ContextLimit=context});
        TaskMemoryQ8.ValidateOptions(options with {ContextLimit=98304});
        TaskMemoryQ8.ValidateOptions(options with {ContextLimit=131072});
        var legacy=JsonSerializer.Deserialize<TaskMemoryQ8.Options>(JsonSerializer.Serialize(new {options.Enabled,options.InputDirectory,options.ModelFile,options.ModelSha256,options.Files}),LocalBrain.ClientHost.ClientConfig.JsonOptions)!;
        if(legacy.ContextLimit!=65536)throw new Exception("Legacy Host config did not retain64K context");
        Reject(options with {Files=[..options.Files,new("bin/../evil.dll",100,hash)]});
        Reject(options with {Files=[..options.Files,new("bin/unapproved.exe",100,hash)]});
        Reject(options with {Files=[..options.Files,new("bin/llama.DLL",100,hash)]});
        Reject(options with {InputDirectory="relative-inputs"});
        var parent=Path.Combine(Path.GetTempPath(),"memory-job-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(parent);
        var configFolder=Path.Combine(parent,"taskmemory-q8");Directory.CreateDirectory(configFolder);
        await File.WriteAllTextAsync(Path.Combine(configFolder,"config.json"),JsonSerializer.Serialize(options with {Enabled=false}));
        await using(var disabled=new TaskMemoryQ8(parent)) {
            try {await disabled.StartAsync();throw new Exception("Disabled backend started");}
            catch(InvalidOperationException ex) when(ex.Message.Contains("disabled")) {}
            if(File.Exists(Path.Combine(input,"host-backend.lock")))throw new Exception("Disabled backend acquired input lease");
        }
        var receipt=Path.Combine(parent,"owned-pid.txt");
        var start=new ProcessStartInfo("dotnet") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{typeof(TaskMemoryNativeTests).Assembly.Location,"--memory-job-owner",receipt})start.ArgumentList.Add(arg);
        using var owner=Process.Start(start)!;var o=owner.StandardOutput.ReadToEndAsync();var e=owner.StandardError.ReadToEndAsync();
        await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));await o;await e;
        if(owner.ExitCode==0 || !File.Exists(receipt))throw new InvalidOperationException("Owner failure fixture not exercised");
        var pid=int.Parse(await File.ReadAllTextAsync(receipt));
        try {using var child=Process.GetProcessById(pid);await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}
        catch(ArgumentException) { /* Already terminated by job close. */ }
        Console.WriteLine("Native memory input constraints and Windows Host-crash child termination checks passed.");
    }
}
