using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace LocalBrain.ClientHost.Slack;
// Deterministic projection of local evidence. No model, script, URL, or external asset.
internal static class SlackOcrArtifacts {
 static string S(JsonElement e,string key)=>e.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString()!:"";
 static int N(JsonElement e,string key,int fallback=0)=>e.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.Number&&p.TryGetInt32(out var n)?n:fallback;
 static string H(string s)=>WebUtility.HtmlEncode(s).Replace("\r","").Replace("\n","<br>");
 static bool B(JsonElement e,string k)=>e.TryGetProperty(k,out var p)&&p.ValueKind==JsonValueKind.True;
 static string Reading(JsonElement e)=>B(e,"unreadable")&&!string.IsNullOrWhiteSpace(S(e,"hypothesis"))?"[推測]"+S(e,"hypothesis"):S(e,"text");
 internal static bool HasTables(IEnumerable<string> results){foreach(var raw in results){using var d=JsonDocument.Parse(raw);if(d.RootElement.TryGetProperty("pages",out var pages)&&pages.EnumerateArray().Any(p=>p.TryGetProperty("tables",out var t)&&t.GetArrayLength()>0))return true;}return false;}
 internal static string Json(IEnumerable<string> results){
  var values=new List<JsonElement>();
  foreach(var raw in results){using var d=JsonDocument.Parse(raw);var r=d.RootElement;
   // Only the canonical OCR result contract is exported. Never include filesystem paths or receipts.
   var safe=new Dictionary<string,JsonElement>();foreach(var key in new[]{"job_id","input_file_ref","input_sha256","profile","experimental","status","text","pages","needs_review","external_ai","automatic_knowledge_adoption"})if(r.TryGetProperty(key,out var value))safe[key]=value.Clone();
   values.Add(JsonSerializer.SerializeToElement(safe));
  }
  return JsonSerializer.Serialize(new{schema="localbrain.ocr.artifacts.v1",unverified=true,attachments=values},new JsonSerializerOptions{WriteIndented=true});
 }
 internal static string Html(IEnumerable<string> results){
  var b=new StringBuilder("<!doctype html><html lang=\"ja\"><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><title>OCR結果（要確認）</title><style>body{font-family:sans-serif;line-height:1.6;margin:2em}table{border-collapse:collapse;margin:1em 0}td,th{border:1px solid #666;padding:.4em}.number{text-align:right}.review{background:#fff2cc}</style><body><h1>OCR結果（未検証）</h1><p>罫線・セル対応と読み取りは要確認。先頭行は見出しとして表示します。推測は確定した文字ではありません。</p>");
  var attachment=0;
  foreach(var raw in results){using var d=JsonDocument.Parse(raw);b.Append("<h2>添付 ").Append(++attachment).Append("</h2>");
   if(!d.RootElement.TryGetProperty("pages",out var pages)||pages.ValueKind!=JsonValueKind.Array||pages.GetArrayLength()==0){b.Append("<p>").Append(H(S(d.RootElement,"text"))).Append("</p>");continue;}
   foreach(var page in pages.EnumerateArray()){
    b.Append("<h3>ページ ").Append(N(page,"page")+1).Append("</h3>");var mapped=new HashSet<(string,int,int)>();
    if(page.TryGetProperty("tables",out var tables))foreach(var table in tables.EnumerateArray()){
     var rows=N(table,"rows");var cols=N(table,"columns");if(rows<1||cols<1||rows>1000||cols>100||rows*cols>10000)throw new InvalidDataException("SLACK_TABLE_SIZE");
     var occupied=new bool[rows,cols];var cells=new Dictionary<(int,int),JsonElement>();var id=S(table,"table_id");
     foreach(var cell in table.GetProperty("cells").EnumerateArray()){
      var r=N(cell,"row_index",-1);var c=N(cell,"column_index",-1);var rs=N(cell,"row_span",1);var cs=N(cell,"column_span",1);
      if(r<0||c<0||r>=rows||c>=cols||rs<1||cs<1||rs>rows-r||cs>cols-c)throw new InvalidDataException("SLACK_TABLE_CELL");
      for(var y=r;y<r+rs;y++)for(var x=c;x<c+cs;x++){if(occupied[y,x])throw new InvalidDataException("SLACK_TABLE_OVERLAP");occupied[y,x]=true;}
      cells.Add((r,c),cell);mapped.Add((id,r,c));
     }
     b.Append("<table><caption>").Append(H(id)).Append("（構造は要確認）</caption>");
     for(var r=0;r<rows;r++){b.Append("<tr>");for(var c=0;c<cols;c++){
      if(!cells.TryGetValue((r,c),out var cell)){if(!occupied[r,c])b.Append("<td class=\"review\">[構造未確認]</td>");continue;}
      var text=Reading(cell);var number=Regex.IsMatch(text,@"^[\s¥￥$€+−\-()0-9０-９,.，．%％円]+$");var tag=r==0?"th":"td";
      b.Append('<').Append(tag).Append(" class=\"").Append(number?"number review":"review").Append("\" rowspan=\"").Append(N(cell,"row_span",1)).Append("\" colspan=\"").Append(N(cell,"column_span",1)).Append("\">").Append(H(text));
      if(B(cell,"unreadable")&&cell.TryGetProperty("hypotheses",out var hypotheses))foreach(var guess in hypotheses.EnumerateArray())if(guess.ValueKind==JsonValueKind.String)b.Append("<br>[推測]").Append(H(guess.GetString()!));
      b.Append("</").Append(tag).Append('>');
     }b.Append("</tr>");}b.Append("</table>");
    }
    if(page.TryGetProperty("spans",out var spans))foreach(var span in spans.EnumerateArray()){
     if(mapped.Contains((S(span,"table_id"),N(span,"row_index",-1),N(span,"column_index",-1))))continue;
     b.Append("<p>").Append(H(Reading(span))).Append(B(span,"needs_review")?" [要確認]":"").Append("</p>");
    }
   }
  }
  b.Append("</body></html>");return Checked(b.ToString());
 }
 internal static string Csv(IEnumerable<string> results){var b=new StringBuilder();var index=0;foreach(var raw in results){using var d=JsonDocument.Parse(raw);if(!d.RootElement.TryGetProperty("pages",out var pages))continue;foreach(var page in pages.EnumerateArray())if(page.TryGetProperty("tables",out var tables))foreach(var table in tables.EnumerateArray()){
  b.Append("# table ").Append(++index).Append("; merged content in top-left cell; verify source\r\n");var rows=N(table,"rows");var cols=N(table,"columns");if(rows<1||cols<1||rows>1000||cols>100||rows*cols>10000)throw new InvalidDataException("SLACK_TABLE_SIZE");var matrix=new string[rows,cols];
  foreach(var cell in table.GetProperty("cells").EnumerateArray()){var r=N(cell,"row_index",-1);var c=N(cell,"column_index",-1);if(r<0||r>=rows||c<0||c>=cols)throw new InvalidDataException("SLACK_TABLE_CELL");matrix[r,c]=Reading(cell);}
  for(var r=0;r<rows;r++){for(var c=0;c<cols;c++){if(c>0)b.Append(',');var v=matrix[r,c]??"";if(v.TrimStart().StartsWith('=')||v.TrimStart().StartsWith('+')||v.TrimStart().StartsWith('-')||v.TrimStart().StartsWith('@')||v.StartsWith('\t')||v.StartsWith('\r'))v="'"+v;b.Append('"').Append(v.Replace("\"","\"\"")).Append('"');}b.Append("\r\n");}
 }}return Checked(b.ToString());}
 static string Checked(string value){_=SlackResultText.Utf8(value);return value;}
}
