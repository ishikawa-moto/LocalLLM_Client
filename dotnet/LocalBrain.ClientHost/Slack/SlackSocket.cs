using System.Net.WebSockets;
using System.Text.Json;

namespace LocalBrain.ClientHost.Slack;

internal sealed class SlackSocket(SlackOptions options,SlackStore store,SlackApi api)
{
    internal async Task Run(CancellationToken token)
    {
        var failures=0;
        while(!token.IsCancellationRequested){
            string? epoch=null;
            try{
                await api.ValidateIdentity(options,token);
                using var ws=new ClientWebSocket();ws.Options.KeepAliveInterval=TimeSpan.FromSeconds(20);
                await ws.ConnectAsync(await api.Open(token),token);DateTimeOffset? since=null;
                while(ws.State==WebSocketState.Open&&!token.IsCancellationRequested){
                    var payload=await Read(ws,token);var type=payload.GetProperty("type").GetString();
                    if(type=="hello"){since=DateTimeOffset.UtcNow;epoch=SlackHash.Id();store.BeginOnline(epoch,since.Value);failures=0;continue;}
                    if(type=="disconnect")break;
                    if(!payload.TryGetProperty("envelope_id",out var envelope))continue;
                    await ProcessEnvelope(payload,options,store,since,(bytes,t)=>ws.SendAsync(bytes,WebSocketMessageType.Text,true,t),token);
                }
            }catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
            catch(Exception e)when(e is HttpRequestException or WebSocketException or IOException or JsonException or InvalidOperationException or UnauthorizedAccessException or SlackRejected or SlackRateLimit or OperationCanceledException){
                // No URL, token, payload or exception.Message is printed.
                Console.Error.WriteLine(JsonSerializer.Serialize(new{component="slack_socket",code="reconnect_or_configuration_required",error_type=e.GetType().Name}));failures++;
                if(e is UnauthorizedAccessException||e is SlackRejected{Code:"invalid_auth" or "token_revoked" or "missing_scope"})throw new InvalidOperationException("SLACK_SETUP_REQUIRED");
            }finally{if(epoch is not null)store.EndOnline(epoch,DateTimeOffset.UtcNow);}
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(120,Math.Pow(2,Math.Min(failures+1,7)))),token);
        }
    }
    internal static async Task ProcessEnvelope(JsonElement payload,SlackOptions options,SlackStore store,DateTimeOffset? since,Func<ArraySegment<byte>,CancellationToken,Task> acknowledge,CancellationToken token)
    {
        if(!payload.TryGetProperty("envelope_id",out var envelope))return;
        // Only bounded local validation/SQLite work precedes ACK; never OCR, inference, download or reply HTTP.
        if(payload.GetProperty("type").GetString()=="events_api"&&since is not null&&payload.TryGetProperty("payload",out var inner)){
            var now=DateTimeOffset.UtcNow;var mention=SlackMention.Parse(inner,options,now);if(mention is not null)store.Accept(mention,since.Value,now);
        }
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await acknowledge(JsonSerializer.SerializeToUtf8Bytes(new{envelope_id=envelope.GetString()}),deadline.Token);
    }
    internal static async Task<JsonElement> Read(WebSocket socket,CancellationToken token)
    {
        using var message=new MemoryStream();var buffer=new byte[8192];WebSocketReceiveResult part;
        do{part=await socket.ReceiveAsync(buffer,token);if(part.MessageType!=WebSocketMessageType.Text)throw new WebSocketException("SLACK_FRAME_INVALID");if(message.Length+part.Count>1024*1024)throw new InvalidDataException("SLACK_FRAME_SIZE");message.Write(buffer,0,part.Count);}while(!part.EndOfMessage);
        return JsonSerializer.Deserialize<JsonElement>(message.ToArray());
    }
    internal static async Task Replies(SlackStore store,SlackApi api,CancellationToken token)
    {
        while(!token.IsCancellationRequested){var reply=store.ClaimReply(DateTimeOffset.UtcNow);if(reply is null){await Task.Delay(100,token);continue;}
            try{store.ReplySent(reply.Id,await api.Reply(reply,token));}
            catch(SlackRateLimit r){store.ReplyRetry(reply.Id,DateTimeOffset.UtcNow.AddSeconds(r.Seconds));}
            catch(SlackRejected r)when(r.Code is "ratelimited" or "rate_limited"){store.ReplyRetry(reply.Id,DateTimeOffset.UtcNow.AddSeconds(30));}
            catch(Exception e)when(e is HttpRequestException or OperationCanceledException or SlackRejected or JsonException or InvalidDataException){
                // Slack may have committed a post before the response was lost. Never blindly replay an unknown send.
                store.ReplyUncertain(reply.Id);
                Console.Error.WriteLine(JsonSerializer.Serialize(new{component="slack_reply",code="delivery_needs_reconciliation",reply_hash=SlackHash.Of(reply.Id)}));
            }
            await Task.Delay(1100,token);
        }
    }
}
