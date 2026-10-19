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
        if(method is not("apps.connections.open" or "auth.test" or "conversations.info" or "files.info" or "chat.postMessage"))throw new UnauthorizedAccessException("SLACK_METHOD_DENIED");
        using var request=new HttpRequestMessage(HttpMethod.Post,"https://slack.com/api/"+method);
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",app?appToken:botToken);
        request.Content=new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body));request.Content.Headers.ContentType=new("application/json");
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
        if(info.GetProperty("id").GetString()!=o.ChannelId||!IsTrue("is_member")||IsTrue("is_private")||IsTrue("is_shared")||IsTrue("is_ext_shared")||IsTrue("is_org_shared")||IsTrue("is_archived"))throw new UnauthorizedAccessException("SLACK_CHANNEL_NOT_ALLOWED");
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
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();
        var bytes=await Bounded(response.Content,options.MaxFileBytes,token);
        if(bytes.LongLength!=file.GetProperty("size").GetInt64())throw new InvalidDataException("SLACK_FILE_SIZE_MISMATCH");
        bool magic=extension switch{".png"=>bytes.AsSpan().StartsWith(new byte[]{137,80,78,71,13,10,26,10}),".jpg"=>bytes.AsSpan().StartsWith(new byte[]{255,216,255}),".webp"=>bytes.Length>=12&&System.Text.Encoding.ASCII.GetString(bytes,0,4)=="RIFF"&&System.Text.Encoding.ASCII.GetString(bytes,8,4)=="WEBP",".pdf"=>bytes.AsSpan().StartsWith("%PDF-"u8),_=>false};
        if(!magic)throw new InvalidDataException("SLACK_FILE_MAGIC_MISMATCH");return(bytes,extension);
    }
    private static async Task<byte[]> Bounded(HttpContent content,int max,CancellationToken token)
    {
        if(content.Headers.ContentLength>max)throw new InvalidDataException("SLACK_RESPONSE_SIZE");
        await using var input=await content.ReadAsStreamAsync(token);using var output=new MemoryStream();var buffer=new byte[8192];int n;
        while((n=await input.ReadAsync(buffer,token))>0){if(output.Length+n>max)throw new InvalidDataException("SLACK_RESPONSE_SIZE");output.Write(buffer,0,n);}return output.ToArray();
    }
    public void Dispose()=>http.Dispose();
}
