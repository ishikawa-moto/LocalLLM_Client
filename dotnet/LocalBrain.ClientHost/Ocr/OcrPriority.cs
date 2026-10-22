using System.Diagnostics;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

namespace LocalBrain.ClientHost.Ocr;

// Metadata-only activity leases. Important work never waits for OCR.
internal static class OcrPriority
{
    private sealed class Noop:IDisposable{public void Dispose(){}}
    internal static IDisposable Enter(string kind){
        OcrConfig? config;
        try{config=OcrConfig.Read(requireEnabled:false);}catch(Exception error)when(error is OcrException or IOException or JsonException or UnauthorizedAccessException){return new Noop();}
        if(config is null)return new Noop();
        if(kind is not ("agent" or "bridge" or "taskmemory" or "laya" or "build_test"))throw new ArgumentException("Activity kind");
        try{return Create(config,kind);}catch(Exception e)when(e is IOException or UnauthorizedAccessException or OcrException){return new Noop();}
    }
    private static IDisposable Create(OcrConfig config,string kind){
        var root=Path.Combine(config.Root,"priority");CanonicalStore.GuardPath(root);Directory.CreateDirectory(root);
        var path=Path.Combine(root,Guid.NewGuid().ToString("N")+".json");
        var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read,4096,FileOptions.DeleteOnClose);
        // The kernel-held lease, not a durable decision, is the signal. No disk fsync on important work's path.
        try{JsonSerializer.Serialize(stream,new{kind,pid=Environment.ProcessId,birth=Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks});stream.Flush();return stream;}
        catch{stream.Dispose();throw;}
    }
    internal static bool Active(OcrConfig config){
        var root=Path.Combine(config.Root,"priority");if(!Directory.Exists(root))return false;CanonicalStore.GuardPath(root);
        foreach(var path in Directory.EnumerateFiles(root,"*.json")){
            try{using var file=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);}
            catch(FileNotFoundException){}catch(IOException){return true;}
        }
        return false;
    }
    internal static bool Healthy(OcrConfig config){
        try{
            var root=Path.Combine(config.Root,"priority");CanonicalStore.GuardPath(root);Directory.CreateDirectory(root);
            using var probe=new FileStream(Path.Combine(root,Guid.NewGuid().ToString("N")+".probe"),FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.DeleteOnClose);
            probe.WriteByte(1);probe.Flush();return true;
        }catch(Exception e)when(e is IOException or UnauthorizedAccessException){return false;}
    }
    internal static bool LegacyConsumerActive(OcrConfig config)=>LegacyConsumerReason(config) is not null;
    // Fixed codes only; process paths are never returned.
    internal static string? LegacyConsumerReason(OcrConfig config){
        if(config.BlockedConsumerImages is null or {Length:0})return null;
        foreach(var process in Process.GetProcessesByName("localbrain"))using(process){
            try{if(config.BlockedConsumerImages.Contains(process.MainModule?.FileName??"",StringComparer.OrdinalIgnoreCase))return "legacy_blocked";}
            catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){return "legacy_unreadable";}
        }
        return null;
    }
}
