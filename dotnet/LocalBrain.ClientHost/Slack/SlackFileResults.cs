using LocalBrain.ClientHost.AgentV2;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace LocalBrain.ClientHost.Slack;

internal static class SlackResultText
{
    // Explicit transcription requests do not require or invite model interpretation.
    internal static bool OcrOnly(string request){
        const RegexOptions flags=RegexOptions.IgnoreCase|RegexOptions.Singleline;
        if(Regex.IsMatch(request,@"文字起こし|文字.{0,30}テキスト|(?:OCR|読み取).{0,20}(?:テキスト|文字).{0,20}(?:返|出力|添付)",flags))return true;
        return !Regex.IsMatch(request,@"分析|考察|要約|要点|比較|解釈|計算",flags)&&Regex.IsMatch(request,@"(?:表|画像).{0,25}(?:HTML|Markdown|md).{0,20}(?:返|出力|添付|再現)|(?:HTML|Markdown|md).{0,20}(?:再現|文字起こし)",flags);
    }
    internal static byte[] Utf8(string value){var b=new UTF8Encoding(false,true).GetBytes(value);if(b.Length is <1 or >1024*1024)throw new InvalidDataException("SLACK_RESULT_FILE_SIZE");return b;}
}

internal sealed partial class SlackStore
{
    internal sealed record ResultFile(string Id,string JobId,string Channel,string Thread,string Filename,byte[] Bytes,string Hash,string Stage,string State,string? RemoteId,string? UploadUrl);
    private const string ResultFileSchema="""
        CREATE TABLE IF NOT EXISTS result_files(id TEXT PRIMARY KEY,job_id TEXT NOT NULL,channel TEXT NOT NULL,thread TEXT NOT NULL,filename TEXT NOT NULL,bytes BLOB NOT NULL,hash TEXT NOT NULL,stage TEXT NOT NULL,state TEXT NOT NULL,remote_id TEXT,upload_url TEXT,retry_after INTEGER NOT NULL);
        """;
    private void QueueResultFile(Job job,string kind,string text,SqliteTransaction tx)
    {
        if(kind is not("ocr" or "analysis" or "analysis-html" or "analysis-md" or "ocr-json" or "ocr-html" or "ocr-csv"))throw new InvalidDataException("SLACK_RESULT_KIND");
        var session=GetSession(job.SessionId,tx);if(session.Channel!=options.ChannelId)throw new UnauthorizedAccessException("SLACK_RESULT_CHANNEL");
        var filenameKind=kind is "analysis-html" or "analysis-md"?"analysis":kind;
        var id=job.Id+"-"+filenameKind+"-text";
        var bytes=SlackResultText.Utf8(text);var hash=SlackHash.Of(bytes);
        var existing=Scalar("SELECT hash FROM result_files WHERE id=$id;",tx,("$id",id)) as string;
        if(existing is not null){if(existing!=hash)throw new InvalidDataException("SLACK_RESULT_FILE_CHANGED");ValidateFrozenResult(id,hash,tx);return;}
        if(StoredBytes(tx)+bytes.Length>512L*1024*1024)throw new IOException("SLACK_STORAGE_ADMISSION");
        Execute("INSERT INTO result_files VALUES($id,$job,$channel,$thread,$filename,$bytes,$hash,'ticket','pending',NULL,NULL,0);",tx,("$id",id),("$job",job.Id),("$channel",session.Channel),("$thread",session.Thread),("$filename",filenameKind+"-"+job.Id+(kind switch{"ocr" or "analysis-md"=>".md","ocr-json"=>".json","ocr-html" or "analysis-html"=>".html","ocr-csv"=>".csv",_=>".txt"})),("$bytes",bytes),("$hash",hash));
    }
    private void QueueArtifact(Job job,string kind,Func<string> render,SqliteTransaction tx){
        try{var text=render();if(Encoding.UTF8.GetByteCount(text)>1024*1024)throw new InvalidDataException("SLACK_RESULT_FILE_SIZE");QueueResultFile(job,kind,text,tx);}
        catch(InvalidDataException e)when(e.Message=="SLACK_RESULT_FILE_SIZE"){
            var session=GetSession(job.SessionId,tx);QueueReply(job.Id+"-artifact-size",job.Id,session.Channel,session.Thread,"成果物が添付サイズ上限を超えました。必要な表・ページを分けて依頼してください。処理ID："+job.Id,"progress",tx);Audit("artifact_export_size_limit",job.Id,DateTimeOffset.UtcNow,tx);
        }
    }
    private void QueueDocument(Job job,bool ocr,Func<string> html,Func<string> markdown,SqliteTransaction tx)
    {
        if(!SlackReplyFormat.Markdown(job.Text)){
            try{QueueResultFile(job,ocr?"ocr-html":"analysis-html",html(),tx);return;}
            catch(InvalidDataException e)when(e.Message is "SLACK_RESULT_FILE_SIZE" or "SLACK_HTML_UNSUPPORTED" or "SLACK_TABLE_SIZE" or "SLACK_TABLE_CELL" or "SLACK_TABLE_OVERLAP"){
                var session=GetSession(job.SessionId,tx);
                QueueReply(job.Id+"-html-fallback",job.Id,session.Channel,session.Thread,"HTMLとして安全に添付できなかったため、Markdownで返します。処理ID："+job.Id,"progress",tx);
                Audit("artifact_html_fallback_markdown",job.Id,DateTimeOffset.UtcNow,tx);
            }
        }
        QueueArtifact(job,ocr?"ocr":"analysis-md",markdown,tx);
    }
    private bool FrozenOcrPrimary(Job job,SqliteTransaction tx)
    {
        foreach(var kind in new[]{"ocr","ocr-html"}){
            var id=job.Id+"-"+kind+"-text";
            if(Scalar("SELECT hash FROM result_files WHERE id=$id;",tx,("$id",id)) is string hash){ValidateFrozenResult(id,hash,tx);return true;}
        }
        return false;
    }
    private void ValidateFrozenResult(string id,string expected,SqliteTransaction tx)
    {
        var bytes=Scalar("SELECT bytes FROM result_files WHERE id=$id;",tx,("$id",id)) as byte[];
        if(bytes is null||SlackHash.Of(bytes)!=expected)throw new InvalidDataException("SLACK_RESULT_FILE_CHANGED");
    }
    private string OcrText(Job job,SqliteTransaction tx,bool markdown=false)
    {
        var texts=job.FileIds.Select(file=>GetAttachment(job.Id,file,tx)??throw new InvalidDataException("SLACK_OCR_NOT_FOUND")).Select(a=>{
            using var d=JsonDocument.Parse(a.Result??throw new InvalidDataException("SLACK_OCR_NOT_READY"));return markdown?a.Result!:d.RootElement.GetProperty("text").GetString()??"";
        }).ToArray();
        return markdown?SlackOcrMarkdown.Render(texts):string.Join("\n\n",texts);
    }
    private void QueueOcrFile(Job job,SqliteTransaction tx)
    {
        // Old uploads (including uncertain ones) retain exactly their original filename and bytes.
        var id=job.Id+"-ocr-text";
        if(Scalar("SELECT hash FROM result_files WHERE id=$id;",tx,("$id",id)) is string hash){ValidateFrozenResult(id,hash,tx);return;}
        QueueResultFile(job,"ocr",OcrText(job,tx,true),tx);
    }
    internal string QueueSavedOcrText(string id)
    {
        lock(gate){using var tx=db.BeginTransaction();var job=GetJob(id,tx);
            if(!options.ReturnFileResults||job.Phase!="complete"||job.FileIds.Length==0)throw new InvalidOperationException("SLACK_SAVED_OCR_UNAVAILABLE");
            if(!FrozenOcrPrimary(job,tx))QueueOcrFile(job,tx);Audit("ocr_text_return_queued",id,DateTimeOffset.UtcNow,tx);tx.Commit();return "queued";
        }
    }
    internal string RetryRejectedOcrTicket(string id)
    {
        lock(gate){using var tx=db.BeginTransaction();var job=GetJob(id,tx);
            if(!options.ReturnFileResults||job.Phase!="complete"||job.FileIds.Length==0)throw new InvalidOperationException("SLACK_SAVED_OCR_UNAVAILABLE");
            var resultId=Scalar("SELECT id FROM result_files WHERE job_id=$id AND id IN($old,$html) ORDER BY CASE WHEN id=$old THEN 0 ELSE 1 END LIMIT 1;",tx,("$id",id),("$old",id+"-ocr-text"),("$html",id+"-ocr-html-text")) as string??throw new InvalidDataException("SLACK_REJECTED_TICKET_RETRY_DENIED");
            var filename=Scalar("SELECT filename FROM result_files WHERE id=$id;",tx,("$id",resultId)) as string;
            if(filename is null||filename!="ocr-"+id+".txt"&&filename!="ocr-"+id+".md"&&filename!="ocr-html-"+id+".html")throw new InvalidDataException("SLACK_REJECTED_TICKET_RETRY_DENIED");
            var hash=SlackHash.Of(SlackResultText.Utf8(filename.EndsWith(".html",StringComparison.Ordinal)
                ?SlackOcrArtifacts.Html(job.FileIds.Select(file=>GetAttachment(job.Id,file,tx)?.Result??throw new InvalidDataException("SLACK_OCR_NOT_READY")))
                :OcrText(job,tx,filename.EndsWith(".md",StringComparison.Ordinal))));
            ValidateFrozenResult(resultId,hash,tx);
            // No bytes/share were attempted. Uncertain stages and any allocated remote ID are never reset.
            if(Execute("UPDATE result_files SET state='pending',retry_after=0 WHERE id=$id AND hash=$hash AND stage='ticket' AND state='held' AND remote_id IS NULL AND upload_url IS NULL;",tx,("$id",resultId),("$hash",hash))!=1)throw new InvalidDataException("SLACK_REJECTED_TICKET_RETRY_DENIED");
            Audit("operator_rejected_ticket_retry",id,DateTimeOffset.UtcNow,tx);tx.Commit();return "queued";
        }
    }
    private void QueueSuccessfulFileResults(Job job,string localResult,SqliteTransaction tx)
    {
        string[] Evidence(Job source)=>source.FileIds.Select(file=>GetAttachment(source.Id,file,tx)?.Result??throw new InvalidDataException("SLACK_OCR_NOT_READY")).ToArray();
        string[] evidence=[];
        if(job.FileIds.Length>0){
            evidence=Evidence(job);
            if(!FrozenOcrPrimary(job,tx))QueueDocument(job,true,()=>SlackOcrArtifacts.Html(evidence),()=>OcrText(job,tx,true),tx);
            if(!SlackResultText.OcrOnly(job.Text)){
                using var value=JsonDocument.Parse(localResult);
                var interpretation=value.RootElement.GetProperty("interpretation").GetString()??"";
                if(!string.IsNullOrWhiteSpace(interpretation))QueueDocument(job,false,()=>SlackAnalysisDocument.Html(interpretation),()=>SlackAnalysisDocument.Markdown(interpretation),tx);
            }
        }else{
            QueueDocument(job,false,()=>SlackAnalysisDocument.Html(localResult),()=>SlackAnalysisDocument.Markdown(localResult),tx);
            // A format-only follow-up may export retained evidence from this Session, never another thread.
            if(SlackReplyFormat.Requested(job.Text,"json")||SlackReplyFormat.Requested(job.Text,"csv")){
                var previous=Scalar("SELECT id FROM jobs WHERE session_id=$session AND phase='complete' AND files<>'[]' AND created<=$created ORDER BY created DESC,rowid DESC LIMIT 1;",tx,("$session",job.SessionId),("$created",job.Created)) as string;
                if(previous is not null)evidence=Evidence(GetJob(previous,tx));
            }
        }
        if(SlackReplyFormat.Requested(job.Text,"json")&&evidence.Length>0)QueueArtifact(job,"ocr-json",()=>SlackOcrArtifacts.Json(evidence),tx);
        if(SlackReplyFormat.Requested(job.Text,"csv")&&evidence.Length>0&&SlackOcrArtifacts.HasTables(evidence))QueueArtifact(job,"ocr-csv",()=>SlackOcrArtifacts.Csv(evidence),tx);
    }
    internal ResultFile? ClaimResultFile(DateTimeOffset now)
    {
        lock(gate){using var tx=db.BeginTransaction();using var c=Command("SELECT id,job_id,channel,thread,filename,bytes,hash,stage,state,remote_id,upload_url FROM result_files WHERE state='pending' AND retry_after<=$now ORDER BY rowid LIMIT 1;",tx,("$now",Ms(now)));ResultFile? value;
            using(var r=c.ExecuteReader())value=r.Read()?new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),(byte[])r[5],r.GetString(6),r.GetString(7),r.GetString(8),r.IsDBNull(9)?null:r.GetString(9),r.IsDBNull(10)?null:r.GetString(10)):null;
            if(value is not null){if(value.Channel!=options.ChannelId||SlackHash.Of(value.Bytes)!=value.Hash)throw new InvalidDataException("SLACK_RESULT_BINDING");Execute("UPDATE result_files SET state='sending' WHERE id=$id AND state='pending';",tx,("$id",value.Id));}tx.Commit();return value;
        }
    }
    internal void AdvanceResultFile(ResultFile file,string next,string? remote=null,string? url=null)
    {
        lock(gate){if((file.Stage,next) is not (("ticket","upload") or ("upload","complete") or ("complete","sent")))throw new InvalidDataException("SLACK_UPLOAD_STAGE");
            if(Execute("UPDATE result_files SET stage=$next,state=$state,remote_id=coalesce($remote,remote_id),upload_url=CASE WHEN $next='sent' THEN NULL ELSE coalesce($url,upload_url) END WHERE id=$id AND hash=$hash AND stage=$stage AND state='sending';",null,("$next",next),("$state",next=="sent"?"sent":"pending"),("$remote",remote),("$url",url),("$id",file.Id),("$hash",file.Hash),("$stage",file.Stage))!=1)throw new InvalidDataException("SLACK_UPLOAD_STATE_CHANGED");
        }
    }
    internal void HoldResultFile(ResultFile f,bool uncertain,string? rejection=null){lock(gate){using var tx=db.BeginTransaction();Execute("UPDATE result_files SET state=$state WHERE id=$id AND hash=$hash AND state='sending';",tx,("$state",uncertain?"uncertain":"held"),("$id",f.Id),("$hash",f.Hash));var code=rejection is not null&&Regex.IsMatch(rejection,"^[a-z0-9_]{1,64}$")?rejection:"unknown";Audit(uncertain?"file_delivery_uncertain":"file_delivery_rejected_"+code,f.JobId,DateTimeOffset.UtcNow,tx);tx.Commit();}}
    internal void RetryResultFile(ResultFile f,DateTimeOffset after){lock(gate)Execute("UPDATE result_files SET state='pending',retry_after=$at WHERE id=$id AND hash=$hash AND state='sending';",null,("$at",Ms(after)),("$id",f.Id),("$hash",f.Hash));}
}

