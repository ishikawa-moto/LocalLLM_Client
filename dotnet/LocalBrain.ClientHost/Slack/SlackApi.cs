using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

namespace LocalBrain.ClientHost.Slack;

internal sealed class SlackRateLimit(int seconds) : Exception("SLACK_RATE_LIMIT") { internal int Seconds => Math.Clamp(seconds,1,3600); }
internal sealed class SlackRejected(string code) : Exception("SLACK_API_REJECTED") { internal string Code=>code; }
internal sealed class SlackApi : IDisposable
{
    private readonly HttpClient http;
    private readonly string appToken,botToken;
    internal SlackApi(SlackOptions options):this(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false},SlackOptions.Secret(options.AppTokenFile,"xapp-"),SlackOptions.Secret(options.BotTokenFile,"xoxb-")){}
    internal SlackApi(HttpMessageHandler handler,string appToken,string botToken){http=new(handler){Timeout=TimeSpan.FromSeconds(20)};this.appToken=appToken;this.botToken=botToken;}
    internal async Task<JsonElement> Call(string method,object body,bool app,CancellationToken token)
    {
        if(method is not("apps.connections.open" or "auth.test" or "conversations.info" or "files.info" or "chat.postMessage" or "files.getUploadURLExternal" or "files.completeUploadExternal"))throw new UnauthorizedAccessException("SLACK_METHOD_DENIED");
        // Slack's read endpoints reject JSON POST arguments in the live API.
        // Tokens remain exclusively in the Authorization header, never the URL.
        var read=method is "conversations.info" or "files.info";
        var url="https://slack.com/api/"+method;
        if(read){
            var name=method=="conversations.info"?"channel":"file";
            var value=JsonSerializer.SerializeToElement(body).GetProperty(name).GetString()??throw new InvalidDataException("SLACK_READ_ARGUMENT_REQUIRED");
            url+="?"+name+"="+Uri.EscapeDataString(value);
        }
        using var request=new HttpRequestMessage(read?HttpMethod.Get:HttpMethod.Post,url);
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",app?appToken:botToken);
        if(method=="files.getUploadURLExternal"){
            // The live endpoint rejects JSON arguments on some Slack installations.
            // Use the officially supported form contract; the token stays in the header.
            var value=JsonSerializer.SerializeToElement(body);
            request.Content=new FormUrlEncodedContent(new Dictionary<string,string>{["filename"]=value.GetProperty("filename").GetString()!,["length"]=value.GetProperty("length").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)});
        }else if(!read){request.Content=new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body));request.Content.Headers.ContentType=new("application/json");}
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
        if(response.StatusCode==HttpStatusCode.TooManyRequests){var retry=response.Headers.RetryAfter?.Delta?.TotalSeconds??30;throw new SlackRateLimit((int)Math.Ceiling(retry));}
        response.EnsureSuccessStatusCode();
        var bytes=await Bounded(response.Content,1024*1024,token);var json=JsonSerializer.Deserialize<JsonElement>(bytes);
        if(!json.TryGetProperty("ok",out var ok)||!ok.GetBoolean())throw new SlackRejected(json.TryGetProperty("error",out var e)?e.GetString()??"unknown":"unknown");
        return json;
    }
    internal async Task ValidateIdentity(SlackOptions o,CancellationToken token)
    {
        var auth=await Call("auth.test",new{},false,token);
        if(auth.GetProperty("team_id").GetString()!=o.TeamId||auth.GetProperty("user_id").GetString()!=o.BotUserId)throw new UnauthorizedAccessException("SLACK_IDENTITY_MISMATCH");
        var info=(await Call("conversations.info",new{channel=o.ChannelId},false,token)).GetProperty("channel");
        bool IsTrue(string k)=>info.TryGetProperty(k,out var x)&&x.ValueKind==JsonValueKind.True;
        bool IsFalse(string k)=>info.TryGetProperty(k,out var x)&&x.ValueKind==JsonValueKind.False;
        if(info.GetProperty("id").GetString()!=o.ChannelId||!IsTrue("is_member")||!IsFalse("is_private")||!IsFalse("is_shared")||IsTrue("is_ext_shared")||IsTrue("is_org_shared")||!IsFalse("is_archived"))throw new UnauthorizedAccessException("SLACK_CHANNEL_NOT_ALLOWED");
    }
    internal async Task<Uri> Open(CancellationToken token)
    {
        var response=await Call("apps.connections.open",new{},true,token);
        var uri=new Uri(response.GetProperty("url").GetString()!);
        if(uri.Scheme!="wss"||!(uri.Host=="wss.slack.com"||uri.Host.EndsWith(".slack.com",StringComparison.OrdinalIgnoreCase))||uri.UserInfo!=""||!uri.IsDefaultPort)throw new UnauthorizedAccessException("SLACK_SOCKET_ORIGIN");
        return uri;
    }
    internal async Task<string> Reply(SlackStore.Reply reply,CancellationToken token)
    {
        var response=await Call("chat.postMessage",new{channel=reply.Channel,thread_ts=reply.Thread,text=reply.Text,reply_broadcast=false,mrkdwn=false,parse="none",link_names=false,unfurl_links=false,unfurl_media=false,client_msg_id=Guid.ParseExact(SlackHash.Of(reply.Id)[..32],"N").ToString()},false,token);
        return response.GetProperty("ts").GetString()!;
    }
    internal static bool ValidResultUploadArgument(string filename,int length)=>length is >=1 and <=1024*1024 &&
        System.Text.RegularExpressions.Regex.IsMatch(filename,@"\A(?:ocr-[a-f0-9]{32}\.(?:txt|md)|analysis-[a-f0-9]{32}\.(?:txt|md|html)|ocr-json-[a-f0-9]{32}\.json|ocr-html-[a-f0-9]{32}\.html|ocr-csv-[a-f0-9]{32}\.csv)\z");
    internal async Task<(string Id,string Url)> ResultUploadTicket(string filename,int length,CancellationToken token)
    {
        if(!ValidResultUploadArgument(filename,length))throw new InvalidDataException("SLACK_RESULT_UPLOAD_ARGUMENT");
        var response=await Call("files.getUploadURLExternal",new{filename,length},false,token);
        var id=response.GetProperty("file_id").GetString()!;var url=response.GetProperty("upload_url").GetString()!;
        ValidateResultUploadUrl(url);
        if(!System.Text.RegularExpressions.Regex.IsMatch(id,"^F[A-Z0-9]{2,32}$"))throw new InvalidDataException("SLACK_RESULT_FILE_ID");
        return(id,url);
    }
    internal static Uri ValidateResultUploadUrl(string value)
    {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="files.slack.com"||!uri.IsDefaultPort||uri.UserInfo!=""||uri.Fragment!=""||!uri.AbsolutePath.StartsWith("/upload/v1/",StringComparison.Ordinal))throw new UnauthorizedAccessException("SLACK_RESULT_UPLOAD_ORIGIN");
        return uri;
    }
    internal async Task UploadResultBytes(string url,byte[] bytes,CancellationToken token)
    {
        if(bytes.Length is <1 or >1024*1024)throw new InvalidDataException("SLACK_RESULT_FILE_SIZE");
        using var request=new HttpRequestMessage(HttpMethod.Post,ValidateResultUploadUrl(url));request.Content=new ByteArrayContent(bytes);request.Content.Headers.ContentType=new("application/octet-stream");
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
        if(response.StatusCode==HttpStatusCode.TooManyRequests)throw new SlackRateLimit((int)Math.Ceiling(response.Headers.RetryAfter?.Delta?.TotalSeconds??30));
        if(response.StatusCode!=HttpStatusCode.OK)throw new HttpRequestException("SLACK_RESULT_UPLOAD_FAILED");
        // Upload URLs are short-lived capabilities. Neither URL nor file text is logged or returned.
    }
    internal async Task CompleteResultUpload(SlackStore.ResultFile file,CancellationToken token)
    {
        if(file.RemoteId is null||!System.Text.RegularExpressions.Regex.IsMatch(file.RemoteId,"^F[A-Z0-9]{2,32}$")||!System.Text.RegularExpressions.Regex.IsMatch(file.Channel,"^C[A-Z0-9]{2,32}$")||!SlackMention.ValidTs(file.Thread))throw new InvalidDataException("SLACK_RESULT_DESTINATION");
        var response=await Call("files.completeUploadExternal",new{files=new[]{new{id=file.RemoteId,title=file.Filename}},channel_id=file.Channel,thread_ts=file.Thread,initial_comment="読み取り結果をテキストファイルで添付しました。"},false,token);
        var files=response.GetProperty("files");if(files.ValueKind!=JsonValueKind.Array||files.GetArrayLength()!=1||files[0].GetProperty("id").GetString()!=file.RemoteId)throw new InvalidDataException("SLACK_UPLOAD_COMPLETION_BINDING");
    }
    internal async Task<(byte[] Bytes,string Extension)> Download(string fileId,SlackOptions options,CancellationToken token)
    {
        var file=(await Call("files.info",new{file=fileId},false,token)).GetProperty("file");
        if(file.GetProperty("id").GetString()!=fileId||file.TryGetProperty("is_external",out var external)&&external.GetBoolean()||
            file.GetProperty("size").GetInt64() is <1||file.GetProperty("size").GetInt64()>options.MaxFileBytes)throw new InvalidDataException("SLACK_FILE_DENIED");
        // IDs were supplied by an authenticated mention from the sole allowed channel. Require channel visibility as well.
        var channels=file.TryGetProperty("channels",out var ch)&&ch.ValueKind==JsonValueKind.Array?ch.EnumerateArray().Select(x=>x.GetString()).ToArray():[];
        var share=file.TryGetProperty("shares",out var sh)&&sh.TryGetProperty("public",out var pub)&&pub.TryGetProperty(options.ChannelId,out _);
        if(!channels.Contains(options.ChannelId)&&!share)throw new UnauthorizedAccessException("SLACK_FILE_CHANNEL_MISMATCH");
        var mime=file.GetProperty("mimetype").GetString();
        var extension=mime switch{"image/png"=>".png","image/jpeg"=>".jpg","image/webp"=>".webp","application/pdf"=>".pdf",_=>throw new InvalidDataException("SLACK_FILE_TYPE_DENIED")};
        var uri=new Uri(file.GetProperty("url_private").GetString()!);
        if(uri.Scheme!="https"||uri.Host!="files.slack.com"||!uri.IsDefaultPort||uri.UserInfo!=""||!uri.AbsolutePath.StartsWith("/files-pri/",StringComparison.Ordinal))throw new UnauthorizedAccessException("SLACK_DOWNLOAD_ORIGIN_DENIED");
        using var request=new HttpRequestMessage(HttpMethod.Get,uri);request.Headers.Authorization=new("Bearer",botToken);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
        if(response.StatusCode==HttpStatusCode.TooManyRequests)throw new SlackRateLimit((int)Math.Ceiling(response.Headers.RetryAfter?.Delta?.TotalSeconds??30));
        response.EnsureSuccessStatusCode();
        var bytes=await Bounded(response.Content,options.MaxFileBytes,token);
        if(bytes.LongLength!=file.GetProperty("size").GetInt64())throw new InvalidDataException("SLACK_FILE_SIZE_MISMATCH");
        bool magic=extension switch{".png"=>bytes.AsSpan().StartsWith(new byte[]{137,80,78,71,13,10,26,10}),".jpg"=>bytes.AsSpan().StartsWith(new byte[]{255,216,255}),".webp"=>bytes.Length>=12&&System.Text.Encoding.ASCII.GetString(bytes,0,4)=="RIFF"&&System.Text.Encoding.ASCII.GetString(bytes,8,4)=="WEBP",".pdf"=>bytes.AsSpan().StartsWith("%PDF-"u8),_=>false};
        if(!magic)throw new InvalidDataException("SLACK_FILE_MAGIC_MISMATCH");return(bytes,extension);
    }
    private static async Task<byte[]> Bounded(HttpContent content,int max,CancellationToken token)
    {
        if(content.Headers.ContentLength>max)throw new InvalidDataException("SLACK_RESPONSE_SIZE");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var input=await content.ReadAsStreamAsync(deadline.Token);using var output=new MemoryStream();var buffer=new byte[8192];int n;
        while((n=await input.ReadAsync(buffer,deadline.Token))>0){if(output.Length+n>max)throw new InvalidDataException("SLACK_RESPONSE_SIZE");output.Write(buffer,0,n);}return output.ToArray();
    }
    public void Dispose()=>http.Dispose();
}
