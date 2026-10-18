using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

namespace LocalBrain.ClientHost.Ocr;

internal sealed record OcrConfig(bool Enabled,string Root,string AssetRoot,string Python,string Worker,
    string Models,string[] InputRoots,Dictionary<string,string> AssetHashes,string[] QualifiedProfiles,
    string[] ExperimentalProfiles,long RamAdmissionBytes,long CommitAdmissionBytes,long WorkerMemoryBytes,
    double MaxMachineCpuPercent,string MeasurementEvidenceHash,int PageTimeoutSeconds=60,int MaxPages=200,
    int MaxInputBytes=25*1024*1024,long MaxStoredBytes=1024L*1024*1024,int Threads=2,int RetentionHours=24,long MinFreeDiskBytes=128L*1024*1024,string[]? BlockedConsumerImages=null)
{
    internal static string ConfigPath=>Environment.GetEnvironmentVariable("LOCALBRAIN_OCR_CONFIG")??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LocalBrain","control-plane","ocr","config.json");
    internal static OcrConfig? Read(bool requireEnabled=true){
        var path=ConfigPath;if(!File.Exists(path)){if(requireEnabled)throw new OcrException("OCR_DISABLED");return null;}
        CanonicalStore.GuardPath(path);if(new FileInfo(path).Length>2*1024*1024)throw new OcrException("OCR_CONFIG_INVALID");
        var value=JsonSerializer.Deserialize<OcrConfig>(File.ReadAllBytes(path),OcrJobStore.Json)??throw new OcrException("OCR_CONFIG_INVALID");
        if(!value.Enabled){if(requireEnabled)throw new OcrException("OCR_DISABLED");return null;}
        value.Validate();return value;
    }
    internal void Validate(){
        if(InputRoots is null||AssetHashes is null||QualifiedProfiles is null||ExperimentalProfiles is null||MeasurementEvidenceHash is null)throw new OcrException("OCR_CONFIG_INVALID");
        foreach(var path in new[]{Root,AssetRoot,Python,Worker,Models}.Concat(InputRoots)){
            if(!Path.IsPathFullyQualified(path)||path.StartsWith("\\\\",StringComparison.Ordinal))throw new OcrException("OCR_CONFIG_INVALID");CanonicalStore.GuardPath(path);
        }
        if(BlockedConsumerImages is {Length:>16})throw new OcrException("OCR_CONFIG_INVALID");
        foreach(var path in BlockedConsumerImages??[]){if(!Path.IsPathFullyQualified(path)||!Path.GetFileName(path).Equals("localbrain.exe",StringComparison.OrdinalIgnoreCase))throw new OcrException("OCR_CONFIG_INVALID");CanonicalStore.GuardPath(path);}
        if(CanonicalStore.Within(AssetRoot,Root)||CanonicalStore.Within(Root,AssetRoot)||!CanonicalStore.Within(AssetRoot,Python)||!CanonicalStore.Within(AssetRoot,Worker)||!CanonicalStore.Within(AssetRoot,Models)||
            InputRoots.Length is <1 or >16||AssetHashes.Count is <3 or >10000||Threads!=2||
            RamAdmissionBytes<128L*1024*1024||CommitAdmissionBytes<128L*1024*1024||RamAdmissionBytes<WorkerMemoryBytes||CommitAdmissionBytes<WorkerMemoryBytes||WorkerMemoryBytes<128L*1024*1024||WorkerMemoryBytes>4L*1024*1024*1024||
            !double.IsFinite(MaxMachineCpuPercent)||MaxMachineCpuPercent is <1 or >50||PageTimeoutSeconds is <5 or >300||MaxPages is <1 or >200||
            MaxInputBytes is <1024 or >25*1024*1024||MaxStoredBytes is <64L*1024*1024 or >2L*1024*1024*1024||
            MinFreeDiskBytes is <64L*1024*1024 or >1024L*1024*1024||RetentionHours is <1 or >168||MeasurementEvidenceHash.Length!=64||!MeasurementEvidenceHash.All(Uri.IsHexDigit))throw new OcrException("OCR_CONFIG_INVALID");
        var profiles=new[]{"AUTO","PRINTED_TEXT","HANDWRITING","TABLE_LAYOUT","PDF_DOCUMENT"};
        if(QualifiedProfiles.Contains("HANDWRITING",StringComparer.Ordinal))throw new OcrException("HANDWRITING_UNQUALIFIED");
        if(QualifiedProfiles.Concat(ExperimentalProfiles).Any(p=>!profiles.Contains(p,StringComparer.Ordinal)))throw new OcrException("OCR_CONFIG_INVALID");
    }
    internal void VerifyAssets(Func<bool>? priority=null){
        if(priority?.Invoke()==true)throw new OcrException("PRIORITY_WORK_ACTIVE");
        var executableTree=AssetFiles(Path.Combine(AssetRoot,"python"),priority)
            .Concat(AssetFiles(Models,priority))
            .Concat(Directory.EnumerateFiles(AssetRoot,"*.py",SearchOption.TopDirectoryOnly))
            .Select(p=>Path.GetRelativePath(AssetRoot,p).Replace('\\','/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(!executableTree.SetEquals(AssetHashes.Keys))throw new OcrException("OCR_ASSET_INVENTORY_MISMATCH");
        if(executableTree.Sum(p=>new FileInfo(Path.Combine(AssetRoot,p)).Length)>512L*1024*1024)throw new OcrException("OCR_ASSET_SIZE_LIMIT");
        var buffer=new byte[65536];
        foreach(var pair in AssetHashes){
            if(priority?.Invoke()==true)throw new OcrException("PRIORITY_WORK_ACTIVE");
            if(Path.IsPathRooted(pair.Key)||pair.Key.Split('/','\\').Any(p=>p is "." or ".." or ""))throw new OcrException("OCR_ASSET_PATH");
            var path=Path.GetFullPath(Path.Combine(AssetRoot,pair.Key));CanonicalStore.GuardPath(path);
            if(!CanonicalStore.Within(AssetRoot,path)||!File.Exists(path))throw new OcrException("OCR_ASSET_HASH_MISMATCH");
            using var file=File.OpenRead(path);if(file.Length>128L*1024*1024)throw new OcrException("OCR_ASSET_SIZE_LIMIT");
            using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);int read;
            while((read=file.Read(buffer))>0){if(priority?.Invoke()==true)throw new OcrException("PRIORITY_WORK_ACTIVE");hash.AppendData(buffer,0,read);}
            if(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()!=pair.Value)throw new OcrException("OCR_ASSET_HASH_MISMATCH");
        }
        foreach(var path in new[]{Python,Worker})if(!AssetHashes.ContainsKey(Path.GetRelativePath(AssetRoot,path).Replace('\\','/')))throw new OcrException("OCR_ASSET_UNBOUND");
    }
    private static IEnumerable<string> AssetFiles(string root,Func<bool>? priority){
        var pending=new Stack<(string Path,int Depth)>();pending.Push((root,0));int count=0;
        while(pending.TryPop(out var next)){
            CanonicalStore.GuardPath(next.Path);if(next.Depth>16)throw new OcrException("OCR_ASSET_PATH");
            foreach(var path in Directory.EnumerateFileSystemEntries(next.Path)){
                if(priority?.Invoke()==true)throw new OcrException("PRIORITY_WORK_ACTIVE");
                if(++count>10000)throw new OcrException("OCR_ASSET_PATH");CanonicalStore.GuardPath(path);
                if(Directory.Exists(path))pending.Push((path,next.Depth+1));else yield return path;
            }
        }
    }
}
internal sealed class OcrException(string code):Exception(code){internal string Code=>Message;}
