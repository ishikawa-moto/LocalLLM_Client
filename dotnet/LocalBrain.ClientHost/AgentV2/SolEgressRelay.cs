using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LocalBrain.ClientHost.AgentV2;

// The destination is taken from the Host route, never from a client URL or forwarded header.
internal sealed class SolEgressRelay(WebApplication app, Uri proxyAddress, HttpClient upstream) : IAsyncDisposable
{
    internal sealed record Route(string Host,string Method,string Target,Uri Destination,bool BearerAllowed=false);
    private sealed record Tunnel(string Host);
    private static readonly object TunnelKey=new();
    private const int ConnectLimit=4096, RequestLimit=128*1024;

    internal Uri ProxyAddress { get; }=proxyAddress;

    internal static async Task<SolEgressRelay> StartAsync(IReadOnlyCollection<Route> routes,
        IReadOnlyDictionary<string,X509Certificate2> certificates,SocketsHttpHandler handler,CancellationToken token=default)
    {
        var policy=routes.ToDictionary(route=>(route.Host,route.Method,route.Target));
        var hosts=routes.Select(route=>route.Host).ToHashSet(StringComparer.Ordinal);
        var keys=certificates.ToDictionary(pair=>pair.Key,pair=>pair.Value,StringComparer.Ordinal);
        if(handler.SslOptions.RemoteCertificateValidationCallback is not null ||
            hosts.Count==0 || !hosts.SetEquals(keys.Keys) || keys.Values.Any(cert=>!cert.HasPrivateKey) ||
            routes.Any(route=>!IsHost(route.Host) || route.Method is not ("GET" or "POST") ||
                !route.Target.StartsWith('/') || route.Target.StartsWith("//",StringComparison.Ordinal) ||
                !route.Destination.IsAbsoluteUri || route.Destination.Scheme!="https" ||
                route.Destination.IdnHost!=route.Host || route.Destination.Port!=443 || route.Destination.UserInfo.Length!=0 ||
                route.Destination.Fragment.Length!=0 || route.Destination.PathAndQuery!=route.Target))
            throw new ArgumentException("Invalid Sol relay policy");

        handler.UseProxy=false;
        handler.AllowAutoRedirect=false;
        handler.UseCookies=false;
        handler.ActivityHeadersPropagator=null;
        handler.SslOptions.CertificateChainPolicy??=new X509ChainPolicy();
        handler.SslOptions.CertificateChainPolicy.DisableCertificateDownloads=true;
        handler.ConnectTimeout=TimeSpan.FromSeconds(8);
        var upstream=new HttpClient(handler) { Timeout=Timeout.InfiniteTimeSpan };
        WebApplication? server=null;
        try {
            var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args=[] });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options=>{
                options.AddServerHeader=false;
                options.Limits.MaxConcurrentConnections=8;
                options.Limits.MaxRequestBodySize=RequestLimit;
                options.Limits.MaxRequestHeadersTotalSize=16*1024;
                options.Limits.RequestHeadersTimeout=TimeSpan.FromSeconds(10);
                options.Listen(IPAddress.Loopback,0,listen=>{
                    listen.Protocols=HttpProtocols.Http1;
                    listen.Use(next=>async connection=>{
                        using var handshake=CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);
                        handshake.CancelAfter(TimeSpan.FromSeconds(8));
                        var host=await ReadConnectAsync(connection,hosts,handshake.Token);
                        if(host is null) {
                            await connection.Transport.Output.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),handshake.Token);
                            return;
                        }
                        connection.Items[TunnelKey]=new Tunnel(host);
                        connection.Features.Set(new Tunnel(host));
                        await connection.Transport.Output.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(),handshake.Token);
                        await next(connection);
                    });
                    listen.UseHttps(new TlsHandshakeCallbackOptions {
                        HandshakeTimeout=TimeSpan.FromSeconds(8),
                        OnConnection=context=>{
                            var tunnel=(Tunnel)context.Connection.Items[TunnelKey]!;
                            if(!string.Equals(context.ClientHelloInfo.ServerName,tunnel.Host,StringComparison.OrdinalIgnoreCase))
                                throw new AuthenticationException("SNI does not match the approved tunnel");
                            return ValueTask.FromResult(new SslServerAuthenticationOptions {
                                ServerCertificate=keys[tunnel.Host],
                                ApplicationProtocols=[SslApplicationProtocol.Http11],
                                EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13
                            });
                        }
                    });
                });
            });
            server=builder.Build();
            server.Run(context=>ForwardAsync(context,policy,upstream));
            await server.StartAsync(token);
            var addresses=server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            var port=new Uri(addresses.Single()).Port;
            return new(server,new UriBuilder(Uri.UriSchemeHttp,IPAddress.Loopback.ToString(),port).Uri,upstream);
        } catch {
            if(server is not null)await server.DisposeAsync();
            upstream.Dispose();
            throw;
        }
    }

    private static bool IsHost(string host) =>
        Uri.CheckHostName(host)==UriHostNameType.Dns && host.Contains('.') &&
        !host.EndsWith('.') && host.All(c=>char.IsAsciiLetterLower(c)||char.IsAsciiDigit(c)||c is '.' or '-');

    private static async Task<string?> ReadConnectAsync(ConnectionContext connection,HashSet<string> allowed,CancellationToken token)
    {
        var input=connection.Transport.Input;
        while(true) {
            var read=await input.ReadAsync(token);
            var buffer=read.Buffer;
            var cursor=new SequenceReader<byte>(buffer);
            if(cursor.TryReadTo(out ReadOnlySequence<byte> header,"\r\n\r\n"u8)) {
                var host=header.Length>ConnectLimit?null:ParseConnect(header.ToArray(),allowed);
                input.AdvanceTo(cursor.Position);
                return host;
            }
            if(buffer.Length>ConnectLimit || read.IsCompleted || read.IsCanceled) {
                input.AdvanceTo(buffer.End);
                return null;
            }
            input.AdvanceTo(buffer.Start,buffer.End);
        }
    }

    private static string? ParseConnect(byte[] header,HashSet<string> allowed)
    {
        if(header.Any(c=>c is not (13 or 10) && (c<32||c>126)))return null;
        var lines=Encoding.ASCII.GetString(header).Split("\r\n",StringSplitOptions.None);
        var first=lines[0].Split(' ');
        if(first.Length!=3 || first[0]!="CONNECT" || first[2]!="HTTP/1.1" ||
            !first[1].EndsWith(":443",StringComparison.Ordinal))return null;
        var host=first[1][..^4];
        if(!allowed.Contains(host))return null;
        var foundHost=false;
        foreach(var line in lines.Skip(1)) {
            var split=line.IndexOf(':');
            if(split<=0)return null;
            var name=line[..split].ToLowerInvariant();
            var value=line[(split+1)..].Trim();
            if(name=="host") {
                if(foundHost || value!=first[1])return null;
                foundHost=true;
            } else if(name is not ("user-agent" or "connection" or "proxy-connection"))return null;
        }
        return foundHost?host:null;
    }

    private static async Task ForwardAsync(HttpContext context,
        Dictionary<(string Host,string Method,string Target),Route> policy,HttpClient upstream)
    {
        var request=context.Request;
        var tunnel=context.Features.Get<Tunnel>();
        var rawTarget=context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        var authority=request.Host.Value;
        if(tunnel is null || !request.IsHttps || rawTarget is null ||
            !(authority==tunnel.Host || authority==tunnel.Host+":443") ||
            request.Headers.ContainsKey("Upgrade") ||
            !policy.TryGetValue((tunnel.Host,request.Method,rawTarget),out var route) ||
            request.ContentLength>RequestLimit ||
            (request.Method=="GET" && (request.ContentLength>0 || request.Headers.ContainsKey("Transfer-Encoding"))) ||
            (!route.BearerAllowed && request.Headers.ContainsKey("Authorization"))) {
            context.Response.StatusCode=StatusCodes.Status403Forbidden;
            return;
        }
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using var forward=new HttpRequestMessage(new HttpMethod(route.Method),route.Destination);
        foreach(var name in new[]{"Accept","User-Agent","Authorization","ChatGPT-Account-Id","OpenAI-Beta","Originator","session_id"})
            if(request.Headers.TryGetValue(name,out var value))forward.Headers.TryAddWithoutValidation(name,value.ToArray());
        if(route.Method=="POST") {
            forward.Content=new StreamContent(request.Body);
            if(request.ContentType is { } type)forward.Content.Headers.TryAddWithoutValidation("Content-Type",type);
            if(request.ContentLength is { } length)forward.Content.Headers.ContentLength=length;
        }
        try {
            using var reply=await upstream.SendAsync(forward,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
            var status=(int)reply.StatusCode;
            if(status is >=300 and <400) {
                context.Response.StatusCode=StatusCodes.Status502BadGateway;
                return;
            }
            context.Response.StatusCode=status;
            if(reply.Content.Headers.ContentType is { } contentType)context.Response.ContentType=contentType.ToString();
            if(reply.Content.Headers.ContentEncoding.Count!=0)
                context.Response.Headers.ContentEncoding=reply.Content.Headers.ContentEncoding.ToArray();
            await reply.Content.CopyToAsync(context.Response.Body,deadline.Token);
        } catch(Exception failure) when(failure is HttpRequestException or IOException or OperationCanceledException) {
            if(context.Response.HasStarted)context.Abort();
            else context.Response.StatusCode=StatusCodes.Status502BadGateway;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await app.DisposeAsync(); }
        finally { upstream.Dispose(); }
    }
}
