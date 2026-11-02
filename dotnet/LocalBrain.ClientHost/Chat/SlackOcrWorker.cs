using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;
using LocalBrain.ClientHost.Ocr;
using LocalBrain.ClientHost.Slack;

namespace LocalBrain.ClientHost.Chat;

// Optional PC70 Bridge relay shares its existing OCR scheduler; no new listener or fallback.
internal static class SlackOcrWorker
{
    internal static async Task RunConfiguredAsync(ClientConfig config,CancellationToken token) {
        var path=Path.Combine(CanonicalStore.DefaultRoot,"slack-ocr","config.json");CanonicalStore.GuardPath(path);
        if(!File.Exists(path))return;
        if(new FileInfo(path).Length>4096)throw new InvalidDataException("SLACK_OCR_WORKER_CONFIG");
        var options=JsonSerializer.Deserialize<ChatOcrRelayOptions>(File.ReadAllBytes(path),ChatOcrProcessor.Json)??throw new InvalidDataException("SLACK_OCR_WORKER_CONFIG");
        if(!options.Enabled)return;options.Validate();
        var endpoint=new Uri(config.GatewayBaseUrl);
        if(endpoint.Scheme!="https")throw new UnauthorizedAccessException("SLACK_OCR_HTTPS_REQUIRED");
        var ocr=OcrConfig.Read()!;
        using var handler=new SocketsHttpHandler {UseProxy=false,AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(5)};
        handler.SslOptions.ClientCertificates=new X509CertificateCollection {config.LoadCertificate()};
        handler.SslOptions.CertificateRevocationCheckMode=X509RevocationMode.NoCheck;
        handler.ConnectCallback=async(context,cancellation)=>{
            if(context.DnsEndPoint.Port!=endpoint.Port)throw new UnauthorizedAccessException("SLACK_OCR_PORT_MISMATCH");
            var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
            try{await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback,endpoint.Port),cancellation);return new NetworkStream(socket,ownsSocket:true);}
            catch{socket.Dispose();throw;}
        };
        using var http=new HttpClient(handler){BaseAddress=endpoint,Timeout=Timeout.InfiniteTimeSpan};
        using var restricted=RestrictedExecution.EnterSlack();Directory.CreateDirectory(options.Root);CanonicalStore.GuardPath(options.Root);
        using var lease=new FileStream(Path.Combine(options.Root,"relay.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var processor=new ChatOcrProcessor(options,ocr);
            while(!token.IsCancellationRequested) {
                try {
                    var raw=await Post(http,"next","{\"schema\":\"localbrain.chat-ocr.poll.v1\"}",token);
                    using var d=JsonDocument.Parse(raw);var root=d.RootElement;
                    if(root.ValueKind!=JsonValueKind.Object || root.EnumerateObject().Count()!=1 || !root.TryGetProperty("job",out var value))throw new InvalidDataException("SLACK_OCR_ENVELOPE");
                    if(value.ValueKind!=JsonValueKind.Null){
                        var job=JsonSerializer.Deserialize<ChatOcrRequest>(value.GetRawText(),ChatOcrProcessor.Json)??throw new InvalidDataException("SLACK_OCR_ENVELOPE");
                        if(job.Origin!="slack")throw new UnauthorizedAccessException("SLACK_OCR_ORIGIN");
                        await Post(http,"update",processor.Report(job),token);
                    }
                    processor.Cleanup(DateTimeOffset.UtcNow);
                }catch(Exception e)when(e is IOException or HttpRequestException or JsonException or InvalidDataException or UnauthorizedAccessException or OcrException or OperationCanceledException){if(token.IsCancellationRequested)throw;}
                await Task.Delay(TimeSpan.FromSeconds(5),token);
            }
    }
    private static async Task<string> Post(HttpClient http,string action,string value,CancellationToken token) {
        var bytes=Encoding.UTF8.GetBytes(value);if(bytes.Length>16*1024*1024)throw new InvalidDataException("SLACK_OCR_REQUEST_SIZE");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request=new HttpRequestMessage(HttpMethod.Post,"/v1/slack-ocr/"+action){Content=new ByteArrayContent(bytes)};request.Content.Headers.ContentType=new("application/json");
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);if(!response.IsSuccessStatusCode)throw new HttpRequestException("SLACK_OCR_WORKER_REJECTED");
        await using var input=await response.Content.ReadAsStreamAsync(deadline.Token);using var output=new MemoryStream();var buffer=new byte[8192];int n;
        while((n=await input.ReadAsync(buffer,deadline.Token))>0){if(output.Length+n>6*1024*1024)throw new InvalidDataException("SLACK_OCR_RESPONSE_SIZE");output.Write(buffer,0,n);}
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
