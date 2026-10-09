using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using LocalBrain.ClientHost.AgentV2;

namespace LocalBrain.ClientHost;

internal sealed class GatewayClient : IDisposable
{
    private readonly HttpClient client;
    public GatewayClient(ClientConfig config)
    {
        var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(config.LoadCertificate());
        // This installation uses an offline private CA and publishes no CRL.
        // Normal Windows chain, validity, hostname, and EKU checks still apply.
        handler.CheckCertificateRevocationList = false;
        client = new HttpClient(handler) { BaseAddress = new Uri(config.GatewayBaseUrl), Timeout = Timeout.InfiniteTimeSpan };
    }
    // Disposable HTTP fixture only. Production always uses the configured existing certificate above.
    internal GatewayClient(Uri baseAddress,HttpMessageHandler fixtureHandler)=>client=new(fixtureHandler){BaseAddress=baseAddress,Timeout=Timeout.InfiniteTimeSpan};
    private bool HostOwnedPath(string path)
    {
        var normalized=Uri.UnescapeDataString(new Uri(client.BaseAddress!,path).AbsolutePath).TrimEnd('/');
        return normalized.Equals("/v1/brain/apply-decision",StringComparison.OrdinalIgnoreCase) || normalized.Equals("/v1/brain/apply-human-decision",StringComparison.OrdinalIgnoreCase);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Stream? body, string? contentType,
        long? contentLength, string requestId, CancellationToken cancellationToken)
    {
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
        return ((int)response.StatusCode,DecisionProtocol.Utf8.GetString(output.ToArray()));
    }

    public async Task<string> JsonAsync(HttpMethod method, string path, string json, CancellationToken cancellationToken = default)
    {
        await using var body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        using var response = await SendAsync(method, path, body, "application/json", body.Length, Guid.NewGuid().ToString(), cancellationToken);
        var result = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(result, null, response.StatusCode);
        return result;
    }
    public void Dispose() => client.Dispose();
}
