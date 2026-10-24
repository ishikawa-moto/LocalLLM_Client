using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalBrain.ClientHost.AgentV2;

namespace LocalBrain.ClientHost.Ocr;

internal sealed class OcrJobStore(OcrConfig config)
{
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,WriteIndented=true};
    internal sealed record Manifest(string JobId,string RequestHash,string InputFileRef,string InputSha256,string InputExtension,string Profile,bool Experimental,bool RetainOriginal,string ConfigHash,string Origin,string ExternalAi,DateTimeOffset CreatedAt);
    internal sealed record State(string JobId,string Status,int PageCount,int CompletedPages,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt,string? ResultRef,string? ErrorCode,Dictionary<int,string> PageHashes);
    internal string Root=>config.Root;
    internal string Jobs=>Path.Combine(Root,"jobs");
    internal static string HashFile(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();}
    internal static void Id(string id){if(!Guid.TryParseExact(id,"N",out _))throw new OcrException("JOB_ID_INVALID");}
    internal string DirectoryFor(string id){Id(id);var p=Path.Combine(Jobs,id);CanonicalStore.GuardPath(p);return p;}
    internal static T Read<T>(string path){CanonicalStore.GuardPath(path);using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);if(file.Length>16*1024*1024)throw new OcrException("STATE_TOO_LARGE");return JsonSerializer.Deserialize<T>(file,Json)??throw new OcrException("STATE_CORRUPT");}
    internal static void Atomic<T>(string path,T value){
        CanonicalStore.GuardPath(path);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(f,value,Json);f.Flush(true);}File.Move(temp,path,true);
    }
    internal static void New<T>(string path,T value){using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);JsonSerializer.Serialize(file,value,Json);file.Flush(true);}
    internal FileStream Lock(){
        CanonicalStore.GuardPath(Root);Directory.CreateDirectory(Root);Directory.CreateDirectory(Jobs);
        // Retry only lock acquisition, before any mutation; never replay an operation with an unknown outcome.
        var deadline=Environment.TickCount64+500;
        while(true){
            try{return new FileStream(Path.Combine(Root,"state.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException e)when((e.HResult&65535)==32&&Environment.TickCount64<deadline){Thread.Sleep(10);}
        }
    }
    internal Manifest GetManifest(string id){
        var path=Path.Combine(DirectoryFor(id),"manifest.json");
        if(Read<JsonElement>(path+".binding").GetProperty("sha256").GetString()!=HashFile(path))throw new OcrException("MANIFEST_EVIDENCE_CHANGED");
        var m=Read<Manifest>(path);
        if(m.JobId!=id||m.ExternalAi!="deny"||m.Origin is not("local_file" or "slack" or "chat")||m.InputExtension is not(".png" or ".jpg" or ".jpeg" or ".webp" or ".pdf"))throw new OcrException("JOB_BINDING_MISMATCH");return m;
    }
    internal State GetState(string id){
        var s=Read<State>(Path.Combine(DirectoryFor(id),"state.json"));
        if(s.JobId!=id||s.Status is not("queued" or "running" or "paused" or "cancelling" or "complete" or "cancelled" or "failed" or "expiring" or "expired")||s.PageHashes is null||s.PageCount is <0 or >200||s.CompletedPages!=s.PageHashes.Count||s.PageHashes.Keys.Any(p=>p<0||p>=s.PageCount))throw new OcrException("STATE_CORRUPT");return s;
    }
    internal string Input(Manifest manifest)=>Path.Combine(DirectoryFor(manifest.JobId),"input","source"+manifest.InputExtension);
    internal static string ConfigHash(OcrConfig config)=>CanonicalStore.Hash(JsonSerializer.SerializeToUtf8Bytes(config,ClientConfig.JsonOptions));
    internal State Submit(string path,string expectedHash,string profile,string requestId,bool experimental=false,bool retainOriginal=false,string origin="local_file"){
        if(origin is not("local_file" or "slack" or "chat")||origin!="local_file"&&!Slack.RestrictedExecution.Active)throw new UnauthorizedAccessException("OCR_ORIGIN_AUTHORITY");
        config.Validate();
        if(!Regex.IsMatch(requestId,@"^[A-Za-z0-9_-]{8,100}$")||!Regex.IsMatch(expectedHash,@"^[a-fA-F0-9]{64}$"))throw new OcrException("REQUEST_INVALID");
        if(!config.QualifiedProfiles.Contains(profile,StringComparer.Ordinal)&&!(experimental&&config.ExperimentalProfiles.Contains(profile,StringComparer.Ordinal)))throw new OcrException("PROFILE_UNQUALIFIED");
        path=Path.GetFullPath(path);CanonicalStore.GuardPath(path);
        if(!config.InputRoots.Any(root=>CanonicalStore.Within(root,path))||!File.Exists(path))throw new OcrException("INPUT_OUTSIDE_ALLOWED_ROOT");
        var ext=Path.GetExtension(path).ToLowerInvariant();if(ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".pdf"))throw new OcrException("INPUT_FORMAT");
        if(new FileInfo(path).Length>config.MaxInputBytes)throw new OcrException("INPUT_TOO_LARGE");
        var binding=CanonicalStore.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{requestId,expectedHash=expectedHash.ToLowerInvariant(),profile,experimental,retainOriginal,config=ConfigHash(config)})));
        if(origin!="local_file")binding=CanonicalStore.Hash(Encoding.UTF8.GetBytes(binding+":"+origin));
        var id=CanonicalStore.Hash(Encoding.UTF8.GetBytes(requestId))[..32];
        using var lease=Lock();var dir=DirectoryFor(id);
        if(Directory.Exists(dir)){
            var old=GetManifest(id);if(old.RequestHash!=binding)throw new OcrException("REQUEST_ID_BINDING_MISMATCH");return GetState(id);
        }
        CheckStorage(new FileInfo(path).Length+8*1024*1024);
        // Publish the complete initial state by same-volume directory rename. Incomplete copies are never jobs.
        var staging=Path.Combine(Root,"incoming",id+"."+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(staging);
        New(Path.Combine(staging,"init-owner.json"),new{pid=Environment.ProcessId,birth=System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,job_id=id,created_at=DateTimeOffset.UtcNow});
        var inputs=Path.Combine(staging,"input");Directory.CreateDirectory(inputs);Directory.CreateDirectory(Path.Combine(staging,"pages"));Directory.CreateDirectory(Path.Combine(staging,"attempts"));
        var destination=Path.Combine(inputs,"source"+ext);
        try{
        using(var source=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))using(var target=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
            var buffer=new byte[65536];long copied=0;int n;
            while((n=source.Read(buffer))>0){copied+=n;if(copied>config.MaxInputBytes)throw new OcrException("INPUT_TOO_LARGE");target.Write(buffer,0,n);}target.Flush(true);
        }
        if(HashFile(destination)!=expectedHash.ToLowerInvariant()){File.Delete(destination);throw new OcrException("INPUT_HASH_MISMATCH");}
        var now=DateTimeOffset.UtcNow;
        var manifest=new Manifest(id,binding,"ocr:"+id+"/input",expectedHash.ToLowerInvariant(),ext,profile,experimental,retainOriginal,ConfigHash(config),origin,"deny",now);
        New(Path.Combine(staging,"manifest.json"),manifest);
        New(Path.Combine(staging,"manifest.json.binding"),new{sha256=HashFile(Path.Combine(staging,"manifest.json"))});
        var state=new State(id,"queued",0,0,now,now,null,null,[]);Atomic(Path.Combine(staging,"state.json"),state);Directory.Move(staging,dir);Audit(id,"queued",null);return state;
        }catch{
            if(Directory.Exists(staging)){
                foreach(var p in Directory.EnumerateFileSystemEntries(staging,"*",SearchOption.AllDirectories))CanonicalStore.GuardPath(p);
                Directory.Delete(staging,true);
            }
            throw;
        }
    }
    internal void Audit(string id,string status,string? code){
        // Dedicated metadata log: no body, source path, filename, OCR text or general task history.
        var line=JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,event_type="ocr_job_state",job_id=id,status,error_code=code},new JsonSerializerOptions(Json){WriteIndented=false})+Environment.NewLine;
        File.AppendAllText(Path.Combine(Root,"events.jsonl"),line,Encoding.UTF8);
    }
    internal State Transition(string id,string status,string? code=null,int? count=null){
        using var lease=Lock();var s=GetState(id);s=s with{Status=status,ErrorCode=code,PageCount=count??s.PageCount,UpdatedAt=DateTimeOffset.UtcNow};Atomic(Path.Combine(DirectoryFor(id),"state.json"),s);Audit(id,status,code);return s;
    }
    internal State Cancel(string id){
        using var lease=Lock();var s=GetState(id);if(s.Status is "complete" or "cancelled")return s;
        Atomic(Path.Combine(DirectoryFor(id),"cancel.json"),new{requested_at=DateTimeOffset.UtcNow});
        s=s with{Status="cancelling",UpdatedAt=DateTimeOffset.UtcNow};Atomic(Path.Combine(DirectoryFor(id),"state.json"),s);return s;
    }
    internal State Resume(string id){
        using var lease=Lock();var s=GetState(id);var m=GetManifest(id);
        if(s.Status=="expired")throw new OcrException("JOB_EXPIRED");
        if(s.Status is "complete" or "cancelled" or "cancelling" or "running" or "queued")return s;
        if(!File.Exists(Input(m))||HashFile(Input(m))!=m.InputSha256)throw new OcrException("INPUT_UNAVAILABLE_OR_CHANGED");
        VerifyPages(m,s);s=s with{Status="queued",ErrorCode=null,UpdatedAt=DateTimeOffset.UtcNow};Atomic(Path.Combine(DirectoryFor(id),"state.json"),s);return s;
    }
    internal void VerifyPages(Manifest manifest,State state){
        if(manifest.JobId!=state.JobId||manifest.ConfigHash!=ConfigHash(config)||manifest.ExternalAi!="deny"||state.CompletedPages!=state.PageHashes.Count)throw new OcrException("JOB_BINDING_MISMATCH");
        foreach(var pair in state.PageHashes){var p=Path.Combine(DirectoryFor(manifest.JobId),"pages",pair.Key+".json");if(!File.Exists(p)||HashFile(p)!=pair.Value)throw new OcrException("PAGE_EVIDENCE_CHANGED");}
    }
    internal State CommitPage(Manifest m,int page,JsonElement result){
        using var lease=Lock();var s=GetState(m.JobId);VerifyPages(m,s);
        if(page<0||page>=s.PageCount||result.GetProperty("page").GetInt32()!=page||result.GetProperty("input_sha256").GetString()!=m.InputSha256)throw new OcrException("PAGE_BINDING_MISMATCH");
        var path=Path.Combine(DirectoryFor(m.JobId),"pages",page+".json");var bytes=JsonSerializer.SerializeToUtf8Bytes(result,Json);var hash=CanonicalStore.Hash(bytes);
        var otherPageBytes=s.PageHashes.Where(p=>p.Key!=page).Sum(p=>new FileInfo(Path.Combine(DirectoryFor(m.JobId),"pages",p.Key+".json")).Length);
        if(otherPageBytes+bytes.Length>6L*1024*1024)throw new OcrException("JOB_RESULT_LIMIT");
        CheckStorage(bytes.Length);
        if(s.PageHashes.TryGetValue(page,out var prior)){if(prior!=hash)throw new OcrException("PAGE_RESULT_CONFLICT");return s;}
        // Orphaned atomic page commit may be imported only with exact body identity.
        if(File.Exists(path)){if(HashFile(path)!=hash)throw new OcrException("ORPHAN_PAGE_CONFLICT");}else {
            Atomic(path+".commit",new{job_id=m.JobId,input_sha256=m.InputSha256,config_hash=m.ConfigHash,page,sha256=hash});Atomic(path,result);
        }
        var hashes=new Dictionary<int,string>(s.PageHashes){[page]=hash};s=s with{CompletedPages=hashes.Count,PageHashes=hashes,UpdatedAt=DateTimeOffset.UtcNow};Atomic(Path.Combine(DirectoryFor(m.JobId),"state.json"),s);return s;
    }
    internal void RecoverPageCommits(Manifest m){
        using var lease=Lock();var s=GetState(m.JobId);VerifyPages(m,s);var hashes=new Dictionary<int,string>(s.PageHashes);
        for(var page=0;page<s.PageCount;page++){
            if(hashes.ContainsKey(page))continue;
            var p=Path.Combine(DirectoryFor(m.JobId),"pages",page+".json");if(!File.Exists(p))continue;
            var proof=Read<JsonElement>(p+".commit");var body=Read<JsonElement>(p);
            if(proof.GetProperty("job_id").GetString()!=m.JobId||proof.GetProperty("input_sha256").GetString()!=m.InputSha256||proof.GetProperty("config_hash").GetString()!=m.ConfigHash||proof.GetProperty("page").GetInt32()!=page||proof.GetProperty("sha256").GetString()!=HashFile(p)||body.GetProperty("page").GetInt32()!=page||body.GetProperty("input_sha256").GetString()!=m.InputSha256||body.GetProperty("external_ai").GetString()!="deny")throw new OcrException("ORPHAN_PAGE_CONFLICT");
            hashes.Add(page,HashFile(p));
        }
        if(hashes.Count!=s.CompletedPages)Atomic(Path.Combine(DirectoryFor(m.JobId),"state.json"),s with{PageHashes=hashes,CompletedPages=hashes.Count,UpdatedAt=DateTimeOffset.UtcNow});
    }
    internal State Finish(Manifest m){
        using var lease=Lock();var s=GetState(m.JobId);VerifyPages(m,s);if(s.PageCount<1||s.CompletedPages!=s.PageCount)throw new OcrException("PAGES_INCOMPLETE");
        if(File.Exists(Path.Combine(DirectoryFor(m.JobId),"cancel.json")))throw new OcrException("CANCELLED");
        var pages=s.PageHashes.OrderBy(p=>p.Key).Select(p=>Read<JsonElement>(Path.Combine(DirectoryFor(m.JobId),"pages",p.Key+".json"))).ToArray();
        var result=new{job_id=m.JobId,input_file_ref=m.InputFileRef,input_sha256=m.InputSha256,profile=m.Profile,experimental=m.Experimental,external_ai="deny",origin=m.Origin,status="complete",text=string.Join('\n',pages.Select(p=>p.GetProperty("text").GetString())),pages,needs_review=pages.Any(p=>p.GetProperty("needs_review").GetBoolean()),automatic_knowledge_adoption=false};
        var resultBytes=JsonSerializer.SerializeToUtf8Bytes(result,Json);if(resultBytes.Length>16*1024*1024)throw new OcrException("JOB_RESULT_LIMIT");CheckStorage(resultBytes.Length);
        var resultPath=Path.Combine(DirectoryFor(m.JobId),"result.json");var resultHash=CanonicalStore.Hash(resultBytes);
        if(File.Exists(resultPath)){if(HashFile(resultPath)!=resultHash)throw new OcrException("RESULT_EVIDENCE_CHANGED");}else Atomic(resultPath,result);
        var bindingPath=Path.Combine(DirectoryFor(m.JobId),"result-binding.json");
        if(File.Exists(bindingPath)){if(Read<JsonElement>(bindingPath).GetProperty("sha256").GetString()!=resultHash)throw new OcrException("RESULT_EVIDENCE_CHANGED");}else Atomic(bindingPath,new{sha256=resultHash});
        s=s with{Status="complete",ResultRef="ocr:"+m.JobId+"/result",ErrorCode=null,UpdatedAt=DateTimeOffset.UtcNow};Atomic(Path.Combine(DirectoryFor(m.JobId),"state.json"),s);Audit(m.JobId,"complete",null);
        TryCleanupInput(m);return s;
    }
    internal JsonElement Result(string id){
        var m=GetManifest(id);var s=GetState(id);VerifyPages(m,s);if(s.Status!="complete")throw new OcrException("RESULT_NOT_READY");
        var p=Path.Combine(DirectoryFor(id),"result.json");var expected=Read<JsonElement>(Path.Combine(DirectoryFor(id),"result-binding.json")).GetProperty("sha256").GetString();
        if(HashFile(p)!=expected)throw new OcrException("RESULT_EVIDENCE_CHANGED");return Read<JsonElement>(p);
    }
    internal bool Expire(string id){
        using var lease=Lock();var m=GetManifest(id);var s=GetState(id);
        if(s.Status=="expired")return true;
        if(DateTimeOffset.UtcNow-m.CreatedAt<TimeSpan.FromHours(config.RetentionHours))return false;
        var dir=DirectoryFor(id);
        var attempts=Path.Combine(dir,"attempts");
        foreach(var processFile in Directory.Exists(attempts)?Directory.EnumerateFiles(attempts,"process.json",SearchOption.AllDirectories):[]){
            var p=Read<JsonElement>(processFile);if(TaskAgentCoordinator.IsAlive(p.GetProperty("pid").GetInt32(),p.GetProperty("birth").GetInt64()))throw new OcrException("PRIOR_WORKER_STILL_ALIVE_INSPECT");
        }
        foreach(var attempt in Directory.Exists(attempts)?Directory.EnumerateDirectories(attempts):[])OcrSandbox.Recover(config,m,attempt);
        Atomic(Path.Combine(dir,"state.json"),s with{Status="expiring",ErrorCode="RETENTION_EXPIRED"});
        foreach(var child in new[]{"input","pages","attempts"}){
            var p=Path.Combine(dir,child);if(!Directory.Exists(p))continue;
            CanonicalStore.GuardPath(p);foreach(var item in Directory.EnumerateFileSystemEntries(p,"*",SearchOption.AllDirectories))CanonicalStore.GuardPath(item);
            Directory.Delete(p,true);
        }
        foreach(var name in new[]{"result.json","result-binding.json"}){var p=Path.Combine(dir,name);CanonicalStore.GuardPath(p);if(File.Exists(p))File.Delete(p);}
        Atomic(Path.Combine(dir,"state.json"),s with{Status="expired",ResultRef=null,CompletedPages=0,PageHashes=[],ErrorCode="RETENTION_EXPIRED",UpdatedAt=DateTimeOffset.UtcNow});Audit(id,"expired","RETENTION_EXPIRED");return true;
    }
    internal void RecoverIncoming(){
        using var lease=Lock();var incoming=Path.Combine(Root,"incoming");if(!Directory.Exists(incoming))return;
        foreach(var stage in Directory.EnumerateDirectories(incoming)){
            if(!Regex.IsMatch(Path.GetFileName(stage),"^[a-f0-9]{32}\\.[a-f0-9]{32}$"))continue;
            var ownerPath=Path.Combine(stage,"init-owner.json");if(!File.Exists(ownerPath))continue;
            try{
            var owner=Read<JsonElement>(ownerPath);
            if(TaskAgentCoordinator.IsAlive(owner.GetProperty("pid").GetInt32(),owner.GetProperty("birth").GetInt64()))continue;
            if(owner.GetProperty("job_id").GetString()!=Path.GetFileName(stage).Split('.')[0])throw new OcrException("INIT_OWNER_MISMATCH");
            var paths=Directory.EnumerateFileSystemEntries(stage,"*",SearchOption.AllDirectories).ToArray();foreach(var p in paths)CanonicalStore.GuardPath(p);
            if(paths.Any(p=>!Directory.Exists(p)&&!new[]{"init-owner.json","manifest.json","manifest.json.binding","state.json","source.png","source.jpg","source.jpeg","source.webp","source.pdf"}.Contains(Path.GetFileName(p),StringComparer.Ordinal)))continue;
            Directory.Delete(stage,true);
            }catch(Exception error)when(error is JsonException or OcrException or KeyNotFoundException or InvalidOperationException){/* Preserve unknown staging evidence; do not block unrelated jobs. */}
        }
    }
    internal void CheckStorage(long reserve){
        var used=Directory.EnumerateFiles(Root,"*",SearchOption.AllDirectories).Sum(p=>new FileInfo(p).Length);
        if(used+reserve>config.MaxStoredBytes||new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace<config.MinFreeDiskBytes+reserve)throw new OcrException("STORAGE_ADMISSION");
    }
    internal bool TryCleanupInput(Manifest m,bool force=false){
        if(m.RetainOriginal)return false;
        var marker=Path.Combine(DirectoryFor(m.JobId),"input-cleanup-pending.json");
        if(!force&&File.Exists(marker)&&(Read<JsonElement>(marker).GetProperty("error_code").GetString()=="OWNED_INPUT_CHANGED_INSPECT"||DateTime.UtcNow-File.GetLastWriteTimeUtc(marker)<TimeSpan.FromSeconds(30)))return false;
        try{
            var path=Input(m);CanonicalStore.GuardPath(path);
            if(File.Exists(path)){
                if(HashFile(path)!=m.InputSha256){if(!File.Exists(marker))Atomic(marker,new{error_code="OWNED_INPUT_CHANGED_INSPECT"});return false;}
                File.Delete(path);
            }
            if(File.Exists(marker))File.Delete(marker);return true;
        }catch(Exception e)when(e is IOException or UnauthorizedAccessException){if(!File.Exists(marker))Atomic(marker,new{error_code="INPUT_CLEANUP_PENDING"});else File.SetLastWriteTimeUtc(marker,DateTime.UtcNow);return false;}
    }
}
