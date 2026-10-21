using System.Text;
using System.Text.Json;

namespace LocalBrain.ClientHost.Slack;

// Pure local projection of retained OCR evidence. Never asks a model to invent layout or text.
internal static class SlackOcrMarkdown
{
    private sealed record Box(double X,double Y,double Right,double Bottom);
    private sealed record Span(string Text,Box Bounds,bool Review,string? Table,int Row,int Column);
    private sealed record Block(Box Bounds,string Markdown);
    private sealed record Grid(string Id,Box Bounds,string Markdown,HashSet<(int,int)> Cells);
    private static string Text(JsonElement e,string key)=>e.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString()!:"";
    private static string Reading(JsonElement span)
    {
        var text=Text(span,"text");var candidate=Text(span,"hypothesis");
        // A retained local OCR hypothesis is a guess, never silently promote it to a transcription.
        if(Flag(span,"unreadable")&&!string.IsNullOrWhiteSpace(candidate)&&candidate!="[判読不能]")return "[推測]"+candidate;
        return text;
    }
    private static bool Flag(JsonElement e,string key)=>e.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.True;
    private static int Number(JsonElement e,string key,int fallback=-1)=>e.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.Number&&p.TryGetInt32(out var n)?n:fallback;
    private static Box? Bounds(JsonElement e)
    {
        if(!e.TryGetProperty("bounding_box",out var a)||a.ValueKind!=JsonValueKind.Array||a.GetArrayLength()!=4)return null;
        var n=new double[4];var i=0;foreach(var p in a.EnumerateArray())if(p.ValueKind!=JsonValueKind.Number||!p.TryGetDouble(out n[i])||!double.IsFinite(n[i++]))return null;
        return n[2]>n[0]&&n[3]>n[1]?new(n[0],n[1],n[2],n[3]):null;
    }
    // Escape document markup, including remote images/links and raw HTML. Only renderer markup is active.
    internal static string Escape(string value)
    {
        var b=new StringBuilder();
        foreach(var ch in value.Replace("\r\n","\n").Replace('\r','\n')){
            if(ch=='&')b.Append("&amp;");else if(ch=='<')b.Append("&lt;");else if(ch=='>')b.Append("&gt;");
            else if(ch=='\n')b.Append("<br>");else if(ch=='\t')b.Append("    ");
            else if("\\`*_{}[]()#+-.!|~".Contains(ch))b.Append('\\').Append(ch);
            else if(!char.IsControl(ch))b.Append(ch);
        }
        return b.ToString();
    }
    private static void Add(StringBuilder b,string text){b.Append(text);if(b.Length>1024*1024)throw new InvalidDataException("SLACK_RESULT_FILE_SIZE");}
    internal static string Render(IEnumerable<string> results)
    {
        var b=new StringBuilder("# OCR結果\n\n> 画像から読み取った文字・罫線表の再構成です。読み順は推定で、色・書体・写真・厳密な配置は再現しません。内容は未検証です。読めない文字にOCR候補がある場合は［推測］を付け、候補がない場合は［判読不能］を残します。\n\n");
        var attachment=0;
        foreach(var raw in results){
            using var doc=JsonDocument.Parse(raw);var root=doc.RootElement;
            Add(b,$"## 添付 {++attachment}\n\n");
            if(root.TryGetProperty("pages",out var pages)&&pages.ValueKind==JsonValueKind.Array&&pages.GetArrayLength()>0){
                var pageNumber=0;foreach(var page in pages.EnumerateArray()){
                    Add(b,$"### ページ {++pageNumber}\n\n");
                    if(Flag(page,"needs_review"))Add(b,"> 要確認：文字の認識や表の構造に不確かな箇所があります。\n\n");
                    RenderPage(b,page);
                }
            }else Add(b,Escape(Text(root,"text")).Replace("<br>","  \n")+"\n\n");
        }
        _=SlackResultText.Utf8(b.ToString());return b.ToString();
    }
    private static void RenderPage(StringBuilder b,JsonElement page)
    {
        var grids=new List<Grid>();
        if(page.TryGetProperty("tables",out var tables)&&tables.ValueKind==JsonValueKind.Array)
            foreach(var table in tables.EnumerateArray())if(Table(table,page) is {} grid)grids.Add(grid);
        var blocks=new List<Block>();var missing=new List<string>();var spans=new List<Span>();
        if(page.TryGetProperty("spans",out var items)&&items.ValueKind==JsonValueKind.Array){
            foreach(var item in items.EnumerateArray()){
                var sourceText=Text(item,"text");var text=Reading(item);if(text.Length==0)continue;
                var bounds=Bounds(item);var table=Text(item,"table_id");var row=Number(item,"row_index");var col=Number(item,"column_index");
                // Suppress duplicate table text only when the rendered cell contains this exact OCR span.
                if(grids.Any(g=>g.Id==table&&g.Cells.Contains((row,col)))&&CellContains(tables,table,row,col,sourceText))continue;
                var review=Flag(item,"needs_review")||Flag(item,"unreadable");
                if(bounds is null)missing.Add(Escape(text)+(review?" **[要確認]**":""));
                else spans.Add(new(text,bounds,review,table,row,col));
            }
        }
        // Group visually aligned spans into lines; use position for reading order without fabricating headings.
        var lines=new List<List<Span>>();
        foreach(var span in spans.OrderBy(s=>s.Bounds.Y).ThenBy(s=>s.Bounds.X)){
            var line=lines.LastOrDefault(l=>Math.Abs(l[0].Bounds.Y-span.Bounds.Y)<=Math.Min(l[0].Bounds.Bottom-l[0].Bounds.Y,span.Bounds.Bottom-span.Bounds.Y)*0.4);
            if(line is null)lines.Add([span]);else line.Add(span);
        }
        foreach(var line in lines){
            var box=new Box(line.Min(s=>s.Bounds.X),line.Min(s=>s.Bounds.Y),line.Max(s=>s.Bounds.Right),line.Max(s=>s.Bounds.Bottom));
            blocks.Add(new(box,string.Join("　",line.OrderBy(s=>s.Bounds.X).Select(s=>Escape(s.Text)+(s.Review?" **[要確認]**":"")))));
        }
        blocks.AddRange(grids.Select(g=>new Block(g.Bounds,"\n"+g.Markdown+"\n")));
        if(blocks.Count==0&&missing.Count==0){Add(b,Escape(Text(page,"text")).Replace("<br>","  \n")+"\n\n");return;}
        foreach(var block in blocks.OrderBy(x=>x.Bounds.Y).ThenBy(x=>x.Bounds.X))Add(b,block.Markdown+"  \n");
        if(missing.Count>0){Add(b,"\n#### 位置を特定できなかった文字\n\n");foreach(var text in missing)Add(b,text+"  \n");}
        Add(b,"\n");
    }
    private static bool CellContains(JsonElement tables,string id,int row,int column,string text)
    {
        foreach(var table in tables.EnumerateArray())if(Text(table,"table_id")==id&&table.TryGetProperty("cells",out var cells))
            foreach(var cell in cells.EnumerateArray())if(Number(cell,"row_index")==row&&Number(cell,"column_index")==column&&Text(cell,"text").Contains(text,StringComparison.Ordinal))return true;
        return false;
    }
    private static Grid? Table(JsonElement table,JsonElement page)
    {
        var rows=Number(table,"rows");var columns=Number(table,"columns");var id=Text(table,"table_id");
        if(rows<1||columns<1||rows>1000||columns>100||rows*columns>10000||id.Length==0||!table.TryGetProperty("cells",out var cells)||cells.ValueKind!=JsonValueKind.Array)return null;
        var matrix=new string[rows,columns];var occupied=new bool[rows,columns];var keys=new HashSet<(int,int)>();var notes=new List<string>();Box? bounds=null;
        foreach(var cell in cells.EnumerateArray()){
            var r=Number(cell,"row_index");var c=Number(cell,"column_index");var rs=Number(cell,"row_span",1);var cs=Number(cell,"column_span",1);var box=Bounds(cell);
            if(r<0||c<0||rs<1||cs<1||r>=rows||c>=columns||rs>rows-r||cs>columns-c||box is null)return null;
            for(var y=r;y<r+rs;y++)for(var x=c;x<c+cs;x++){if(occupied[y,x])return null;occupied[y,x]=true;}
            var review=false;
            if(page.TryGetProperty("spans",out var spans)&&spans.ValueKind==JsonValueKind.Array)
                review=spans.EnumerateArray().Any(s=>Text(s,"table_id")==id&&Number(s,"row_index")==r&&Number(s,"column_index")==c&&(Flag(s,"needs_review")||Flag(s,"unreadable")));
            var cellText=Text(cell,"text");
            if(page.TryGetProperty("spans",out var sourceSpans)&&sourceSpans.ValueKind==JsonValueKind.Array){
                var members=sourceSpans.EnumerateArray().Where(s=>Text(s,"table_id")==id&&Number(s,"row_index")==r&&Number(s,"column_index")==c).ToArray();
                if(members.Length>0&&string.Join(" ",members.Select(s=>Text(s,"text")))==cellText)cellText=string.Join(" ",members.Select(Reading));
                else foreach(var member in members.Where(s=>Reading(s)!=Text(s,"text")))notes.Add($"- 行{r+1}・列{c+1}の参考候補：{Escape(Reading(member))}\n");
            }
            matrix[r,c]=Escape(cellText)+(review?" **[要確認]**":"");keys.Add((r,c));
            if(rs>1||cs>1)notes.Add($"- 行{r+1}・列{c+1}：{rs}行×{cs}列の結合セル（内容は左上セルに記載）\n");
            bounds=bounds is null?box:new(Math.Min(bounds.X,box.X),Math.Min(bounds.Y,box.Y),Math.Max(bounds.Right,box.Right),Math.Max(bounds.Bottom,box.Bottom));
        }
        if(bounds is null)return null;
        var output=new StringBuilder();
        output.AppendLine("| "+string.Join(" | ",Enumerable.Range(1,columns).Select(i=>"列"+i))+" |");
        output.AppendLine("| "+string.Join(" | ",Enumerable.Repeat("---",columns))+" |");
        for(var r=0;r<rows;r++)output.AppendLine("| "+string.Join(" | ",Enumerable.Range(0,columns).Select(c=>matrix[r,c]??""))+" |");
        output.AppendLine("\n> 表の罫線構造は要確認。列名は位置を示す補助ラベルです。");
        foreach(var note in notes)output.Append(note);
        return new(id,bounds,output.ToString(),keys);
    }
}
