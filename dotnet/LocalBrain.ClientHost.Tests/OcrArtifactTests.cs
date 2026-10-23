using System.Text.Json;
using LocalBrain.ClientHost.Slack;
using LocalBrain.ClientHost.Ocr;
internal static class OcrArtifactTests {
 internal static void Run(string? input=null,string? output=null){
  var checks=0;void Check(bool value,string name){if(!value)throw new Exception("OCR_ARTIFACT_TEST:"+name);checks++;}
  var page=new {page=0,needs_review=true,spans=new[]{new{span_id="s1",text="注記<script>alert(1)</script>",bounding_box=new[]{0,100,200,130},confidence=.9,needs_review=true,unreadable=false}},tables=new[]{new{table_id="table_0",rows=2,columns=2,cells=new object[]{new{row_index=0,column_index=0,row_span=1,column_span=2,text="<img src=https://public.invalid/x onerror=alert(1)>",bounding_box=new[]{0,0,200,40},needs_review=true},new{row_index=1,column_index=0,row_span=1,column_span=1,text="=1+1",bounding_box=new[]{0,40,100,80}},new{row_index=1,column_index=1,row_span=1,column_span=1,text="1,234.5%",bounding_box=new[]{100,40,200,80}}}}}};
  var raw=JsonSerializer.Serialize(new{text="PUBLIC",pages=new[]{page},private_local_path="MUST_NOT_EXPORT"});var data=new[]{raw};
  var html=SlackOcrArtifacts.Html(data);Check(html.Contains("colspan=\"2\"")&&html.Contains("rowspan=\"1\""),"merged cells preserved");Check(!html.Contains("<img")&&!html.Contains("<script")&&html.Contains("&lt;script&gt;"),"OCR markup escaped");Check(html.Contains("default-src 'none'")&&!html.Contains("<link")&&!html.Contains("<script"),"static offline HTML");Check(html.Contains("number review"),"numeric alignment");
  var json=SlackOcrArtifacts.Json(data);Check(!json.Contains("MUST_NOT_EXPORT")&&json.Contains("attachments"),"canonical fields only");Check(SlackOcrArtifacts.HasTables(data),"table detection");
  var csv=SlackOcrArtifacts.Csv(data);Check(csv.Contains("\"'=1+1\"")&&csv.Contains("\"1,234.5%\""),"CSV formula neutralization and quotes");
  var md=SlackOcrMarkdown.Render(data);Check(md.Contains("| --- | --- |")&&md.Contains("結合セル"),"real markdown table with merged note");
  using(var d=JsonDocument.Parse("{\"error_type\":\"PRIVATE_TEXT\"}"))Check(OcrWorkerProcess.DiagnosticType(d.RootElement)=="Other","worker arbitrary exception type not persisted");
  using(var d=JsonDocument.Parse("{\"error_type\":\"OnnxMemory\"}"))Check(OcrWorkerProcess.DiagnosticType(d.RootElement)=="OnnxMemory","fixed exception category persisted");
  if(input is not null&&output is not null){Directory.CreateDirectory(output);foreach(var file in Directory.EnumerateFiles(input,"financial*.json").Where(p=>!p.EndsWith(".receipt.json"))){
   var value=File.ReadAllText(file);var root=JsonSerializer.Serialize(new{job_id=new string('a',32),input_file_ref="ocr:"+new string('a',32)+"/input",input_sha256=JsonSerializer.Deserialize<JsonElement>(value).GetProperty("input_sha256").GetString(),profile="TABLE_LAYOUT",status="complete",text="PUBLIC",external_ai="deny",automatic_knowledge_adoption=false,needs_review=true,pages=new[]{JsonSerializer.Deserialize<JsonElement>(value)}});
   var values=new[]{root};var stem=Path.GetFileNameWithoutExtension(file);File.WriteAllText(Path.Combine(output,stem+".html"),SlackOcrArtifacts.Html(values));File.WriteAllText(Path.Combine(output,stem+".md"),SlackOcrMarkdown.Render(values));File.WriteAllText(Path.Combine(output,stem+".json"),SlackOcrArtifacts.Json(values));File.WriteAllText(Path.Combine(output,stem+".csv"),SlackOcrArtifacts.Csv(values));checks++;
  }}
  var deliveryRoot=Directory.CreateTempSubdirectory("lb-artifact-public-").FullName;var options=new SlackOptions(true,deliveryRoot,"TTEST","CTEST","UBOT",Path.Combine(deliveryRoot,"app"),Path.Combine(deliveryRoot,"bot"),ReturnFileResults:true);
  using(var store=new SlackStore(options)){
   var now=DateTimeOffset.UtcNow;var ts=now.ToUnixTimeSeconds()+".000001";var job=store.Accept(new("EV-ARTIFACT","TTEST","CTEST","UALICE",ts,ts,"文字起こしを HTML CSV JSON で返してください",["FPUBLIC"],now),now.AddSeconds(-1),now).JobId!;
   while(store.ClaimReply(now) is {} ack)store.ReplySent(ack.Id,ts);store.ClaimNext();store.SaveAttachment(job,"FPUBLIC",".png",[1,2,3]);store.SaveOcrResult(job,"FPUBLIC",raw);store.Finish(job,"{\"output_kind\":\"ocr_text\"}",true,true);
   var formats=new HashSet<string>();while(store.ClaimResultFile(now) is {} file){Check(file.Channel=="CTEST"&&file.Thread==ts,"each artifact bound to exact original destination");formats.Add(Path.GetExtension(file.Filename));store.HoldResultFile(file,false,"public_fixture_hold");}
   Check(formats.SetEquals(new[]{".md",".json",".html",".csv"}),"requested formats queued once");store.QueueSavedOcrText(job);Check(store.ClaimResultFile(now) is null,"held files not recreated");
  }
  Console.WriteLine(JsonSerializer.Serialize(new{ocr_artifact_checks=checks,status="PASS",real_slack=false,private_data=false}));
 }
}