internal static class SlackFileDelivery
{
    internal static async Task Step(SlackStore store,SlackApi api,CancellationToken token)
    {
        if(store.ClaimResultFile(DateTimeOffset.UtcNow) is not{} file)return;
        using var restricted=RestrictedExecution.EnterSlack();
        // Local validation cannot have allocated a Slack ticket; distinguish it from a lost response.
        if(file.Stage=="ticket"&&!SlackApi.ValidResultUploadArgument(file.Filename,file.Bytes.Length)){
            store.HoldResultFile(file,false,"local_upload_argument");return;
        }
        try{
            if(file.Stage=="ticket"){
                var ticket=await api.ResultUploadTicket(file.Filename,file.Bytes.Length,token);
                store.AdvanceResultFile(file,"upload",ticket.Id,ticket.Url);
            }else if(file.Stage=="upload"){
                await api.UploadResultBytes(file.UploadUrl??throw new InvalidDataException("SLACK_UPLOAD_URL_MISSING"),file.Bytes,token);store.AdvanceResultFile(file,"complete");
            }else if(file.Stage=="complete"){
                await api.CompleteResultUpload(file,token);store.AdvanceResultFile(file,"sent");
            }else throw new InvalidDataException("SLACK_UPLOAD_STAGE");
        }catch(SlackRateLimit rate){store.RetryResultFile(file,DateTimeOffset.UtcNow.AddSeconds(rate.Seconds));}
        catch(SlackRejected rejected){store.HoldResultFile(file,false,rejected.Code);}
        catch(Exception e)when(e is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or IOException){
            // A lost completion response may already have shared the file. No automatic repeat of any uncertain stage.
            store.HoldResultFile(file,true);
        }
    }
    internal static async Task Run(SlackStore store,SlackApi api,CancellationToken token){while(!token.IsCancellationRequested){await Step(store,api,token);await Task.Delay(1100,token);}}
}

