using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LocalBrain.ClientHost.AgentV2;
using LocalBrain.ClientHost.Ocr;
using LocalBrain.ClientHost.Slack;

namespace LocalBrain.ClientHost.Chat;

// Fixed, local-only mailbox contract. Images never select a command, path, URL or model.
internal sealed record ChatOcrRequest(string Schema,string JobId,string ConversationId,string InputSha256,
    string Extension,string[] Formats,string? Bytes,bool CancelRequested)
{
    internal const string Version="localbrain.chat-ocr.job.v1";
    internal const int MaxInputBytes=4*1024*1024;
    internal void Validate(){
        if(Schema!=Version||!Guid.TryParseExact(JobId,"N",out _)||JobId!=JobId.ToLowerInvariant()||
            !Guid.TryParseExact(ConversationId,"D",out _)||ConversationId!=ConversationId.ToLowerInvariant()||
            InputSha256 is null||!Regex.IsMatch(InputSha256,@"\A[a-f0-9]{64}\z")||
            Extension is not(".png" or ".jpg" or ".webp" or ".pdf")||Formats is null||Formats.Length is <1 or >4||
            !Formats.Contains("md",StringComparer.Ordinal)||Formats.Distinct(StringComparer.Ordinal).Count()!=Formats.Length||
            Formats.Any(f=>f is not("md" or "json" or "html" or "csv"))||Bytes is {Length:>5592408})throw new InvalidDataException("CHAT_OCR_ENVELOPE_INVALID");
    }
    internal string Binding()=>CanonicalStore.Hash(JsonSerializer.SerializeToUtf8Bytes(new{Schema,JobId,ConversationId,InputSha256,Extension,Formats=Formats.Order(StringComparer.Ordinal).ToArray()},ChatOcrProcessor.Json));
    internal byte[] Decode(){
        Validate();if(Bytes is null)throw new InvalidDataException("CHAT_OCR_INPUT_REQUIRED");byte[] b;
        try{b=Convert.FromBase64String(Bytes);}catch(FormatException){throw new InvalidDataException("CHAT_OCR_INPUT_BASE64");}
        if(b.Length is <1 or >MaxInputBytes||CanonicalStore.Hash(b)!=InputSha256)throw new InvalidDataException("CHAT_OCR_INPUT_BINDING");
        var magic=Extension switch{
            ".png"=>b.Length>=8&&b.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),
            ".jpg"=>b.Length>=3&&b[0]==255&&b[1]==216&&b[2]==255,
            ".webp"=>b.Length>=12&&b.AsSpan(0,4).SequenceEqual("RIFF"u8)&&b.AsSpan(8,4).SequenceEqual("WEBP"u8),
            ".pdf"=>b.Length>=5&&b.AsSpan(0,5).SequenceEqual("%PDF-"u8),_=>false};
        if(!magic)throw new InvalidDataException("CHAT_OCR_INPUT_FORMAT");return b;
    }
}

internal sealed record ChatOcrRelayOptions(bool Enabled,string Root)
{
    internal static string ConfigPath=>Path.Combine(CanonicalStore.DefaultRoot,"chat-ocr","config.json");
    internal static ChatOcrRelayOptions? Load(){
        if(!File.Exists(ConfigPath))return null;CanonicalStore.GuardPath(ConfigPath);
        if(new FileInfo(ConfigPath).Length>4096)throw new InvalidDataException("CHAT_OCR_CONFIG_SIZE");
        var o=JsonSerializer.Deserialize<ChatOcrRelayOptions>(File.ReadAllBytes(ConfigPath),ChatOcrProcessor.Json)??throw new InvalidDataException("CHAT_OCR_CONFIG_INVALID");
        if(!o.Enabled)return null;o.Validate();return o;
    }
    internal void Validate(){
        if(!Path.IsPathFullyQualified(Root)||Root.StartsWith("\\\\",StringComparison.Ordinal))throw new InvalidDataException("CHAT_OCR_PRIVATE_ROOT_REQUIRED");CanonicalStore.GuardPath(Root);
        for(var d=new DirectoryInfo(Root);d is not null;d=d.Parent)if(File.Exists(Path.Combine(d.FullName,".git"))||Directory.Exists(Path.Combine(d.FullName,".git")))throw new InvalidDataException("CHAT_OCR_PRIVATE_ROOT_REQUIRED");
    }
}

