using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using LocalBrain.ClientHost.Slack;

internal static class SlackMarkdownTests
{
    internal static async Task RunAsync(string? samplePath=null)
    {
        var checks=0;void Check(bool ok,string name){if(!ok)throw new Exception("SLACK_MARKDOWN_TEST_FAILED: "+name);checks++;}
        object Span(string text,double x,double y,string? table=null,int? row=null,int? col=null,bool review=false,bool unreadable=false,string? hypothesis=null)=>new{text,bounding_box=new[]{x,y,x+30,y+10},table_id=table,row_index=row,column_index=col,needs_review=review,unreadable,hypothesis};
        object Cell(string text,int r,int c,int cs=1)=>new{text,row_index=r,column_index=c,row_span=1,column_span=cs,bounding_box=new[]{(double)c*40,30+r*20,(c+cs)*40d,50+r*20}};
        var evidence=JsonSerializer.Serialize(new{text="原文",pages=new object[]{new{
            page=0,text="PUBLIC sheet",needs_review=true,
            spans=new[]{Span("PUBLIC REPORT",0,0),Span("右",60,15.5),Span("左",0,16),Span("見出し",0,35,"table_0",0,0),Span("[判読不能]",0,55,"table_0",1,0,true,true,"合計"),Span("100",45,55,"table_0",1,1),Span("[判読不能]",0,90,unreadable:true,review:true),Span("[判読不能]",0,105,unreadable:true,review:true,hypothesis:"摘要"),Span("![remote](https://example.invalid/pixel) <img src='https://example.invalid/x'> | a*b",0,125)},
            tables=new[]{new{table_id="table_0",rows=2,columns=2,cells=new[]{Cell("見出し",0,0,2),Cell("[判読不能]",1,0),Cell("100",1,1)},markdown="UNTRUSTED STORED MARKDOWN"}}
        },new{page=1,text="PDF second page\nline2",needs_review=false}}});
        var md=SlackOcrMarkdown.Render([evidence]);
        Check(md.StartsWith("# OCR結果\n"),"Markdown document header");
        Check(md.Contains("## 添付 1")&&md.Contains("### ページ 2"),"attachment and PDF page separation");
        Check(md.IndexOf("PUBLIC REPORT",StringComparison.Ordinal)<md.IndexOf("| 列1",StringComparison.Ordinal)&&md.IndexOf("| 列1",StringComparison.Ordinal)<md.IndexOf("摘要",StringComparison.Ordinal),"text/table/text geometry order");
        Check(md.Contains("左　右"),"aligned words order by X even with unequal Y");
        Check(md.Contains("| 見出し |  |")&&md.Contains("1行×2列"),"merged cell anchored once and noted");
        Check(md.Split("見出し").Length==2,"table spans not duplicated");
        Check(md.Contains("\\[推測\\]合計")&&md.Contains("\\[推測\\]摘要"),"retained local guesses labeled in table and body");
        Check(md.Contains("\\[判読不能\\]"),"no hypothesis leaves unreadable marker");
        Check(md.Contains("**[要確認]**"),"uncertainty stays visible");
        Check(!md.Contains("![remote](")&&!md.Contains("<img")&&!md.Contains("UNTRUSTED STORED MARKDOWN"),"no active document image link or raw HTML copied");
        Check(md.Contains("&lt;img")&&md.Contains("\\| a\\*b"),"document characters escaped");
        Check(md.Contains("PDF second page  \nline2"),"plain page fallback retains line breaks");
        var multiple=SlackOcrMarkdown.Render(["{\"text\":\"ONE\"}","{\"text\":\"TWO\"}"]);
        Check(multiple.Contains("## 添付 2")&&multiple.Contains("ONE")&&multiple.Contains("TWO"),"all attachments preserved");
        var invalid=JsonSerializer.Serialize(new{pages=new[]{new{text="fallback",spans=new[]{Span("unmapped",0,1,"bad",0,0)},tables=new[]{new{table_id="bad",rows=1000000,columns=2,cells=Array.Empty<object>()}}}}});
        Check(SlackOcrMarkdown.Render([invalid]).Contains("unmapped"),"invalid table fails to text without allocation or lost span");
        var unknown=JsonSerializer.Serialize(new{pages=new[]{new{text="",spans=new[]{new{text="UNLOCATED",needs_review=true,table_id=(string?)null,row_index=(int?)null,column_index=(int?)null}},tables=Array.Empty<object>()}}});
        Check(SlackOcrMarkdown.Render([unknown]).Contains("UNLOCATED"),"missing geometry cannot drop retained text");
        Check(SlackResultText.OcrOnly("この表をmdで返してください")&&!SlackResultText.OcrOnly("要点を要約してください"),"layout-only request bypasses inference");
        try{SlackOcrMarkdown.Render([JsonSerializer.Serialize(new{text=new string('あ',400000)})]);throw new Exception("oversize accepted");}catch(InvalidDataException){checks++;}
        var root=Directory.CreateTempSubdirectory("lb-public-markdown-").FullName;
        var options=new SlackOptions(true,root,"TTEST","CTEST","UBOT",Path.Combine(root,"app.token"),Path.Combine(root,"bot.token"),ReturnFileResults:true);
        var now=DateTimeOffset.UtcNow;string job;
        using(var store=new SlackStore(options)){
            var ts=now.ToUnixTimeSeconds()+".000001";
            job=store.Accept(new("EVMD","TTEST","CTEST","UALICE",ts,ts,"<@UBOT> この表をmdで返してください",["FPUBLIC"],now),now.AddMinutes(-1),now).JobId!;
            while(store.ClaimReply(now) is{} ack)store.ReplySent(ack.Id,ts);
            Check(store.ClaimNext()?.Id==job,"public fixture job accepted");
            store.SaveAttachment(job,"FPUBLIC",".png",[1,2,3]);store.SaveOcrResult(job,"FPUBLIC",evidence);
            store.Finish(job,"{\"output_kind\":\"ocr_text\"}",true,true);
            var file=store.ClaimResultFile(now)!;
            Check(file.Filename=="ocr-"+job+".md"&&Encoding.UTF8.GetString(file.Bytes)==md,"structured result delivered as exact default Markdown bytes");
            store.HoldResultFile(file,true);
            store.QueueSavedOcrText(job);
            Check(store.ClaimResultFile(now) is null,"uncertain Markdown never requeued");
        }
        // Simulate an older runtime's frozen txt result in the isolated public database.
        var oldBytes=Encoding.UTF8.GetBytes("原文");
        using(var db=new SqliteConnection("Data Source="+Path.Combine(root,"slack.db"))){db.Open();using var c=db.CreateCommand();c.CommandText="UPDATE result_files SET filename=$f,bytes=$b,hash=$h,stage='ticket',state='held',remote_id=NULL,upload_url=NULL WHERE job_id=$j";c.Parameters.AddWithValue("$f","ocr-"+job+".txt");c.Parameters.AddWithValue("$b",oldBytes);c.Parameters.AddWithValue("$h",SlackHash.Of(oldBytes));c.Parameters.AddWithValue("$j",job);Check(c.ExecuteNonQuery()==1,"one legacy upload fixture");}
        using(var store=new SlackStore(options)){
            store.QueueSavedOcrText(job);Check(store.ClaimResultFile(now) is null,"legacy held file not overwritten or requeued by format default");
            Check(store.RetryRejectedOcrTicket(job)=="queued","legacy known-rejected ticket remains recoverable");
            var file=store.ClaimResultFile(now)!;Check(file.Filename.EndsWith(".txt")&&file.Bytes.SequenceEqual(oldBytes),"legacy retry retains original filename and bytes");
            store.AdvanceResultFile(file,"upload","FOLD","https://files.slack.com/upload/v1/OLD");
            var upload=store.ClaimResultFile(now)!;store.AdvanceResultFile(upload,"complete");var complete=store.ClaimResultFile(now)!;store.AdvanceResultFile(complete,"sent");
            store.QueueSavedOcrText(job);Check(store.ClaimResultFile(now) is null,"already sent legacy does not gain a second Markdown upload");
        }
        if(samplePath is not null)File.WriteAllText(samplePath,md,new UTF8Encoding(false));
        await Task.CompletedTask;
        Console.WriteLine(JsonSerializer.Serialize(new{slack_markdown_public_checks=checks,status="PASS",real_slack=false,model_or_ocr_runs=0,private_data=false}));
    }
}
