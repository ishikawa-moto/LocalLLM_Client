using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace LocalBrain.ClientHost.Slack;

// Format selection is local Host policy. Documents/models never select paths or upload destinations.
internal static class SlackReplyFormat
{
    private const RegexOptions Flags=RegexOptions.IgnoreCase|RegexOptions.CultureInvariant;
    private static string Pattern(string format)=>format switch{
        "md"=>@"(?<![A-Za-z0-9_])(?:markdown|md)(?![A-Za-z0-9_])|マークダウン",
        _=>@"(?<![A-Za-z0-9_])"+Regex.Escape(format)+@"(?![A-Za-z0-9_])"
    };
    internal static bool Requested(string request,string format)
    {
        foreach(Match match in Regex.Matches(request,Pattern(format),Flags)){
            var tail=request[(match.Index+match.Length)..];
            var end=tail.IndexOfAny(['。','\n','！','？']);if(end>=0)tail=tail[..end];
            if(tail.Length>48)tail=tail[..48];
            // Adjacent Japanese characters are valid format suffixes. Latin word boundaries are not.
            var prefix=request[..match.Index];if(prefix.Length>48)prefix=prefix[^48..];
            if(Regex.IsMatch(prefix,@"(?:\bno|\bwithout|\bexclude|\bdo not (?:include|attach|return|send))\s*$",Flags))continue;
            if(Regex.IsMatch(tail,@"^\s*(?:形式|ファイル)?\s*(?:の?(?:添付|出力|返信|返却|送信))?\s*(?:(?:は|を|も|で|の)?\s*)?(?:不要|なし|いらない|除外|禁止|ではなく|じゃなく|返さ|出力しな|添付しな|is not needed|not required)",Flags))continue;
            if(Regex.IsMatch(tail,@"^\s*(?:とは|について|の(?:意味|説明|仕様)|を(?:説明|解説)|って)",Flags))continue;
            if(Regex.IsMatch(tail,@"^\s*(?:形式|ファイル|で|に|だけ|のみ|も|、|,|と|や|/|・|format\b|file\b|$)|^.{0,16}(?:返して|返す|返答|出力|添付|保存|提供|再現|が必要)",Flags))return true;
        }
        return false;
    }
    internal static bool Markdown(string request)=>Requested(request,"md")&&!Requested(request,"html");
    internal static bool ExplicitDocument(string request)=>Requested(request,"html")||Requested(request,"md");
}