internal static class SlackResultReturnRequests
{
    private sealed record Request(string JobId,string Format);
    internal static string Submit(SlackOptions options,string job,bool retryRejectedTicket=false)
    {
        if(!Guid.TryParseExact(job,"N",out _))throw new InvalidDataException("SLACK_JOB_ID");
        var directory=Path.Combine(options.Root,"result-requests");CanonicalStore.GuardPath(directory);Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,job+(retryRejectedTicket?"-retry-ticket":"")+".json");CanonicalStore.GuardPath(path);
        if(File.Exists(path)||File.Exists(path+".receipt"))return "existing_request_observe_only";
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{using(var f=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(f,new Request(job,retryRejectedTicket?"ocr-text-retry-ticket":"ocr-text"),SlackOptions.Json);f.Flush(true);}File.Move(temporary,path,false);return "queued_for_host";}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    internal static void Step(SlackOptions options,SlackStore store)
    {
        var directory=Path.Combine(options.Root,"result-requests");if(!Directory.Exists(directory))return;CanonicalStore.GuardPath(directory);
        foreach(var path in Directory.EnumerateFiles(directory,"*.json").Where(p=>!File.Exists(p+".receipt")).Take(20)){
            try{
            CanonicalStore.GuardPath(path);if(File.Exists(path+".receipt"))continue;
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None);if(input.Length>4096)continue;
            var request=JsonSerializer.Deserialize<Request>(input,SlackOptions.Json);
            if(request is null||!Guid.TryParseExact(request.JobId,"N",out _)||request.Format is not("ocr-text" or "ocr-text-retry-ticket")||Path.GetFileName(path)!=request.JobId+(request.Format=="ocr-text-retry-ticket"?"-retry-ticket":"")+".json")continue;
            var status="held";try{status=request.Format=="ocr-text"?store.QueueSavedOcrText(request.JobId):store.RetryRejectedOcrTicket(request.JobId);}catch(Exception e)when(e is InvalidOperationException or KeyNotFoundException or InvalidDataException){}
            using var output=new FileStream(path+".receipt",FileMode.CreateNew,FileAccess.Write,FileShare.None);JsonSerializer.Serialize(output,new{status,format="ocr-text",request_id=request.JobId},SlackOptions.Json);output.Flush(true);
            }catch(Exception e)when(e is IOException or JsonException or InvalidDataException){/* Observe incomplete/invalid local requests without terminating the receiver. */}
        }
    }
}
