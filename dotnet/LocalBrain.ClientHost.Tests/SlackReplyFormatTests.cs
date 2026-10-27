using System.Net;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.Slack;
using Microsoft.Data.Sqlite;

internal static class SlackReplyFormatTests
{
    internal static async Task RunAsync()
    {
        var checks=0;void Check(bool ok,string label){if(!ok)throw new Exception("SLACK_REPLY_FORMAT: "+label);checks++;}
        foreach(var request in new[]{"HTMLで返してください","HTML形式で返してください","htmlファイルを添付してください","画像をHTMLで再現して","HTML"})Check(SlackReplyFormat.Requested(request,"html"),"Japanese HTML suffix: "+request);
        foreach(var request in new[]{"JSONで返してください","構造化JSON形式も添付してください","HTMLとJSONを返してください","JSONのみ"})Check(SlackReplyFormat.Requested(request,"json"),"explicit JSON: "+request);
        foreach(var request in new[]{"表を再現して","HTMLで返してください","JSONは不要です","JSON形式は不要です","JSONではなくHTMLで返して","JSONを添付しないで","JSONの添付は不要です","JSONについて説明して","JSONとは何ですか","No JSON, return HTML"})Check(!SlackReplyFormat.Requested(request,"json"),"no implicit/negated JSON: "+request);
        Check(SlackReplyFormat.Markdown("mdで返してください")&&SlackReplyFormat.Markdown("Markdown形式で返して")&&!SlackReplyFormat.Markdown("形式指定なし"),"explicit MD overrides default");
        Check(SlackResultText.OcrOnly("画像をHTMLで再現して")&&SlackResultText.OcrOnly("この表をHTML形式で返してください")&&!SlackResultText.OcrOnly("要点を分析してください"),"format-only OCR needs no interpretation");
        Check(!SlackResultText.OcrOnly("この画像を分析してHTMLで返してください")&&!SlackResultText.OcrOnly("この画像を解析してHTMLで返してください")&&!SlackResultText.OcrOnly("この表を比較してMarkdownで返してください"),"format request never suppresses explicitly requested analysis");
        var html=SlackAnalysisDocument.Html("```html\n<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>body{background:red}</style></head><body><table onclick=\"alert(1)\"><tr><th colspan=\"2\">財務 &amp; 表</th></tr><tr><td>売上</td><td align=\"right\" style=\"background:url(https://evil.invalid)\">1,234円</td></tr></table></body></html>\n```");
        Check(html.StartsWith("<!doctype html>")&&html.Contains("<table>")&&html.Contains("colspan=\"2\"")&&html.Contains("1,234円"),"fenced full HTML becomes real document with merged table");
        Check(!html.Contains("onclick")&&!html.Contains("evil.invalid")&&!html.Contains("background:red")&&html.Contains("Content-Security-Policy"),"only Host metadata CSS and safe attributes survive");
        var mdHtml=SlackAnalysisDocument.Html("# 公開表\n| 項目 | 金額 |\n| --- | ---: |\n| 売上 | 1,234 |\n注記 [推測]候補");
        Check(mdHtml.Contains("<h1>公開表</h1>")&&mdHtml.Contains("<table>")&&mdHtml.Contains("<td>売上</td>")&&mdHtml.Contains("[推測]候補"),"basic Markdown table and uncertainty retained in HTML");
        Check(!SlackAnalysisDocument.Html("![image](https://evil.invalid/x.png)").Contains("<img"),"ordinary text cannot create remote assets");
        Check(SlackAnalysisDocument.Html("<TABLE><TR><TD COLSPAN=\"2\">PUBLIC</TD></TR></TABLE>").Contains("colspan=\"2\""),"case-insensitive HTML preserves merged cells");
        foreach(var raw in new[]{"<table><tr><td>unclosed","<!DOCTYPE html [<!ENTITY secret SYSTEM 'file:///private'>]><p>&secret;</p>","<svg xmlns=\"http://www.w3.org/2000/svg\"><script>bad</script></svg>","<script>alert(1)</script>","<img src=\"https://evil.invalid/x.png\">","<table><tr><td rowspan=\"0\">bad</td></tr></table>"}){
            try{SlackAnalysisDocument.Html(raw);throw new Exception("unsafe/unsupported HTML accepted");}catch(InvalidDataException){checks++;}
            Check(!SlackAnalysisDocument.Markdown(raw).Contains("<script")&&!SlackAnalysisDocument.Markdown(raw).Contains("<img"),"fallback has no active raw HTML");
        }
        const string evidence="""{"text":"PUBLIC 売上 1,234円","pages":[{"page":0,"text":"PUBLIC 売上 1,234円","spans":[],"tables":[{"table_id":"public","rows":1,"columns":2,"cells":[{"row_index":0,"column_index":0,"row_span":1,"column_span":1,"text":"PUBLIC 売上","bounding_box":[0,0,50,10]},{"row_index":0,"column_index":1,"row_span":1,"column_span":1,"text":"1,234円","bounding_box":[50,0,100,10]}]}]}]}""";
        Check(SlackOcrArtifacts.Html(["{\"text\":\"PUBLIC flat\",\"pages\":[]}"]).Contains("PUBLIC flat"),"empty pages cannot drop retained text in default HTML");
        var root=Directory.CreateTempSubdirectory("lb-public-html-format-").FullName;var options=new SlackOptions(true,root,"TTEST","CTEST","UBOT",Path.Combine(root,"app"),Path.Combine(root,"bot"),ReturnFileResults:true);var now=DateTimeOffset.UtcNow;var sequence=0;
        using(var store=new SlackStore(options)){
            string Prepare(string request,bool image=true,string? thread=null,string user="UALICE",string? ocrEvidence=null){
                var ts=now.ToUnixTimeSeconds()+"."+(++sequence).ToString("D6");var id=store.Accept(new("EV-FORMAT-"+sequence,"TTEST","CTEST",user,ts,thread??ts,request,image?["FPUBLIC"]:[],now.AddMilliseconds(sequence)),now.AddSeconds(-1),now.AddMilliseconds(sequence)).JobId!;
                while(store.ClaimReply(DateTimeOffset.UtcNow.AddMinutes(1)) is{} ack)store.ReplySent(ack.Id,ts);
                Check(store.ClaimNext()?.Id==id,"fixture accepted job");if(image){store.SaveAttachment(id,"FPUBLIC",".png",[1,2,3]);store.SaveOcrResult(id,"FPUBLIC",ocrEvidence??evidence);}return id;
            }
            List<SlackStore.ResultFile> Drain(){var all=new List<SlackStore.ResultFile>();while(store.ClaimResultFile(DateTimeOffset.UtcNow.AddMinutes(1)) is{} file){all.Add(file);store.HoldResultFile(file,true);}return all;}
            var first=Prepare("添付画像を文字起こししてください");store.Finish(first,"{\"output_kind\":\"ocr_text\"}",true,true);var files=Drain();
            Check(files.Count==1&&files[0].Filename=="ocr-html-"+first+".html"&&Encoding.UTF8.GetString(files[0].Bytes).Contains("<table>"),"default table OCR is HTML only; no MD or JSON by default");
            var firstThread=files[0].Thread;
            store.QueueSavedOcrText(first);store.Recover();Check(Drain().Count==0,"uncertain frozen HTML never gains MD or JSON on saved-result request/restart");
            var analysis=Prepare("この画像を確認してください");store.Finish(analysis,"{\"interpretation\":\"<p>PUBLIC analysis only</p>\"}",true,true);files=Drain();
            Check(files.Count==1&&files[0].Filename=="analysis-"+analysis+".html"&&Encoding.UTF8.GetString(files[0].Bytes).Contains("PUBLIC analysis only"),"image analysis defaults to its single analysis file without an OCR duplicate or JSON");
            store.Recover();Check(Drain().Count==0,"restart does not add an OCR document to frozen analysis delivery");
            var analysisMd=Prepare("この画像を解析してMarkdownで返してください");store.Finish(analysisMd,"{\"interpretation\":\"PUBLIC requested Markdown\"}",true,true);files=Drain();
            Check(files.Count==1&&files[0].Filename=="analysis-"+analysisMd+".md"&&Encoding.UTF8.GetString(files[0].Bytes).Contains("PUBLIC requested Markdown"),"explicit Markdown analysis stays a single analysis deliverable");
            var analysisFallback=Prepare("この画像を分析してください");store.Finish(analysisFallback,"{\"interpretation\":\"<table><tr><td>PUBLIC image analysis fallback\"}",true,true);files=Drain();
            Check(files.Count==1&&files[0].Filename=="analysis-"+analysisFallback+".md"&&Encoding.UTF8.GetString(files[0].Bytes).Contains("PUBLIC image analysis fallback"),"unsupported analysis HTML returns only Markdown, never an implicit OCR file");
            var analysisJson=Prepare("この画像を分析しJSON形式も添付してください");store.Finish(analysisJson,"{\"interpretation\":\"PUBLIC analysis with requested JSON\"}",true,true);files=Drain();
            Check(files.Count==2&&files.Any(f=>f.Filename=="analysis-"+analysisJson+".html")&&files.Any(f=>f.Filename=="ocr-json-"+analysisJson+".json"),"explicit JSON remains available without an unwanted primary OCR document");
            store.QueueSavedOcrText(analysis);files=Drain();Check(files.Count==1&&files[0].Filename=="ocr-"+analysis+".md","explicit saved-OCR operator request can still export evidence separately");
            var follow=Prepare("HTML形式で返してください",false,firstThread,"UBOB");store.Finish(follow,"```html\n<table><tr><td>PUBLIC follow-up</td></tr></table>\n```",true,true);files=Drain();
            Check(files.Count==1&&files[0].Filename=="analysis-"+follow+".html"&&files[0].Thread==firstThread&&files[0].Channel=="CTEST"&&Encoding.UTF8.GetString(files[0].Bytes).Contains("<td>PUBLIC follow-up</td>"),"second user image-free follow-up returns actual HTML into original shared thread");
            var json=Prepare("構造化JSON形式も添付してください",false,firstThread);store.Finish(json,"PUBLIC continued analysis",true,true);files=Drain();
            Check(files.Count==2&&files.Any(f=>f.Filename.EndsWith(".html"))&&files.Any(f=>f.Filename.EndsWith(".json")&&JsonDocument.Parse(f.Bytes).RootElement.GetProperty("attachments")[0].GetProperty("text").GetString()!.Contains("PUBLIC 売上")),"explicit follow-up JSON exports retained same-session evidence");
            var negative=Prepare("文字起こしをHTML形式で。JSONは不要です");store.Finish(negative,"{\"output_kind\":\"ocr_text\"}",true,true);files=Drain();Check(files.Count==1&&files[0].Filename.EndsWith(".html"),"negative JSON request never adds structured artifact");
            var markdown=Prepare("文字起こしをMarkdown形式で返してください");store.Finish(markdown,"{\"output_kind\":\"ocr_text\"}",true,true);files=Drain();Check(files.Count==1&&files[0].Filename.EndsWith(".md"),"explicit MD preserved without JSON sidecar");
            var fallback=Prepare("HTML形式で返してください",false,firstThread);store.Finish(fallback,"<table><tr><td>PUBLIC malformed",true,true);files=Drain();Check(files.Count==1&&files[0].Filename=="analysis-"+fallback+".md"&&Encoding.UTF8.GetString(files[0].Bytes).Contains("PUBLIC malformed"),"unsupported HTML falls back to MD without dropping result");
            const string uncertainGrid="""{"text":"PUBLIC uncertain table","pages":[{"page":0,"text":"PUBLIC uncertain table","spans":[],"tables":[{"table_id":"public","rows":1,"columns":1,"cells":[{"row_index":0,"column_index":0,"text":"A","bounding_box":[0,0,50,10]},{"row_index":0,"column_index":0,"text":"B","bounding_box":[0,0,50,10]}]}]}]}""";
            var ocrFallback=Prepare("画像を文字起こししてHTMLで返してください",ocrEvidence:uncertainGrid);store.Finish(ocrFallback,"{\"output_kind\":\"ocr_text\"}",true,true);files=Drain();Check(files.Count==1&&files[0].Filename.EndsWith(".md")&&Encoding.UTF8.GetString(files[0].Bytes).Contains("PUBLIC uncertain table"),"unsupported OCR table falls back to retained readable MD, no implicit JSON");
            var rejected=Prepare("画像を文字起こししてください");store.Finish(rejected,"{\"output_kind\":\"ocr_text\"}",true,true);var rejectedFile=store.ClaimResultFile(DateTimeOffset.UtcNow.AddMinutes(1))!;store.HoldResultFile(rejectedFile,false,"invalid_arguments");Check(store.RetryRejectedOcrTicket(rejected)=="queued","known rejected default HTML ticket remains recoverable");files=Drain();Check(files.Count==1&&files[0].Filename==rejectedFile.Filename&&files[0].Bytes.SequenceEqual(rejectedFile.Bytes),"HTML ticket recovery keeps filename destination and exact frozen bytes");
            var tampered=Prepare("画像を文字起こししてください");store.Finish(tampered,"{\"output_kind\":\"ocr_text\"}",true,true);var original=store.ClaimResultFile(DateTimeOffset.UtcNow.AddMinutes(1))!;store.HoldResultFile(original,false,"invalid_arguments");
            using(var db=new SqliteConnection("Data Source="+Path.Combine(root,"slack.db"))){db.Open();using var q=db.CreateCommand();q.CommandText="UPDATE result_files SET bytes=$bytes,hash=$hash WHERE id=$id";var bytes=Encoding.UTF8.GetBytes("PUBLIC TAMPER");q.Parameters.AddWithValue("$bytes",bytes);q.Parameters.AddWithValue("$hash",SlackHash.Of(bytes));q.Parameters.AddWithValue("$id",original.Id);q.ExecuteNonQuery();}
            try{store.RetryRejectedOcrTicket(tampered);throw new Exception("source/hash binding weakened");}catch(InvalidDataException){checks++;}
            using(var noticeDb=new SqliteConnection("Data Source="+Path.Combine(root,"slack.db"))){noticeDb.Open();using var q=noticeDb.CreateCommand();q.CommandText="SELECT count(*) FROM replies WHERE job_id=$job AND kind='progress' AND text LIKE '%Markdownで返します%'";q.Parameters.AddWithValue("$job",fallback);Check(Convert.ToInt64(q.ExecuteScalar())==1,"one durable fallback notice even after later ACK draining");}
            var ordinary=Prepare("こんにちは",false);store.Finish(ordinary,"PUBLIC hello",true,false);Check(Drain().Count==0,"ordinary non-OCR conversation remains Slack text");
            var explicitHtml=Prepare("HTML形式で返してください",false);store.Finish(explicitHtml,"PUBLIC document",true,false);files=Drain();Check(files.Count==1&&files[0].Filename.EndsWith(".html"),"explicit HTML works without an image or sensitive history");
            var foreign=Prepare("JSON形式も添付してください",false);store.Finish(foreign,"PUBLIC other thread",true,true);files=Drain();Check(files.Count==1&&files[0].Filename.EndsWith(".html"),"other thread cannot export another session OCR");
        }
        var tokenCalls=0;using var api=new SlackApi(new TicketHandler(()=>tokenCalls++),"xapp-PUBLIC","xoxb-PUBLIC");
        foreach(var ext in new[]{"html","md","txt"}){await api.ResultUploadTicket("analysis-"+new string('a',32)+"."+ext,12,CancellationToken.None);checks++;}
        Check(tokenCalls==3,"analysis output extensions accepted by real upload argument validator");
        Console.WriteLine(JsonSerializer.Serialize(new{slack_reply_format_checks=checks,status="PASS",real_slack=false,model_or_ocr_runs=0,private_data=false}));
    }
    private sealed class TicketHandler(Action call):HttpMessageHandler{
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){call();return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"ok\":true,\"file_id\":\"FPUBLIC\",\"upload_url\":\"https://files.slack.com/upload/v1/PUBLIC\"}")});}
    }
}