internal sealed class ChatOcrProcessor
{
    internal static readonly JsonSerializerOptions Json=new(OcrJobStore.Json){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    private sealed record Receipt(string JobId,string ConversationId,string InputSha256,string Binding,string OcrJobId,DateTimeOffset CreatedAt);
    private sealed record Frozen(string Sha256,string Payload);
    private readonly ChatOcrRelayOptions options;
    private readonly OcrConfig config;
    private readonly OcrJobStore store;
    internal ChatOcrProcessor(ChatOcrRelayOptions options,OcrConfig config){options.Validate();this.options=options;this.config=config;store=new(config);CanonicalStore.GuardPath(options.Root);Directory.CreateDirectory(options.Root);}
    private string ReceiptPath(string job)=>Path.Combine(options.Root,job+".receipt.json");
    private string FrozenPath(string job)=>Path.Combine(options.Root,job+".update.json");
    private static T Read<T>(string p){CanonicalStore.GuardPath(p);using var f=File.OpenRead(p);if(f.Length>40L*1024*1024)throw new InvalidDataException("CHAT_OCR_LOCAL_STATE_SIZE");return JsonSerializer.Deserialize<T>(f,Json)??throw new InvalidDataException("CHAT_OCR_LOCAL_STATE_INVALID");}
    private void Save<T>(string p,T value){var bytes=JsonSerializer.SerializeToUtf8Bytes(value,Json);if(bytes.Length>40*1024*1024||Directory.EnumerateFiles(options.Root).Sum(f=>new FileInfo(f).Length)+bytes.Length>256L*1024*1024)throw new IOException("CHAT_OCR_STORAGE_ADMISSION");OcrJobStore.Atomic(p,value);}
    private static string Wire(object value)=>JsonSerializer.Serialize(value,Json);
    private void DeleteKnownStaging(ChatOcrRequest job){
        var input=Path.Combine(config.InputRoots[0],"chat-restricted",job.JobId+"-"+job.InputSha256+job.Extension);
        foreach(var path in new[]{input,input+".incoming"}){CanonicalStore.GuardPath(path);if(File.Exists(path)&&OcrJobStore.HashFile(path)==job.InputSha256)File.Delete(path);}
    }

    private string Freeze(ChatOcrRequest job,string value){
        if(Encoding.UTF8.GetByteCount(value)>16*1024*1024)value=Wire(new{schema="localbrain.chat-ocr.update.v1",job_id=job.JobId,input_sha256=job.InputSha256,state="failed",error_code="CHAT_OCR_RESULT_SIZE_LIMIT"});
        Save(FrozenPath(job.JobId),new Frozen(CanonicalStore.Hash(Encoding.UTF8.GetBytes(value)),value));return value;
    }
    internal string Step(ChatOcrRequest job){
        job.Validate();using var restricted=RestrictedExecution.EnterChat();var path=ReceiptPath(job.JobId);Receipt receipt;
        if(File.Exists(path)){
            receipt=Read<Receipt>(path);if(receipt.JobId!=job.JobId||receipt.ConversationId!=job.ConversationId||receipt.InputSha256!=job.InputSha256||receipt.Binding!=job.Binding())throw new InvalidDataException("CHAT_OCR_IMMUTABLE_BINDING");
        }else{
            if(job.CancelRequested){
                receipt=new(job.JobId,job.ConversationId,job.InputSha256,job.Binding(),"",DateTimeOffset.UtcNow);Save(path,receipt);DeleteKnownStaging(job);
                return Freeze(job,Wire(new{schema="localbrain.chat-ocr.update.v1",job_id=job.JobId,input_sha256=job.InputSha256,state="cancelled"}));
            }
            var bytes=job.Decode();var directory=Path.Combine(config.InputRoots[0],"chat-restricted");CanonicalStore.GuardPath(directory);Directory.CreateDirectory(directory);
            var input=Path.Combine(directory,job.JobId+"-"+job.InputSha256+job.Extension);CanonicalStore.GuardPath(input);
            // Publish an input atomically. Unknown/corrupt pre-existing files are kept for inspection.
            if(File.Exists(input)){if(OcrJobStore.HashFile(input)!=job.InputSha256)throw new InvalidDataException("CHAT_OCR_STAGED_INPUT_CHANGED");}
            else{
                if(Directory.EnumerateFiles(directory).Sum(f=>new FileInfo(f).Length)+bytes.Length>64L*1024*1024)throw new OcrException("CHAT_STAGING_ADMISSION");
                var temp=input+".incoming";CanonicalStore.GuardPath(temp);
                if(File.Exists(temp)){if(OcrJobStore.HashFile(temp)!=job.InputSha256)throw new InvalidDataException("CHAT_OCR_STAGED_INPUT_CHANGED");}
                else{using var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None);f.Write(bytes);f.Flush(true);}
                File.Move(temp,input,false);
            }
            var state=store.Submit(input,job.InputSha256,job.Extension==".pdf"?"PDF_DOCUMENT":"AUTO","chat_"+job.JobId,origin:"chat");
            receipt=new(job.JobId,job.ConversationId,job.InputSha256,job.Binding(),state.JobId,DateTimeOffset.UtcNow);Save(path,receipt);
            if(OcrJobStore.HashFile(input)==job.InputSha256)File.Delete(input);
        }
        if(File.Exists(FrozenPath(job.JobId))){
            var frozen=Read<Frozen>(FrozenPath(job.JobId));if(CanonicalStore.Hash(Encoding.UTF8.GetBytes(frozen.Payload))!=frozen.Sha256)throw new InvalidDataException("CHAT_OCR_FROZEN_UPDATE_CHANGED");
            using var doc=JsonDocument.Parse(frozen.Payload);if(doc.RootElement.GetProperty("job_id").GetString()!=job.JobId||doc.RootElement.GetProperty("input_sha256").GetString()!=job.InputSha256)throw new InvalidDataException("CHAT_OCR_FROZEN_UPDATE_BINDING");
            if(job.CancelRequested)return Wire(new{schema="localbrain.chat-ocr.update.v1",job_id=job.JobId,input_sha256=job.InputSha256,state="cancelled"});
            return frozen.Payload;
        }
        if(receipt.OcrJobId.Length==0)return Freeze(job,Wire(new{schema="localbrain.chat-ocr.update.v1",job_id=job.JobId,input_sha256=job.InputSha256,state="cancelled"}));
        var current=store.GetState(receipt.OcrJobId);var manifest=store.GetManifest(receipt.OcrJobId);
        if(manifest.Origin!="chat"||manifest.InputSha256!=job.InputSha256)throw new InvalidDataException("CHAT_OCR_SOURCE_BINDING");
        if(job.CancelRequested){
            if(current.Status is not("complete" or "cancelled" or "failed" or "expired"))current=store.Cancel(receipt.OcrJobId);
            var cancellation=Wire(new{schema="localbrain.chat-ocr.update.v1",job_id=job.JobId,input_sha256=job.InputSha256,state=current.Status=="cancelling"?"cancelling":"cancelled"});
            return current.Status=="cancelling"?cancellation:Freeze(job,cancellation);
        }
        if(current.Status=="complete"){
            var result=store.Result(receipt.OcrJobId);var raw=result.GetRawText();var formats=new HashSet<string>(job.Formats,StringComparer.Ordinal);if(SlackOcrArtifacts.HasTables([raw]))formats.Add("json");
            var artifacts=new List<object>();var warnings=new List<string>();
            foreach(var format in new[]{"md","json","html","csv"}.Where(formats.Contains)){
                try{
                    var text=format switch{"md"=>SlackOcrMarkdown.Render([raw]),"json"=>SlackOcrArtifacts.Json([raw]),"html"=>SlackOcrArtifacts.Html([raw]),"csv"=>SlackOcrArtifacts.Csv([raw]),_=>throw new InvalidDataException("CHAT_OCR_FORMAT")};
                    var bytes=SlackResultText.Utf8(text);artifacts.Add(new{format,filename="ocr-"+job.JobId+"."+format,mime_type=format switch{"md"=>"text/markdown","json"=>"application/json","html"=>"text/html",_=>"text/csv"},sha256=CanonicalStore.Hash(bytes),bytes=Convert.ToBase64String(bytes)});
                }catch(InvalidDataException e)when(e.Message=="SLACK_RESULT_FILE_SIZE"){warnings.Add("ARTIFACT_SIZE_LIMIT_"+format.ToUpperInvariant());}
            }
            return Freeze(job,Wire(new{schema="localbrain.chat-ocr.update.v1",job_id=job.JobId,input_sha256=job.InputSha256,state="complete",result,artifacts,warnings,external_ai="deny",automatic_knowledge_adoption=false}));
        }
        if(current.Status is "failed" or "cancelled" or "expired"){
            var code=current.ErrorCode is{} c&&Regex.IsMatch(c,@"\A[A-Z][A-Z0-9_]{0,79}\z")?c:"OCR_UNAVAILABLE";
            return Freeze(job,Wire(new{schema="localbrain.chat-ocr.update.v1",job_id=job.JobId,input_sha256=job.InputSha256,state=current.Status,error_code=code}));
        }
        return Wire(new{schema="localbrain.chat-ocr.update.v1",job_id=job.JobId,input_sha256=job.InputSha256,state=current.Status,page_count=current.PageCount,completed_pages=current.CompletedPages,wait_reason=current.ErrorCode});
    }
    internal string Report(ChatOcrRequest job){
        job.Validate();
        try{return Step(job);}
        catch(IOException e)when((e.HResult&65535)==32){return Wire(new{schema="localbrain.chat-ocr.update.v1",job_id=job.JobId,input_sha256=job.InputSha256,state="paused",wait_reason="OCR_STATE_BUSY"});}
        catch(Exception e)when(e is OcrException or InvalidDataException){
            // A validated request may fail locally; report a fixed terminal code instead of silently polling forever.
            // Changed receipt identity is not repaired or adopted.
            if(File.Exists(FrozenPath(job.JobId)))throw; // Never overwrite unknown/corrupt or previously terminal evidence.
            var p=ReceiptPath(job.JobId);
            if(File.Exists(p)){var old=Read<Receipt>(p);if(old.Binding!=job.Binding())throw new InvalidDataException("CHAT_OCR_IMMUTABLE_BINDING");}
            else Save(p,new Receipt(job.JobId,job.ConversationId,job.InputSha256,job.Binding(),"",DateTimeOffset.UtcNow));
            DeleteKnownStaging(job);
            var code=e is OcrException{Code:"PROFILE_UNQUALIFIED"}?"OCR_PROFILE_UNQUALIFIED":"CHAT_OCR_LOCAL_VALIDATION_FAILED";
            return Freeze(job,Wire(new{schema="localbrain.chat-ocr.update.v1",job_id=job.JobId,input_sha256=job.InputSha256,state="failed",error_code=code}));
        }
    }
    internal void Cleanup(DateTimeOffset now){
        foreach(var p in Directory.EnumerateFiles(options.Root,"*.receipt.json")){
            var receipt=Read<Receipt>(p);if(!Guid.TryParseExact(receipt.JobId,"N",out _)||Path.GetFileName(p)!=receipt.JobId+".receipt.json"||now-receipt.CreatedAt<TimeSpan.FromHours(24)||!File.Exists(FrozenPath(receipt.JobId)))continue;
            var frozen=Read<Frozen>(FrozenPath(receipt.JobId));if(CanonicalStore.Hash(Encoding.UTF8.GetBytes(frozen.Payload))!=frozen.Sha256)continue;
            File.Delete(FrozenPath(receipt.JobId));File.Delete(p);
        }
    }
}

