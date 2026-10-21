using System.Text;
using System.Text.Json;

namespace LocalBrain.ClientHost.Slack;

// Derived summaries stay in the restricted store. Originals are never truncated or overwritten.
internal static class SlackConversation
{
    internal const int InputBytes=16000, SummaryBytes=4096, BatchBytes=65536;
    internal static int Bytes(string value)=>Encoding.UTF8.GetByteCount(value);
    internal static int EncodedBytes(string value)=>Bytes(JsonSerializer.Serialize(value))-2;
    internal static bool Fits(string context,string prompt)=>Bytes(context)+1+Bytes(prompt)<=InputBytes;
    private static string Base(string summary,string hash)=>JsonSerializer.Serialize(new{summary,source_history_hash=hash,history_is_summary=true});
    private static string WithDocument(string history,object? document)=>document is null?history:
        JsonSerializer.Serialize(new{history=JsonSerializer.Deserialize<JsonElement>(history),current_document=document});
    internal static int Budget(string prompt,object? document=null)
    {
        var budget=Math.Min(SummaryBytes,InputBytes-1-Bytes(prompt)-Bytes(WithDocument(Base("",new string('0',64)),document)));
        if(budget<256)throw new InvalidDataException("SLACK_PROMPT_REQUIRES_SMALLER_SECTION");
        return budget;
    }
    internal static object CurrentDocument(SlackStore store,SlackStore.Job job)
    {
        var texts=new List<string>();var references=new List<object>();
        foreach(var file in job.FileIds){
            var attachment=store.GetAttachment(job.Id,file);
            if(attachment?.Result is null)throw new InvalidDataException("SLACK_OCR_NOT_READY");
            texts.Add(JsonSerializer.Deserialize<JsonElement>(attachment.Result).GetProperty("text").GetString()??"");
            references.Add(new{file_id=file,input_hash=attachment.Hash,ocr_job=attachment.OcrJob,result_hash=SlackHash.Of(attachment.Result),external_ai="deny"});
        }
        return new{untrusted_ocr=string.Join("\n\n",texts),references,restriction=SlackRestriction.Instance};
    }
    internal static int JobBudget(SlackStore store,SlackStore.Job job)=>Budget(job.Text,job.FileIds.Length==0?null:CurrentDocument(store,job));
    internal static bool NeedsCompression(SlackStore store,SlackStore.Session session,int threshold)=>
        store.HasSummaryWaiter(session.Id)||(Bytes(JsonSerializer.Serialize(store.History(session.Id).Where(x=>x.Phase=="complete")))>threshold&&session.SummaryHash!=store.HistoryHash(session.Id));
    internal static object Turn(SlackStore.Job job)=>new{message_id=job.EventId,user_id=job.User,user=job.Text,assistant=job.Result,ocr_refs=job.FileIds};
    internal static string? Context(SlackStore store,SlackStore.Session session,SlackStore.Job[] history,string prompt,int threshold,object? document=null)
    {
        if(history.Length==0){var current=document is null?"":JsonSerializer.Serialize(document);if(!Fits(current,prompt))throw new InvalidDataException("SLACK_INFERENCE_INPUT_BUDGET");return current;}
        var latestOcr=history.LastOrDefault(x=>x.FileIds.Length>0);
        var retained=latestOcr?.FileIds.Select(f=>store.GetAttachment(latestOcr.Id,f)).Where(a=>a?.Result is not null)
            .Select(a=>new{file_id=a!.FileId,source_hash=a.Hash,ocr_text=JsonSerializer.Deserialize<JsonElement>(a.Result!).GetProperty("text").GetString()}).ToArray();
        var historyContext=JsonSerializer.Serialize(new{history=history.Select(Turn),latest_ocr=retained});
        var raw=WithDocument(historyContext,document);
        // The soft threshold schedules background compression; only the hard transport limit may block a reply.
        if(Fits(raw,prompt))return raw;
        var budget=Budget(prompt,document);
        if(session.Summary is null||session.SummaryHash!=store.HistoryHash(session.Id)||EncodedBytes(session.Summary)>budget)return null;
        var context=WithDocument(Base(session.Summary,session.SummaryHash),document);
        if(!Fits(context,prompt))throw new InvalidDataException("SLACK_INFERENCE_INPUT_BUDGET");
        // Whole recent turns are optional detail; the bound summary covers all omitted originals.
        for(var count=1;count<=Math.Min(2,history.Length);count++){
            var candidate=WithDocument(JsonSerializer.Serialize(new{summary=session.Summary,source_history_hash=session.SummaryHash,history_is_summary=true,latest=history.TakeLast(count).Select(Turn)}),document);
            if(!Fits(candidate,prompt))break;context=candidate;
        }
        return context;
    }
    private static IEnumerable<string> Parts(string value)
    {
        var part=new StringBuilder();var bytes=0;
        foreach(var rune in value.EnumerateRunes()){
            if(bytes+rune.Utf8SequenceLength>4096){yield return part.ToString();part.Clear();bytes=0;}
            part.Append(rune.ToString());bytes+=rune.Utf8SequenceLength;
        }
        if(part.Length>0)yield return part.ToString();
    }
    internal static List<string> Records(SlackStore store,SlackStore.Job[] history)
    {
        var records=new List<string>();
        void Add(string id,string kind,string text,string actor,string? hash=null){var index=0;foreach(var part in Parts(text))records.Add(JsonSerializer.Serialize(new{message_id=id,user_id=actor,kind,part=index++,source_hash=hash,text=part}));}
        foreach(var job in history.Where(x=>x.Phase=="complete")){
            Add(job.EventId,"user",job.Text,job.User);Add(job.EventId,"assistant",job.Result??"",job.User);
            foreach(var file in job.FileIds){var a=store.GetAttachment(job.Id,file);if(a?.Result is not null)Add(job.EventId+":"+file,"ocr",JsonSerializer.Deserialize<JsonElement>(a.Result).GetProperty("text").GetString()??"",job.User,a.Hash);}
        }
        return records;
    }
    internal static async Task<bool> Compress(SlackStore store,string session,int budget,Func<string,int,CancellationToken,Task<string>> summarize,Func<bool> admission,CancellationToken token)
    {
        var snapshot=store.Snapshot(session);var records=Records(store,snapshot.Jobs);
        var prefixes=new List<string>{SlackHash.Of("slack-summary-participants-v1")};
        foreach(var record in records)prefixes.Add(SlackHash.Of(prefixes[^1]+record));
        var saved=store.SummaryCheckpoint(session);var done=0;var summary="";
        if(saved is not null&&saved.Count>=0&&saved.Count<=records.Count&&prefixes[saved.Count]==saved.Hash){done=saved.Count;summary=saved.Summary;}
        async Task<string> Summarize(string source){
            var result=await summarize(source,budget,token);
            if(string.IsNullOrWhiteSpace(result)||EncodedBytes(result)>budget)throw new InvalidDataException("SLACK_SUMMARY_SIZE");
            return result;
        }
        while(done<records.Count){
            token.ThrowIfCancellationRequested();if(!admission())return false;
            var batch=new List<JsonElement>();var next=done;string source="";
            while(next<records.Count){
                batch.Add(JsonSerializer.Deserialize<JsonElement>(records[next]));
                var candidate=JsonSerializer.Serialize(new{previous_summary=summary,untrusted_records=batch});
                if(Bytes(candidate)>BatchBytes){batch.RemoveAt(batch.Count-1);break;}
                source=candidate;next++;
            }
            if(next==done)throw new InvalidDataException("SLACK_SUMMARY_RECORD_SIZE");
            summary=await Summarize(source);done=next;
            if(!store.SaveSummaryCheckpoint(session,done,prefixes[done],summary,snapshot.Hash))return false;
        }
        if(EncodedBytes(summary)>budget){if(!admission())return false;summary=await Summarize(JsonSerializer.Serialize(new{previous_summary=summary,compact_only=true}));}
        return store.SaveSummary(session,summary,snapshot.Hash);
    }
}
