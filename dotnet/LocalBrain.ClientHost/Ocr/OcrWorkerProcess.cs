using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

namespace LocalBrain.ClientHost.Ocr;

internal static class OcrWorkerProcess
{
    internal sealed record Outcome(string State,string? ErrorCode,JsonElement? Result,long ElapsedMs,long PeakWorkingSet,long PeakPrivateCommit,double CpuSeconds,int? ExitCode,string StdoutHash,string StderrHash,string? ResultHash,long PreparationMs=0,string? ErrorType=null);
    private static async Task<string> ReadBounded(StreamReader reader,int limit,CancellationTokenSource overBudget){
        var text=new StringBuilder();var buffer=new char[1024];int n;
        while((n=await reader.ReadAsync(buffer))>0){if(text.Length+n>limit){overBudget.Cancel();return text.ToString();}text.Append(buffer,0,n);}
        return text.ToString();
    }
    internal static async Task<Outcome> RunAsync(OcrConfig config,string jobId,string input,string hash,string attempt,string operation,int page,string profile,Func<bool> priority,CancellationToken token){
        var watch=Stopwatch.StartNew();
        bool PreparationPriority(){if(watch.Elapsed>TimeSpan.FromSeconds(Math.Max(60,config.PageTimeoutSeconds)))throw new OcrException("PAGE_PREPARATION_TIMEOUT");return token.IsCancellationRequested||priority();}
        config.Validate();config.VerifyAssets(PreparationPriority);CanonicalStore.GuardPath(attempt);Directory.CreateDirectory(attempt);
        var scratch=Path.Combine(attempt,"scratch");Directory.CreateDirectory(scratch);var request=Path.Combine(attempt,"request.json");var resultPath=Path.Combine(scratch,"result.json");
        TaskCommandGate.WriteNew(request,new{operation,input_path=input,input_sha256=hash,profile,page,model_root=config.Models,variant="v5",result_path=resultPath});
        using var sandbox=new OcrSandbox(config.AssetRoot,attempt,scratch,jobId,[Path.GetDirectoryName(input)!]);
        if(PreparationPriority())return new("paused","PRIORITY_WORK_ACTIVE",null,watch.ElapsedMilliseconds,0,0,0,null,"","",null);
        // The input snapshot is a separate read-only directory; no normal task/credential paths are granted.
        var start=new ProcessStartInfo(config.Python){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=scratch,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(var arg in new[]{"-I","-B",config.Worker,"--request",request})start.ArgumentList.Add(arg);
        start.Environment.Clear();
        foreach(var key in new[]{"TEMP","TMP","LOCALAPPDATA","APPDATA","ProgramData","USERPROFILE"})start.Environment[key]=scratch;
        var windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);start.Environment["SystemRoot"]=windows;start.Environment["PATH"]=Path.Combine(windows,"System32");
        start.Environment["ProgramFiles"]=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);start.Environment["ProgramFiles(x86)"]=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        start.Environment["PYTHONDONTWRITEBYTECODE"]="1";start.Environment["DO_NOT_TRACK"]="1";
        using var budget=new CancellationTokenSource();
        using var child=WindowsChildJob.Start(start,ocrLimits:new(config.WorkerMemoryBytes,3,sandbox.Sid));
        var preparationMs=watch.ElapsedMilliseconds;var execution=Stopwatch.StartNew();
        TaskCommandGate.WriteNew(Path.Combine(attempt,"process.json"),new{pid=child.Process.Id,birth=child.Process.StartTime.ToUniversalTime().Ticks,owner_pid=Environment.ProcessId,job_id=jobId,network_capabilities=0,profile_sid=sandbox.Sid,memory_limit_bytes=config.WorkerMemoryBytes,affinity=3,priority="BelowNormal"});
        var stdout=ReadBounded(child.Output,32768,budget);var stderr=ReadBounded(child.Error,32768,budget);
        string? reason=null;long peakWorking=0,peakPrivate=0;double cpu=0;var resources=new OcrResources();_=resources.Read();long checkedResource=0;int highCpu=0;
        try{
            while(!child.Process.HasExited){
                try{child.Process.Refresh();peakWorking=Math.Max(peakWorking,child.Process.PeakWorkingSet64);peakPrivate=Math.Max(peakPrivate,child.Process.PrivateMemorySize64);cpu=child.Process.TotalProcessorTime.TotalSeconds;}
                catch(InvalidOperationException)when(child.Process.HasExited){break;}
                if(token.IsCancellationRequested){reason="CANCELLED";break;}
                if(priority()){reason="PRIORITY_WORK_ACTIVE";break;}
                if(watch.ElapsedMilliseconds-checkedResource>=250){
                    checkedResource=watch.ElapsedMilliseconds;var sample=resources.Read();
                    reason=Pressure(config,sample);highCpu=sample.MachineCpuPercent>config.MaxMachineCpuPercent?highCpu+1:0;
                    if(reason is null&&highCpu>=3)reason="CPU_PRESSURE";
                    if(reason is not null)break;
                }
                if(budget.IsCancellationRequested){reason="OUTPUT_LIMIT";break;}
                if(execution.Elapsed>TimeSpan.FromSeconds(config.PageTimeoutSeconds)){reason="PAGE_TIMEOUT";break;}
                await Task.Delay(50);
            }
        }finally{
            if(!child.Process.HasExited)child.Terminate();
            await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        var output=await stdout.WaitAsync(TimeSpan.FromSeconds(3));var error=await stderr.WaitAsync(TimeSpan.FromSeconds(3));
        if(reason is null&&budget.IsCancellationRequested)reason="OUTPUT_LIMIT";
        var outHash=CanonicalStore.Hash(Encoding.UTF8.GetBytes(output));var errHash=CanonicalStore.Hash(Encoding.UTF8.GetBytes(error));
        var exit=child.Process.ExitCode;
        string? diagnosticType=null;
        if(reason is null&&(exit!=0||!File.Exists(resultPath))){
            reason="WORKER_FAILED";
            try{using var diagnostic=JsonDocument.Parse(output.Trim());var code=diagnostic.RootElement.GetProperty("error_code").GetString();if(code is not null&&System.Text.RegularExpressions.Regex.IsMatch(code,"^[A-Z_]{1,80}$"))reason=code;diagnosticType=DiagnosticType(diagnostic.RootElement);}catch(Exception e)when(e is JsonException or KeyNotFoundException){}
        }
        JsonElement? result=null;
        if(reason is null){
            CanonicalStore.GuardPath(resultPath);
            if(new FileInfo(resultPath).Length>8*1024*1024)reason="RESULT_TOO_LARGE";
            else{
                using var document=JsonDocument.Parse(File.ReadAllBytes(resultPath));var value=document.RootElement;
                if(value.GetProperty("status").GetString()!="complete"||value.GetProperty("input_sha256").GetString()!=hash||value.GetProperty("external_ai").GetString()!="deny"||value.GetProperty("network_attempts").GetInt32()!=0)reason="WORKER_RESULT_BINDING";
                else result=value.Clone();
            }
        }
        var receipt=new Outcome(reason is null?"complete":reason is "PRIORITY_WORK_ACTIVE" or "RAM_PRESSURE" or "COMMIT_PRESSURE" or "CPU_PRESSURE"?"paused":reason=="CANCELLED"?"cancelled":"failed",reason,result,watch.ElapsedMilliseconds,peakWorking,peakPrivate,cpu,exit,outHash,errHash,result is null?null:OcrJobStore.HashFile(resultPath),preparationMs,diagnosticType);
        // Receipt excludes the private OCR body. Host separately commits validated page bytes.
        OcrJobStore.New(Path.Combine(attempt,"receipt.json"),receipt with{Result=null});
        return receipt;
    }
    internal static string? DiagnosticType(JsonElement diagnostic){
        if(!diagnostic.TryGetProperty("error_type",out var type)||type.ValueKind!=JsonValueKind.String)return null;
        return type.GetString() is "OnnxMemory" or "OnnxExecution" or "Memory" or "ImageDecode" or "Other"?type.GetString():"Other";
    }
    internal static string? Pressure(OcrConfig config,OcrResources.Sample sample)=>sample.AvailableRam<Math.Max(64L*1024*1024,config.RamAdmissionBytes-config.WorkerMemoryBytes)?"RAM_PRESSURE":sample.CommitHeadroom<Math.Max(64L*1024*1024,config.CommitAdmissionBytes-config.WorkerMemoryBytes)?"COMMIT_PRESSURE":null;
}