internal static class ChatOcrRelay
{
    internal static async Task RunConfiguredAsync(GatewayClient gateway,CancellationToken token){
        ChatOcrRelayOptions? options;
        try{options=ChatOcrRelayOptions.Load();}catch(Exception e)when(e is IOException or JsonException or UnauthorizedAccessException or InvalidDataException){Console.Error.WriteLine("{\"component\":\"chat_ocr_relay\",\"code\":\"CONFIGURATION_UNAVAILABLE\"}");return;}
        if(options is null)return;OcrConfig? ocr;
        try{ocr=OcrConfig.Read();}catch(Exception e)when(e is OcrException or IOException or JsonException or UnauthorizedAccessException){Console.Error.WriteLine("{\"component\":\"chat_ocr_relay\",\"code\":\"OCR_CONFIGURATION_UNAVAILABLE\"}");return;}
        if(ocr is null)return;
        using var restricted=RestrictedExecution.EnterChat();CanonicalStore.GuardPath(options.Root);Directory.CreateDirectory(options.Root);
        using var lease=new FileStream(Path.Combine(options.Root,"relay.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);var processor=new ChatOcrProcessor(options,ocr);
        while(!token.IsCancellationRequested){
            try{
                var raw=await gateway.ChatOcrJsonAsync("/v1/chat-ocr/next","{\"schema\":\"localbrain.chat-ocr.poll.v1\"}",token);
                using var data=JsonDocument.Parse(raw);
                if(data.RootElement.ValueKind!=JsonValueKind.Object||data.RootElement.EnumerateObject().Any(p=>p.Name!="job")||!data.RootElement.TryGetProperty("job",out var job))throw new InvalidDataException("CHAT_OCR_GATEWAY_ENVELOPE_INVALID");
                if(job.ValueKind!=JsonValueKind.Null){
                    var input=JsonSerializer.Deserialize<ChatOcrRequest>(job.GetRawText(),ChatOcrProcessor.Json)??throw new InvalidDataException("CHAT_OCR_ENVELOPE_INVALID");
                    var report=processor.Report(input);await gateway.ChatOcrJsonAsync("/v1/chat-ocr/update",report,token);
                }
                processor.Cleanup(DateTimeOffset.UtcNow);
            }catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
            catch(Exception e)when(e is IOException or HttpRequestException or JsonException or UnauthorizedAccessException or InvalidDataException or OcrException or OperationCanceledException){
                // Metadata only. No exception message, image, OCR text, URL, source path or failed HTTP body.
                Console.Error.WriteLine("{\"component\":\"chat_ocr_relay\",\"code\":\"WAITING_FOR_LOCAL_OCR_OR_GATEWAY\"}");
            }
            try{await Task.Delay(TimeSpan.FromSeconds(5),token);}catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
        }
    }
}
