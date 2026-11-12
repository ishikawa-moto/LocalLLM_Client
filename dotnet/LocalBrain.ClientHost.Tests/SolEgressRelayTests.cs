using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class SolEgressRelayTests
{
    private const string Good="sol.fixture.test", BadCertificate="bad.fixture.test";
    private const string DeviceIssuer="auth.openai.com", DevicePath="/api/accounts/deviceauth/usercode";

    internal static async Task RunAsync(string? fixtureBundle=null)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var token=timeout.Token;
        var deviceFixture=fixtureBundle is not null;
        var hostname=deviceFixture?DeviceIssuer:Good;
        using var rootKey=RSA.Create(2048);
        var rootRequest=new CertificateRequest("CN=LocalBrain relay fixture CA",rootKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign,true));
        var now=DateTimeOffset.UtcNow;
        using var root=rootRequest.CreateSelfSigned(now.AddMinutes(-5),now.AddHours(1));
        using var originCert=Leaf(root,hostname);
        using var relayCert=Leaf(root,hostname);
        using var wrongNameRelayCert=Leaf(root,BadCertificate);
        var calls=0;
        var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions {Args=[]});
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options=>options.Listen(IPAddress.Loopback,0,listen=>listen.UseHttps(originCert)));
        await using var origin=builder.Build();
        origin.Run(async context=>{
            Interlocked.Increment(ref calls);
            if(deviceFixture) {
                if(context.Request.Host.Value!=DeviceIssuer || !HttpMethods.IsPost(context.Request.Method) ||
                    context.Request.Path!=DevicePath || context.Request.Headers.ContainsKey("Authorization"))
                    throw new InvalidDataException("Unexpected device fixture request");
                context.Response.StatusCode=418;
                return;
            }
            if(context.Request.Path=="/redirect") {
                context.Response.Redirect("https://unapproved.fixture.test/other");
                return;
            }
            if(context.Request.Host.Value!=Good || context.Request.Headers.Authorization!="Bearer synthetic-only" ||
                context.Request.Headers.ContainsKey("X-Forwarded-Host") ||
                context.Request.Headers.ContainsKey("Cookie"))
                throw new InvalidDataException("Fixture headers crossed unexpectedly");
            using var body=new StreamReader(context.Request.Body);
            if(await body.ReadToEndAsync(context.RequestAborted)!="{}")throw new InvalidDataException("Fixture body changed");
            context.Response.ContentType="text/event-stream";
            await context.Response.WriteAsync("data: fixture\n\n",context.RequestAborted);
        });
        await origin.StartAsync(token);
        var originUri=new Uri(origin.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var upstream=Handler(root);
        upstream.ConnectCallback=async (connection,cancellation)=>{
            if(connection.DnsEndPoint.Host!=hostname && (deviceFixture || connection.DnsEndPoint.Host!=BadCertificate) ||
                connection.DnsEndPoint.Port!=443)
                throw new IOException("Fixture destination escaped");
            var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
            try {
                await socket.ConnectAsync(IPAddress.Loopback,originUri.Port,cancellation);
                return new NetworkStream(socket,ownsSocket:true);
            } catch {socket.Dispose();throw;}
        };
        SolEgressRelay.Route[] routes=deviceFixture?[
            new(DeviceIssuer,"POST",DevicePath,new Uri($"https://{DeviceIssuer}{DevicePath}"))
        ]:[
            new(Good,"POST","/responses",new Uri($"https://{Good}/responses"),BearerAllowed:true),
            new(Good,"GET","/redirect",new Uri($"https://{Good}/redirect")),
            new(BadCertificate,"GET","/responses",new Uri($"https://{BadCertificate}/responses"))
        ];
        var certificates=new Dictionary<string,X509Certificate2>{{hostname,relayCert}};
        if(!deviceFixture)certificates.Add(BadCertificate,wrongNameRelayCert);
        await using var relay=await SolEgressRelay.StartAsync(routes,certificates,upstream,token);
        if(fixtureBundle is not null) {
            fixtureBundle=Path.GetFullPath(fixtureBundle);
            ReviewQualification.GuardRoot(fixtureBundle);
            using(var ca=new StreamWriter(new FileStream(Path.Combine(fixtureBundle,"relay-fixture-root.pem"),FileMode.CreateNew)))
                await ca.WriteAsync(root.ExportCertificatePem());
            Console.WriteLine(JsonSerializer.Serialize(new {port=relay.ProxyAddress.Port}));
            await Console.Out.FlushAsync(token);
            if(await Console.In.ReadLineAsync(token) is not null)throw new IOException("Fixture control input must be EOF");
            Console.WriteLine(JsonSerializer.Serialize(new {upstream_requests=Volatile.Read(ref calls),external_requests=0}));
            return;
        }
        using var clientHandler=Handler(root);
        clientHandler.UseProxy=true;
        clientHandler.Proxy=new WebProxy(relay.ProxyAddress);
        using var client=new HttpClient(clientHandler);
        using var post=new HttpRequestMessage(HttpMethod.Post,$"https://{Good}/responses") {
            Content=new StringContent("{}",Encoding.UTF8,"application/json")
        };
        post.Headers.TryAddWithoutValidation("Authorization","Bearer synthetic-only");
        post.Headers.TryAddWithoutValidation("X-Forwarded-Host","unapproved.fixture.test");
        post.Headers.TryAddWithoutValidation("Cookie","trial=synthetic");
        using var result=await client.SendAsync(post,token);
        Check(result.StatusCode==HttpStatusCode.OK && result.Content.Headers.ContentType?.MediaType=="text/event-stream" &&
            await result.Content.ReadAsStringAsync(token)=="data: fixture\n\n","approved CONNECT and TLS streaming");
        Check(calls==1,"approved request is delivered once");
        using var otherPath=await client.PostAsync($"https://{Good}/responses?extra=1",new StringContent("{}"),token);
        Check(otherPath.StatusCode==HttpStatusCode.Forbidden && calls==1,"unlisted target denied before upstream");
        using var invalid=await client.GetAsync($"https://{BadCertificate}/responses",token);
        Check(invalid.StatusCode==HttpStatusCode.BadGateway && calls==1,"wrong upstream TLS name rejected");
        using var redirect=await client.GetAsync($"https://{Good}/redirect",token);
        Check(redirect.StatusCode==HttpStatusCode.BadGateway && calls==2 && redirect.Headers.Location is null,"upstream redirect denied");
        foreach(var authority in new[]{"unapproved.fixture.test:443","127.0.0.1:443",Good+":444"}) {
            using var tcp=await Connect(relay.ProxyAddress,token);
            await using var wire=tcp.GetStream();
            await Send(wire,$"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n",token);
            Check((await ReadHeaders(wire,token)).StartsWith("HTTP/1.1 403",StringComparison.Ordinal),"CONNECT authority rejected");
        }
        using(var tcp=await Connect(relay.ProxyAddress,token)) {
            await using var wire=tcp.GetStream();
            await Send(wire,$"CONNECT {Good}:443 HTTP/1.1\r\nHost: {Good}:443\r\n\r\n",token);
            Check((await ReadHeaders(wire,token)).StartsWith("HTTP/1.1 200",StringComparison.Ordinal),"approved raw CONNECT");
            await using var tls=new SslStream(wire);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost=Good,
                CertificateChainPolicy=Chain(root) },token);
            await Send(tls,"GET /redirect HTTP/1.1\r\nHost: unapproved.fixture.test\r\nConnection: close\r\n\r\n",token);
            Check((await ReadHeaders(tls,token)).StartsWith("HTTP/1.1 403",StringComparison.Ordinal) && calls==2,"inner authority rejected");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { status="PASS",external_requests=0,upstream_requests=calls,
            approved_tls=true,wrong_certificate_denied=true,unapproved_connect_denied=true,inner_authority_denied=true,redirect_denied=true }));
    }

    private static SocketsHttpHandler Handler(X509Certificate2 root) => new() {
        UseProxy=false,AllowAutoRedirect=false,SslOptions=new SslClientAuthenticationOptions { CertificateChainPolicy=Chain(root) }
    };

    private static X509ChainPolicy Chain(X509Certificate2 root) => new() {
        TrustMode=X509ChainTrustMode.CustomRootTrust,CustomTrustStore={root},RevocationMode=X509RevocationMode.NoCheck,
        DisableCertificateDownloads=true
    };

    private static X509Certificate2 Leaf(X509Certificate2 root,string dns)
    {
        using var key=RSA.Create(2048);
        var request=new CertificateRequest("CN="+dns,key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var names=new SubjectAlternativeNameBuilder();
        names.AddDnsName(dns);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
        var now=DateTimeOffset.UtcNow;
        using var cert=request.Create(root,now.AddMinutes(-2),now.AddMinutes(20),RandomNumberGenerator.GetBytes(12));
        using var combined=cert.CopyWithPrivateKey(key);
        var pfx=combined.Export(X509ContentType.Pkcs12);
        try {
            // Schannel cannot serve TLS with an ephemeral imported private key.
            return X509CertificateLoader.LoadPkcs12(pfx,null,
                OperatingSystem.IsWindows()?X509KeyStorageFlags.UserKeySet:X509KeyStorageFlags.EphemeralKeySet);
        } finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    private static async Task<TcpClient> Connect(Uri address,CancellationToken token)
    {
        var tcp=new TcpClient(AddressFamily.InterNetwork);
        try {await tcp.ConnectAsync(IPAddress.Loopback,address.Port,token);return tcp;}
        catch {tcp.Dispose();throw;}
    }

    private static Task Send(Stream stream,string text,CancellationToken token) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text),token).AsTask();

    private static async Task<string> ReadHeaders(Stream stream,CancellationToken token)
    {
        using var text=new MemoryStream();
        var buffer=new byte[1];
        while(text.Length<8192 && await stream.ReadAsync(buffer,token)==1) {
            text.WriteByte(buffer[0]);
            if(text.GetBuffer().AsSpan(0,(int)text.Length).EndsWith("\r\n\r\n"u8))
                return Encoding.ASCII.GetString(text.ToArray());
        }
        throw new IOException("Fixture HTTP headers incomplete");
    }

    private static void Check(bool accepted,string name)
    {
        if(!accepted)throw new InvalidOperationException(name);
    }
}
