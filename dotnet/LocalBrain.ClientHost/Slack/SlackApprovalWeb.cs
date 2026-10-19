using System.Collections.Concurrent;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace LocalBrain.ClientHost.Slack;

internal sealed record SlackApprovalOptions(string ListenAddress,int Port,string PublicOrigin,string ServerCertificateThumbprint,
    Dictionary<string,string> AdminCertificateSha256,string LanCidr)
{
    internal void Validate()
    {
        var address=IPAddress.Parse(ListenAddress);
        if(!SlackLan.IsPrivate(address)||!SlackLan.Contains(LanCidr,address)||Port is <1024 or >65535||
            !Uri.TryCreate(PublicOrigin,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Port!=Port||uri.UserInfo!=""||uri.AbsolutePath!="/"||uri.Query!=""||uri.Fragment!=""||
            !Regex.IsMatch(ServerCertificateThumbprint,"^[A-Fa-f0-9]{40,64}$")||AdminCertificateSha256.Count is <1 or >20||
            AdminCertificateSha256.Any(p=>!Regex.IsMatch(p.Key,"^[a-f0-9]{64}$")||!Regex.IsMatch(p.Value,"^[A-Za-z0-9_-]{8,64}$")))throw new InvalidDataException("SLACK_APPROVAL_CONFIG_INVALID");
    }
}
internal static class SlackLan
{
    internal static bool IsPrivate(IPAddress a){var b=a.MapToIPv4().GetAddressBytes();return a.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork&&(b[0]==10||b[0]==172&&b[1] is >=16 and <=31||b[0]==192&&b[1]==168);}
    internal static bool Contains(string cidr,IPAddress address)
    {
        var parts=cidr.Split('/');if(parts.Length!=2||!IPAddress.TryParse(parts[0],out var network)||!int.TryParse(parts[1],out var bits)||bits is <8 or >32||!IsPrivate(network)||!IsPrivate(address))return false;
        var a=address.GetAddressBytes();var n=network.GetAddressBytes();var whole=bits/8;var remainder=bits%8;
        return a.Take(whole).SequenceEqual(n.Take(whole))&&(remainder==0||(a[whole]&(255<<(8-remainder)))==(n[whole]&(255<<(8-remainder))));
    }
}
internal sealed class SlackApprovalWeb(SlackApprovalOptions options,SlackStore store,Func<string>? hostCertificateHash=null)
{
    private sealed record Login(string Actor,string Cert,string Csrf,DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string,Login> logins=new();
    internal bool ValidateCertificate(X509Certificate2 certificate)
    {
        var now=DateTime.UtcNow;var hash=certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
        return now>=certificate.NotBefore.ToUniversalTime()&&now<certificate.NotAfter.ToUniversalTime()&&options.AdminCertificateSha256.ContainsKey(hash)&&
            certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e=>e.EnhancedKeyUsages.Cast<Oid>().Any(o=>o.Value=="1.3.6.1.5.5.7.3.2"));
    }
    internal async Task Run(CancellationToken token)
    {
        options.Validate();
        using var certificates=new X509Store(StoreName.My,StoreLocation.CurrentUser);certificates.Open(OpenFlags.ReadOnly);
        using var server=certificates.Certificates.Find(X509FindType.FindByThumbprint,options.ServerCertificateThumbprint,true).OfType<X509Certificate2>().Single(c=>c.HasPrivateKey);
        await RunCore(server,false,token);
    }
    // Only an internal test calls this with ephemeral fixture certificates. No command/config/model input exposes it.
    internal Task RunPublicFixture(X509Certificate2 server,CancellationToken token)
    {
        if(!IPAddress.IsLoopback(IPAddress.Parse(options.ListenAddress)))throw new UnauthorizedAccessException();
        return RunCore(server,true,token);
    }
    private async Task RunCore(X509Certificate2 server,bool fixture,CancellationToken token)
    {
        var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions{Args=[]});
        builder.Configuration.Sources.Clear();builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o=>o.SerializerOptions.Encoder=System.Text.Encodings.Web.JavaScriptEncoder.Default);
        if(fixture){builder.Logging.AddConsole();builder.Logging.SetMinimumLevel(LogLevel.Information);}
        builder.WebHost.ConfigureKestrel(k=>{
            k.AddServerHeader=false;k.Limits.MaxRequestBodySize=4096;k.Limits.MaxConcurrentConnections=20;
            k.Listen(IPAddress.Parse(options.ListenAddress),options.Port,listen=>listen.UseHttps(tls=>{
                tls.ServerCertificate=server;tls.SslProtocols=SslProtocols.Tls12|SslProtocols.Tls13;tls.ClientCertificateMode=ClientCertificateMode.RequireCertificate;
                // Exact Host-admin certificate enrollment is the authority, not forwarded headers or Slack identity.
                tls.ClientCertificateValidation=(cert,chain,errors)=>ValidateCertificate(cert);
            }));
        });
        await using var app=builder.Build();
        app.Use(async(context,next)=>{
            context.Response.Headers.CacheControl="no-store";context.Response.Headers["X-Content-Type-Options"]="nosniff";
            context.Response.Headers["Referrer-Policy"]="no-referrer";context.Response.Headers["X-Frame-Options"]="DENY";
            context.Response.Headers["Strict-Transport-Security"]="max-age=31536000";
            var actual="https://"+context.Request.Host;
            var cert=await context.Connection.GetClientCertificateAsync(token);
            if(!context.Request.IsHttps||actual!=options.PublicOrigin.TrimEnd('/')||context.Connection.RemoteIpAddress is not{} remote||!(fixture?IPAddress.IsLoopback(remote):SlackLan.Contains(options.LanCidr,remote))||cert is null||!ValidateCertificate(cert)){
                context.Response.StatusCode=403;return;
            }
            using var restricted=RestrictedExecution.EnterSlack();
            try{await next(context);}catch(Exception error)when(error is UnauthorizedAccessException or InvalidDataException or KeyNotFoundException or InvalidOperationException or JsonException){context.Response.StatusCode=409;await context.Response.WriteAsJsonAsync(new{status="rejected",code="SLACK_APPROVAL_INVALID"},token);}
        });
        RequestDelegate page=async context=>{
            foreach(var p in logins.Where(p=>p.Value.Expires<DateTimeOffset.UtcNow))logins.TryRemove(p.Key,out _);
            if(logins.Count>=100){context.Response.StatusCode=429;return;}
            var cert=(await context.Connection.GetClientCertificateAsync(token))!;var hash=cert.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
            var session=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));var csrf=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            logins[session]=new(options.AdminCertificateSha256[hash],hash,csrf,DateTimeOffset.UtcNow.AddMinutes(10));
            context.Response.Cookies.Append("__Host-lbslack",session,new CookieOptions{HttpOnly=true,Secure=true,SameSite=SameSiteMode.Strict,Path="/",MaxAge=TimeSpan.FromMinutes(10)});
            var nonce=Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            context.Response.Headers.ContentSecurityPolicy=$"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            context.Response.ContentType="text/html; charset=utf-8";
            await context.Response.WriteAsync(Page.Replace("{{NONCE}}",nonce).Replace("{{CSRF}}",csrf),token);
        };
        app.MapGet("/",page);app.MapGet("/slack/approvals",page);
        app.MapGet("/api/proposals",async context=>{_ = await Authenticate(context,false,token);await context.Response.WriteAsJsonAsync(new{proposals=store.Proposals(),restriction=SlackRestriction.Instance},token);});
        app.MapGet("/api/deliveries",async context=>{_ = await Authenticate(context,false,token);await context.Response.WriteAsJsonAsync(new{deliveries=store.UncertainReplies().Select(r=>new{r.Id,r.Channel,r.Thread,r.Kind,r.Text,hash=SlackStore.ReplyHash(r)})},token);});
        app.MapPost("/api/delivery-confirmation",async context=>{
            var login=await Authenticate(context,true,token);if(context.Request.ContentType?.Split(';')[0]!="application/json"){context.Response.StatusCode=415;return;}var input=await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body,cancellationToken:token);
            LocalBrain.ClientHost.AgentV2.DecisionProtocol.Keys(input,"id","hash","remote_ts");
            store.ReconcileReply(input.GetProperty("id").GetString()!,input.GetProperty("hash").GetString()!,input.GetProperty("remote_ts").GetString()!,login.Actor);
            await context.Response.WriteAsJsonAsync(new{status="confirmed_sent"},token);
        });
        app.MapPost("/api/decision",async context=>{
            var login=await Authenticate(context,true,token);
            if(context.Request.ContentType?.Split(';')[0]!="application/json"){context.Response.StatusCode=415;return;}
            var input=await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body,cancellationToken:token);
            LocalBrain.ClientHost.AgentV2.DecisionProtocol.Keys(input,"id","hash","action");
            var id=input.GetProperty("id").GetString()!;var action=input.GetProperty("action").GetString()!;string? hostHash=null;
            if(action=="approve"&&store.HasServerBinding(id)){
                if(hostCertificateHash is not null)hostHash=hostCertificateHash();
                else{using var hostCert=ClientConfig.Load().LoadCertificate();hostHash=hostCert.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();}
            }
            var result=store.Authorize(id,input.GetProperty("hash").GetString()!,login.Actor,true,action,DateTimeOffset.UtcNow,hostHash);
            await context.Response.WriteAsJsonAsync(new{proposal_id=result.Id,status=result.State,formal_adoption=result.State=="applied",restriction=SlackRestriction.Instance},token);
        });
        await app.RunAsync(token);
    }
    private async Task<Login> Authenticate(HttpContext context,bool mutation,CancellationToken token)
    {
        if(!context.Request.Cookies.TryGetValue("__Host-lbslack",out var key)||!logins.TryGetValue(key,out var login)||login.Expires<DateTimeOffset.UtcNow)throw new UnauthorizedAccessException();
        var cert=await context.Connection.GetClientCertificateAsync(token);
        if(cert is null||cert.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant()!=login.Cert)throw new UnauthorizedAccessException();
        if(mutation&&(context.Request.Headers.Origin.ToString()!=options.PublicOrigin.TrimEnd('/')||context.Request.Headers["X-CSRF-Token"].ToString()!=login.Csrf))throw new UnauthorizedAccessException();
        return login;
    }
    private const string Page="""
        <!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>LocalBrain 承認管理</title>
        <style>body{font-family:system-ui,sans-serif;max-width:960px;margin:24px auto;padding:0 16px;background:#f6f7fb;color:#18263c}article{background:white;border:1px solid #ccd5e5;border-radius:12px;padding:18px;margin:16px 0}pre{white-space:pre-wrap;overflow-wrap:anywhere}button{padding:12px 18px;margin:4px;border:1px solid #8295ae;border-radius:8px;font:inherit;cursor:pointer}h1{font-size:1.6rem}.note{color:#38506f}#status{padding:10px}</style>
        <h1>LocalBrain 承認管理</h1><p class="note">Slack由来・外部AI送信禁止。正式な承認主体はClientPCのWindows Hostです。承認と適用結果は区別して表示します。</p>
        <button id="refresh">一覧を更新</button><div id="status" role="status"></div><main id="list"></main><section id="deliveries"></section>
        <script nonce="{{NONCE}}">
        const csrf='{{CSRF}}';const status=document.getElementById('status');
        async function load(){const r=await fetch('/api/proposals');if(!r.ok){status.textContent='認証が期限切れです。このHTTPSページを開き直してください。';return}const j=await r.json();const list=document.getElementById('list');list.replaceChildren();
        for(const p of j.proposals){const a=document.createElement('article');const h=document.createElement('h2');h.textContent=(p.kind==='rule'?'Rule変更':'知識候補')+' — '+p.state;a.append(h);
        for(const [label,value] of [['提案本文',p.body],['根拠・適用対象',p.evidence],['ローカルReviewer評価',p.review],['内容Hash',p.hash],['期限',new Date(p.expires).toLocaleString()]]){const title=document.createElement('strong');title.textContent=label;const v=document.createElement('pre');v.textContent=value;a.append(title,v)}
        if(['pending','held'].includes(p.state))for(const [action,label]of [['approve','承認'],['reject','却下'],['hold','保留']]){const b=document.createElement('button');b.textContent=label;b.onclick=async()=>{if(!confirm(label+'しますか？ 内容Hashに結合して記録します。'))return;b.disabled=true;try{const r=await fetch('/api/decision',{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-Token':csrf},body:JSON.stringify({id:p.id,hash:p.hash,action})});status.textContent=r.ok?'Hostに記録しました。適用結果は一覧で確認してください。':'拒否されました。内容または認証を確認してください。';await load()}catch{status.textContent='通信結果を確認できません。一覧で状態を確認してください。'}finally{b.disabled=false}};a.append(b)}list.append(a)}if(!j.proposals.length)status.textContent='承認待ちはありません。'}
        async function deliveries(){const r=await fetch('/api/deliveries');if(!r.ok)return;const j=await r.json();const box=document.getElementById('deliveries');box.replaceChildren();
        for(const d of j.deliveries){const a=document.createElement('article');const h=document.createElement('h2');h.textContent='送信結果の確認が必要';const p=document.createElement('pre');p.textContent='channel: '+d.channel+' / thread: '+d.thread+'\n'+d.text;const b=document.createElement('button');b.textContent='Slackで同じ投稿を確認し、送信済みとして記録';b.onclick=async()=>{const ts=prompt('Slackで該当する投稿のtsを確認して入力してください。未確認の投稿は送信済みにしないでください。');if(!ts)return;const r=await fetch('/api/delivery-confirmation',{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-Token':csrf},body:JSON.stringify({id:d.id,hash:d.hash,remote_ts:ts})});status.textContent=r.ok?'送信済みとして記録しました。':'確認を記録できませんでした。';await deliveries()};a.append(h,p,b);box.append(a)}}
        document.getElementById('refresh').onclick=()=>{load();deliveries()};load();deliveries();
        </script></html>
        """;
}