internal static class SlackAnalysisDocument
{
    private static readonly HashSet<string> Tags=new(StringComparer.Ordinal){"p","div","span","section","article","header","footer","h1","h2","h3","h4","h5","h6","table","caption","thead","tbody","tfoot","tr","th","td","ul","ol","li","dl","dt","dd","pre","code","strong","b","em","i","u","s","blockquote","br","hr","sub","sup"};
    private static string Encode(string value)=>WebUtility.HtmlEncode(value);
    private static string? Attribute(XElement element,string name)=>element.Attributes().FirstOrDefault(a=>a.Name.NamespaceName.Length==0&&a.Name.LocalName.Equals(name,StringComparison.OrdinalIgnoreCase))?.Value;
    private static string Unfence(string value){
        var trimmed=value.Trim();var match=Regex.Match(trimmed,@"\A```(?:html|HTML)?\s*\r?\n(?<body>[\s\S]*)\r?\n```\z",RegexOptions.CultureInvariant);
        return match.Success?match.Groups["body"].Value:trimmed;
    }
    internal static string Html(string value)
    {
        var body=Unfence(value);_=SlackResultText.Utf8(body);
        string rendered;
        if(Regex.IsMatch(body,@"<\s*(?:!doctype|[A-Za-z][A-Za-z0-9]*)(?:\s|>|/)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)){
            body=Regex.Replace(body,@"\A\s*<!doctype\s+html\s*>","",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            // XML is deliberately strict. Normal HTML void tags are normalized; malformed markup falls back to MD.
            body=Regex.Replace(body,@"<(br|hr|meta)\b([^<>]*?)(?<!/)>","<$1$2 />",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            body=Regex.Replace(body,@"&([A-Za-z][A-Za-z0-9]+);",m=>Encode(WebUtility.HtmlDecode(m.Value)),RegexOptions.CultureInvariant);
            try{
                using var reader=XmlReader.Create(new StringReader("<root>"+body+"</root>"),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1024*1024+100});
                var root=XElement.Load(reader,LoadOptions.PreserveWhitespace);var output=new StringBuilder();var count=0;
                foreach(var node in root.Nodes())Write(node,output,0,ref count);rendered=output.ToString();
            }catch(XmlException){throw new InvalidDataException("SLACK_HTML_UNSUPPORTED");}
        }else rendered=Plain(body);
        var html="<!doctype html><html lang=\"ja\"><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><title>解析結果</title><style>body{font-family:sans-serif;line-height:1.6;margin:2em}table{border-collapse:collapse;margin:1em 0}th,td{border:1px solid #666;padding:.4em}pre{white-space:pre-wrap;overflow-wrap:anywhere}.number{text-align:right}</style></head><body>"+rendered+"</body></html>";
        _=SlackResultText.Utf8(html);return html;
    }
    private static void Write(XNode node,StringBuilder output,int depth,ref int count)
    {
        if(depth>64||++count>25000)throw new InvalidDataException("SLACK_HTML_UNSUPPORTED");
        if(node is XText text){output.Append(Encode(text.Value));return;}
        if(node is not XElement element)return;
        if(element.Name.NamespaceName.Length!=0)throw new InvalidDataException("SLACK_HTML_UNSUPPORTED");
        var tag=element.Name.LocalName.ToLowerInvariant();
        if(tag=="head")return; // Only Host-owned metadata/CSS/CSP is ever emitted.
        if(tag is "html" or "body"){foreach(var child in element.Nodes())Write(child,output,depth+1,ref count);return;}
        if(tag=="a")tag="span"; // Preserve text; never copy navigation/upload/resource URLs.
        if(!Tags.Contains(tag))throw new InvalidDataException("SLACK_HTML_UNSUPPORTED");
        output.Append('<').Append(tag);
        foreach(var key in new[]{"rowspan","colspan"}){
            if(tag is not("td" or "th"))break;
            if(Attribute(element,key) is{} attribute){if(!int.TryParse(attribute,NumberStyles.None,CultureInfo.InvariantCulture,out var n)||n is <1 or >1000)throw new InvalidDataException("SLACK_HTML_UNSUPPORTED");output.Append(' ').Append(key).Append("=\"").Append(n).Append('"');}
        }
        if(string.Equals(Attribute(element,"align"),"right",StringComparison.OrdinalIgnoreCase)||Attribute(element,"class")=="number")output.Append(" class=\"number\"");
        output.Append('>');foreach(var child in element.Nodes())Write(child,output,depth+1,ref count);
        if(tag is not("br" or "hr"))output.Append("</").Append(tag).Append('>');
    }
    private static string Plain(string body)
    {
        var lines=body.Replace("\r\n","\n").Split('\n');var output=new StringBuilder();
        for(var i=0;i<lines.Length;i++){
            if(i+1<lines.Length&&lines[i].Trim().StartsWith('|')&&Regex.IsMatch(lines[i+1].Trim(),@"^\|?\s*:?-{3,}:?\s*(?:\|\s*:?-{3,}:?\s*)+\|?$")){
                string[] Cells(string line)=>line.Trim().Trim('|').Split('|').Select(s=>s.Trim()).ToArray();
                var headers=Cells(lines[i]);output.Append("<table><thead><tr>");foreach(var cell in headers)output.Append("<th>").Append(Encode(cell)).Append("</th>");output.Append("</tr></thead><tbody>");i+=2;
                for(;i<lines.Length&&lines[i].Trim().StartsWith('|');i++){var cells=Cells(lines[i]);if(cells.Length!=headers.Length)throw new InvalidDataException("SLACK_HTML_UNSUPPORTED");output.Append("<tr>");foreach(var cell in cells)output.Append("<td>").Append(Encode(cell)).Append("</td>");output.Append("</tr>");}
                output.Append("</tbody></table>");i--;
            }else{var heading=Regex.Match(lines[i],@"^(#{1,6})\s+(.+)$");if(heading.Success){var n=heading.Groups[1].Length;output.Append("<h").Append(n).Append('>').Append(Encode(heading.Groups[2].Value)).Append("</h").Append(n).Append('>');}else output.Append("<p>").Append(Encode(lines[i])).Append("</p>");}
        }
        return output.ToString();
    }
    internal static string Markdown(string value)=>"# 解析結果\n\n"+SlackOcrMarkdown.Escape(Unfence(value)).Replace("<br>","  \n")+"\n";
}
