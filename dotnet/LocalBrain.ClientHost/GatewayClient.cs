using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

namespace LocalBrain.ClientHost;

internal sealed class GatewayClient : IDisposable
{
    private readonly HttpClient client;
    private readonly Lazy<HttpClient> slackClient;
    public GatewayClient(ClientConfig config)
    {
        var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(config.LoadCertificate());
        // This installation uses an offline private CA and publishes no CRL.
        // Normal Windows chain, validity, hostname, and EKU checks still apply.
        handler.CheckCertificateRevocationList = false;
        client = new HttpClient(handler) { BaseAddress = new Uri(config.GatewayBaseUrl), Timeout = Timeout.InfiniteTimeSpan };
        slackClient=new Lazy<HttpClient>(()=>{
        var localHandler=new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(5)};
        localHandler.SslOptions.ClientCertificates=new X509CertificateCollection{config.LoadCertificate()};
        localHandler.SslOptions.CertificateRevocationCheckMode=X509RevocationMode.NoCheck;
        localHandler.ConnectCallback=async(context,token)=>{
            var addresses=await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host,token);
            if(addresses.Length==0||addresses.Any(a=>!Slack.SlackLan.IsPrivate(a)))throw new UnauthorizedAccessException("SLACK_GATEWAY_MUST_BE_PRIVATE_LAN");
            var socket=new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream,System.Net.Sockets.ProtocolType.Tcp);
            try{await socket.ConnectAsync(new System.Net.IPEndPoint(addresses[0],context.DnsEndPoint.Port),token);return new System.Net.Sockets.NetworkStream(socket,ownsSocket:true);}catch{socket.Dispose();throw;}
        };
        return new HttpClient(localHandler){BaseAddress=new Uri(config.GatewayBaseUrl),Timeout=Timeout.InfiniteTimeSpan};
        });
    }
    // Disposable HTTP fixture only. Production always uses the configured existing certificate above.
    internal GatewayClient(Uri baseAddress,HttpMessageHandler fixtureHandler){client=new(fixtureHandler){BaseAddress=baseAddress,Timeout=Timeout.InfiniteTimeSpan};slackClient=new(()=>client);}
    internal async Task<string> SlackJsonAsync(string path,string json,CancellationToken token)
    {
        if(!Slack.RestrictedExecution.Active||path is not("/v1/slack/chat" or "/v1/slack/review" or "/v1/slack/proposal" or "/v1/slack/apply" or "/v1/slack/status" or "/v1/slack/retire" or "/v1/slack/cancel"))throw new UnauthorizedAccessException("SLACK_PRIVATE_ROUTE_DENIED");
        var bytes=System.Text.Encoding.UTF8.GetBytes(json);if(bytes.Length>65536)throw new InvalidDataException("SLACK_LOCAL_INPUT_SIZE");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(path=="/v1/slack/chat"?125:90));
        using var request=new HttpRequestMessage(HttpMethod.Post,path){Content=new ByteArrayContent(bytes)};request.Content.Headers.ContentType=new("application/json");
        request.Headers.TryAddWithoutValidation("X-LocalBrain-Request-Id",Guid.NewGuid().ToString());
        request.Headers.TryAddWithoutValidation("X-LocalBrain-Approval-Owner",DecisionProtocol.Owner);
        using var response=await slackClient.Value.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
        if(!response.IsSuccessStatusCode)throw new HttpRequestException("SLACK_LOCAL_GATEWAY_REJECTED",null,response.StatusCode);
        await using var stream=await response.Content.ReadAsStreamAsync(deadline.Token);using var output=new MemoryStream();var buffer=new byte[8192];int count;
        while((count=await stream.ReadAsync(buffer,deadline.Token))>0){if(output.Length+count>128000)throw new InvalidDataException("SLACK_LOCAL_RESPONSE_SIZE");output.Write(buffer,0,count);}
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }
    private bool HostOwnedPath(string path)
    {
        var normalized=Uri.UnescapeDataString(new Uri(client.BaseAddress!,path).AbsolutePath).TrimEnd('/');
        return normalized.Equals("/v1/brain/apply-decision",StringComparison.OrdinalIgnoreCase) || normalized.Equals("/v1/brain/apply-human-decision",StringComparison.OrdinalIgnoreCase);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Stream? body, string? contentType,
        long? contentLength, string requestId, CancellationToken cancellationToken)
    {
        var normalizedPath=new Uri(client.BaseAddress!,path).AbsolutePath;
        var slackPath=normalizedPath.StartsWith("/v1/slack/",StringComparison.Ordinal);
        if(slackPath||Slack.RestrictedExecution.Active)
            throw new UnauthorizedAccessException("SLACK_ROUTE_REQUIRES_RESTRICTED_HOST_OWNER");
        if(HostOwnedPath(path))throw new UnauthorizedAccessException("Decision adoption requires the owning Windows Host's durable approval dispatch");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("X-LocalBrain-Request-Id", requestId);
        if (body is not null)
        {
            request.Content = new StreamContent(body);
            if (MediaTypeHeaderValue.TryParse(contentType, out var mediaType)) request.Content.Headers.ContentType = mediaType;
            if (contentLength >= 0) request.Content.Headers.ContentLength = contentLength;
        }
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
    internal async Task<(int Status,string Json)> SendOwnedDecisionAsync(HostTaskJournal journal,DecisionAdoption.Reservation reservation,CancellationToken token)
    {
        Slack.RestrictedExecution.DenyAgentTools();
        if(!OperatingSystem.IsWindows() || HostTaskJournal.Current!=journal)throw new UnauthorizedAccessException("Adoption needs its current Windows Host journal");
        var persisted=journal.Canonical.DecisionApproval(journal.TaskId,reservation.ApprovalId);
        var bytes=DecisionProtocol.Utf8.GetBytes(reservation.Json);var payload=DecisionProtocol.Parse(reservation.Json);
        var approval=payload.GetProperty("approval");
        var route=approval.GetProperty("operation").GetString() switch{"decision"=>"/v1/brain/apply-decision","human-decision"=>"/v1/brain/apply-human-decision",_=>throw new UnauthorizedAccessException("Unknown owned adoption route")};
        if(!persisted.Reserved || persisted.ResultHash is not null || persisted.RequestHash!=reservation.RequestHash ||
            reservation.Route!=route || CanonicalStore.Hash(bytes)!=persisted.RequestHash ||
            !journal.Canonical.ReadEvidence(persisted.RequestHash).SequenceEqual(bytes) ||
            approval.GetProperty("task_id").GetString()!=journal.TaskId || approval.GetProperty("owner").GetString()!=DecisionProtocol.Owner ||
            approval.GetProperty("approval_id").GetString()!=reservation.ApprovalId)
            throw new UnauthorizedAccessException("Adoption transport is not bound to its durable Host reservation");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var request=new HttpRequestMessage(HttpMethod.Post,route);
        request.Headers.TryAddWithoutValidation("X-LocalBrain-Request-Id",Guid.NewGuid().ToString());
        request.Headers.TryAddWithoutValidation("X-LocalBrain-Approval-Owner",DecisionProtocol.Owner);
        request.Content=new ByteArrayContent(bytes);request.Content.Headers.ContentType=new("application/json");request.Content.Headers.ContentLength=bytes.LongLength;
        journal.Record("decision_adoption_transport_started",new{approval_id=reservation.ApprovalId,request_hash=reservation.RequestHash,route,content_length=bytes.LongLength,execution_owner=DecisionProtocol.Owner},"host_verified");
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
        await using var input=await response.Content.ReadAsStreamAsync(deadline.Token);using var output=new MemoryStream();var buffer=new byte[8192];
        int count;while((count=await input.ReadAsync(buffer,deadline.Token))>0){if(output.Length+count>250000)throw new InvalidDataException("Adoption response exceeds protocol budget");output.Write(buffer,0,count);}
        var responseBytes=output.ToArray();var responseJson=DecisionProtocol.Utf8.GetString(responseBytes);
        if((int)response.StatusCode is >=400 and <500 && (int)response.StatusCode is not (408 or 429) && !BoundBusinessRejection(responseJson,payload.GetProperty("request_id").GetString()!)){
            var responseHash=journal.Canonical.PutEvidence(responseBytes);
            journal.Record("decision_adoption_transport_denied",new{approval_id=reservation.ApprovalId,request_hash=reservation.RequestHash,response_hash=responseHash,http_status=(int)response.StatusCode,route,automatic_retry=false},"authenticated_server_response");
            throw new HttpRequestException("Owned adoption transport was denied without a bound adoption receipt",null,response.StatusCode);
        }
        return ((int)response.StatusCode,responseJson);
    }

    private static bool BoundBusinessRejection(string json,string requestId)
    {
        try{
            var value=DecisionProtocol.Parse(json);
            DecisionProtocol.Keys(value,value.TryGetProperty("audit_id",out _)?["status","reason_code","reason","request_id","audit_id"]:["status","reason_code","reason","request_id"]);
            return DecisionProtocol.Text(value,"status",32)=="rejected"&&DecisionProtocol.Text(value,"request_id",80)==requestId&&
                DecisionProtocol.Text(value,"reason_code",80).Length>0&&DecisionProtocol.Text(value,"reason",2000).Length>0&&
                (!value.TryGetProperty("audit_id",out var audit)||Guid.TryParseExact(audit.GetString(),"D",out _));
        }catch(Exception e) when(e is JsonException or InvalidDataException or ArgumentException or InvalidOperationException){return false;}
    }

    internal async Task<string> SendOwnedDecisionReviewAsync(HostTaskJournal journal,string path,string json,CancellationToken token)
    {
        Slack.RestrictedExecution.DenyAgentTools();
        if(path!="/v1/brain/decision-review-register"||!OperatingSystem.IsWindows()||HostTaskJournal.Current!=journal)throw new UnauthorizedAccessException("Decision review registration requires the current Windows Host owner");
        var payload=DecisionProtocol.Parse(json);
        DecisionProtocol.Keys(payload,"proposal_id","request_mode","host_policy_hash","task_id","workspace_id");
        if(payload.GetProperty("task_id").GetString()!=journal.TaskId||payload.GetProperty("workspace_id").GetString()!=journal.Canonical.GetTask(journal.TaskId)!.WorkspaceId)throw new UnauthorizedAccessException("Review registration belongs to another owner");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var bytes=DecisionProtocol.Utf8.GetBytes(json);using var request=new HttpRequestMessage(HttpMethod.Post,path);
        request.Headers.TryAddWithoutValidation("X-LocalBrain-Request-Id",Guid.NewGuid().ToString());request.Headers.TryAddWithoutValidation("X-LocalBrain-Approval-Owner",DecisionProtocol.Owner);
        request.Content=new ByteArrayContent(bytes);request.Content.Headers.ContentType=new("application/json");request.Content.Headers.ContentLength=bytes.LongLength;
        journal.Record("decision_review_registration_transport",new{request_hash=CanonicalStore.Hash(bytes),route=path,execution_owner=DecisionProtocol.Owner},"host_verified");
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
        await using var stream=await response.Content.ReadAsStreamAsync(deadline.Token);using var output=new MemoryStream();var buffer=new byte[8192];int count;
        while((count=await stream.ReadAsync(buffer,deadline.Token))>0){if(output.Length+count>250000)throw new InvalidDataException("Review response exceeds budget");output.Write(buffer,0,count);}
        var result=DecisionProtocol.Utf8.GetString(output.ToArray());if(!response.IsSuccessStatusCode)throw new HttpRequestException(result,null,response.StatusCode);return result;
    }
    public async Task<string> JsonAsync(HttpMethod method, string path, string json, CancellationToken cancellationToken = default)
    {
        await using var body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        using var response = await SendAsync(method, path, body, "application/json", body.Length, Guid.NewGuid().ToString(), cancellationToken);
        var result = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(result, null, response.StatusCode);
        return result;
    }
    public void Dispose(){client.Dispose();if(slackClient.IsValueCreated&&!ReferenceEquals(client,slackClient.Value))slackClient.Value.Dispose();}
}
