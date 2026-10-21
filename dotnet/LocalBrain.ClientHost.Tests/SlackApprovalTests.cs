using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalBrain.ClientHost.Slack;

internal static class SlackApprovalTests
{
    internal static async Task RunAsync()
    {
        var checks=0;void Check(bool ok,string name){if(!ok)throw new Exception("SLACK_WEB_TEST_FAILED: "+name);checks++;}
        Check(SlackLan.Contains("192.168.10.0/24",IPAddress.Parse("192.168.10.33")),"same LAN");
        Check(!SlackLan.Contains("192.168.10.0/24",IPAddress.Parse("192.168.11.33")),"foreign LAN denied");
        Check(!SlackLan.IsPrivate(IPAddress.Parse("8.8.8.8"))&&!SlackLan.IsPrivate(IPAddress.IPv6Loopback),"public and IPv6 denied");
        using var rootKey=RSA.Create(2048);var rootReq=new CertificateRequest("CN=LocalBrain Public Test Root",rootKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign,true));
        using var rootCert=rootReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddHours(2));
        X509Certificate2 Leaf(string name,string eku){using var key=RSA.Create(2048);var req=new CertificateRequest("CN="+name,key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new Oid(eku)},true));
            if(name=="localhost"){var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");req.CertificateExtensions.Add(san.Build());}
            using var signed=req.Create(rootCert,DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1),RandomNumberGenerator.GetBytes(16));
            using var copy=signed.CopyWithPrivateKey(key);
            // Windows Schannel needs an OS-accessible key handle. No certificate is enrolled in a system store;
            // the test-only imported key container is owned by this disposable certificate.
            return X509CertificateLoader.LoadPkcs12(copy.Export(X509ContentType.Pkcs12),null,X509KeyStorageFlags.UserKeySet|X509KeyStorageFlags.Exportable);
        }
        using var server=Leaf("localhost","1.3.6.1.5.5.7.3.1");using var client=Leaf("test-admin","1.3.6.1.5.5.7.3.2");using var outsider=Leaf("unregistered","1.3.6.1.5.5.7.3.2");
        var directory=Directory.CreateTempSubdirectory("lb-slack-https-public-").FullName;
        using var store=new SlackStore(new(true,directory,"TTEST","CTEST","UBOT",Path.Combine(directory,"app.token"),Path.Combine(directory,"bot.token")));
        var now=DateTimeOffset.UtcNow;var mention=new SlackMention("EV-WEB","TTEST","CTEST","UALICE",now.ToUnixTimeSeconds()+".000000",now.ToUnixTimeSeconds()+".000000","<@UBOT> public",[],now);
        var job=store.Accept(mention,now.AddMinutes(-1),now).JobId!;var session=store.GetJob(job).SessionId;
        var proposal=store.Propose(session,"knowledge","PUBLIC <script>alert(1)</script>","PUBLIC source","PUBLIC local two pass fixture",now);
        for(var i=0;i<100;i++)store.Propose(session,"knowledge","PUBLIC page fixture "+i,"PUBLIC source","PUBLIC local two pass fixture",now);
        var portProbe=new TcpListener(IPAddress.Loopback,0);portProbe.Start();var port=((IPEndPoint)portProbe.LocalEndpoint).Port;portProbe.Stop();
        var origin="https://localhost:"+port;
        var password="PUBLIC-test-password-20261020";
        var credential=SlackPasswordCredential.Create(password,"public-test-admin");var clock=now;
        Check(SlackPasswordCredential.Create("1234").Matches("1234"),"four character minimum supported");
        Check(credential.Matches(password)&&!credential.Matches(password+"x"),"salted password exact matching");
        var auth=new SlackPasswordAuth(()=>credential);
        for(var i=0;i<6;i++)Check(!auth.Verify("wrong").Accepted,"invalid password");
        Check(auth.Verify(password).Accepted,"no attempt-count lockout");
        var options=new SlackApprovalOptions(IPAddress.Loopback.ToString(),port,origin,server.Thumbprint,Path.Combine(directory,"password.json"),"192.168.10.0/24");
        var web=new SlackApprovalWeb(options,store,credential:()=>credential,clock:()=>clock);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(30));var serving=web.RunPublicFixture(server,stop.Token);
        HttpClient Client(X509Certificate2? cert){var handler=new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false};if(cert is not null)handler.ClientCertificates.Add(cert);
            handler.ServerCertificateCustomValidationCallback=(request,certificate,chain,errors)=>{
                if(certificate is null||(errors&SslPolicyErrors.RemoteCertificateNameMismatch)!=0||certificate.GetCertHashString(HashAlgorithmName.SHA256)!=server.GetCertHashString(HashAlgorithmName.SHA256))return false;
                using var verified=new X509Chain();verified.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;verified.ChainPolicy.CustomTrustStore.Add(rootCert);verified.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;verified.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));return verified.Build(certificate);
            };return new(handler){BaseAddress=new Uri(origin),Timeout=TimeSpan.FromSeconds(5)};
        }
        try{
            using var admin=Client(null);HttpResponseMessage? page=null;
            for(var i=0;i<30;i++){try{page=await admin.GetAsync("/",stop.Token);break;}catch(HttpRequestException error){if(i==0)Console.WriteLine("PUBLIC_FIXTURE_CONNECT: "+error.ToString());await Task.Delay(100,stop.Token);}}
            Check(page is not null&&page.IsSuccessStatusCode,"real HTTPS login without client certificate");
            var html=await page!.Content.ReadAsStringAsync(stop.Token);Check(page.Headers.TryGetValues("Content-Security-Policy",out var csp)&&csp.Single().Contains("frame-ancestors 'none'"),"frame and script policy");
            Check(page.Headers.GetValues("Set-Cookie").Any(x=>x.Contains("secure",StringComparison.OrdinalIgnoreCase)&&x.Contains("httponly",StringComparison.OrdinalIgnoreCase)&&x.Contains("samesite=strict",StringComparison.OrdinalIgnoreCase)),"secure cookie");
            var csrf=Regex.Match(html,"const csrf='([A-F0-9]+)'").Groups[1].Value;Check(csrf.Length==64,"prelogin CSRF token");
            using(var denied=await admin.GetAsync("/api/proposals",stop.Token))Check(denied.StatusCode==HttpStatusCode.Unauthorized,"unauthenticated data denied");
            async Task<HttpResponseMessage> Login(string value,string? csrfValue,string originValue){using var req=new HttpRequestMessage(HttpMethod.Post,"/api/login");req.Headers.Add("Origin",originValue);if(csrfValue is not null)req.Headers.Add("X-CSRF-Token",csrfValue);req.Content=new StringContent(JsonSerializer.Serialize(new{password=value}),Encoding.UTF8,"application/json");return await admin.SendAsync(req,stop.Token);}
            using(var denied=await Login(password,null,origin))Check(denied.StatusCode==HttpStatusCode.Unauthorized,"login CSRF required");
            using(var denied=await Login(password,csrf,"https://foreign.invalid"))Check(denied.StatusCode==HttpStatusCode.Unauthorized,"login origin required");
            using(var denied=await Login("wrong",csrf,origin))Check(denied.StatusCode==HttpStatusCode.Unauthorized,"wrong password denied");
            using(var yes=await Login(password,csrf,origin))Check(yes.IsSuccessStatusCode,"password login without browser certificate");
            html=await admin.GetStringAsync("/",stop.Token);csrf=Regex.Match(html,"const csrf='([A-F0-9]+)'").Groups[1].Value;
            Check(html.Contains("一覧を更新")&&html.Contains("次へ")&&csrf.Length==64,"authenticated approval page");
            using(var list=await admin.GetAsync("/api/proposals",stop.Token)){Check(list.IsSuccessStatusCode,"authorized list");var body=await list.Content.ReadAsStringAsync(stop.Token);Check(body.Contains("slack_restricted")&&!body.Contains("<script>"),"restriction and JSON encoding");using var json=JsonDocument.Parse(body);Check(json.RootElement.GetProperty("pageCount").GetInt32()==2&&!body.Contains(proposal.Id),"proposal list first page");}
            using(var list=await admin.GetAsync("/api/proposals?page=2",stop.Token)){using var json=JsonDocument.Parse(await list.Content.ReadAsStringAsync(stop.Token));Check(list.IsSuccessStatusCode&&json.RootElement.GetProperty("proposals").EnumerateArray().Any(p=>p.GetProperty("id").GetString()==proposal.Id),"proposal list reaches older items");}
            async Task<HttpResponseMessage> Decide(string hash,string? csrfValue,string originValue){using var req=new HttpRequestMessage(HttpMethod.Post,"/api/decision");req.Headers.Add("Origin",originValue);if(csrfValue is not null)req.Headers.Add("X-CSRF-Token",csrfValue);req.Content=new StringContent(JsonSerializer.Serialize(new{id=proposal.Id,hash,action="approve"}),Encoding.UTF8,"application/json");return await admin.SendAsync(req,stop.Token);}
            using(var no=await Decide(proposal.Hash,null,origin))Check(!no.IsSuccessStatusCode,"missing CSRF denied");
            using(var no=await Decide(proposal.Hash,csrf,"https://foreign.invalid"))Check(!no.IsSuccessStatusCode,"foreign origin denied");
            using(var no=await Decide(new string('0',64),csrf,origin))Check(!no.IsSuccessStatusCode,"hash drift denied");
            using(var yes=await Decide(proposal.Hash,csrf,origin)){Check(yes.IsSuccessStatusCode,"real HTTPS Host approval");Check(!(JsonSerializer.Deserialize<JsonElement>(await yes.Content.ReadAsStringAsync(stop.Token)).GetProperty("formal_adoption").GetBoolean()),"approval does not falsely claim apply");}
            using(var no=await Decide(proposal.Hash,csrf,origin))Check(!no.IsSuccessStatusCode,"replay denied");
            credential=SlackPasswordCredential.Create(password,"public-test-admin");
            using(var no=await admin.GetAsync("/api/proposals",stop.Token))Check(no.StatusCode==HttpStatusCode.Unauthorized,"password rotation revokes sessions");
            html=await admin.GetStringAsync("/",stop.Token);csrf=Regex.Match(html,"const csrf='([A-F0-9]+)'").Groups[1].Value;
            using(var yes=await Login(password,csrf,origin))Check(yes.IsSuccessStatusCode,"login after rotation");
            clock=now.AddMinutes(59);
            using(var yes=await admin.GetAsync("/api/proposals",stop.Token))Check(yes.IsSuccessStatusCode,"session remains valid before one hour");
            html=await admin.GetStringAsync("/",stop.Token);csrf=Regex.Match(html,"const csrf='([A-F0-9]+)'").Groups[1].Value;
            using(var request=new HttpRequestMessage(HttpMethod.Post,"/api/logout")){request.Headers.Add("Origin",origin);request.Headers.Add("X-CSRF-Token",csrf);using var response=await admin.SendAsync(request,stop.Token);Check(response.IsSuccessStatusCode,"explicit logout");}
            using(var no=await admin.GetAsync("/api/proposals",stop.Token))Check(no.StatusCode==HttpStatusCode.Unauthorized,"logout immediately ends session");
            html=await admin.GetStringAsync("/",stop.Token);csrf=Regex.Match(html,"const csrf='([A-F0-9]+)'").Groups[1].Value;
            using(var yes=await Login(password,csrf,origin))Check(yes.IsSuccessStatusCode,"login after logout");
            clock=now.AddMinutes(120);
            using(var no=await admin.GetAsync("/api/proposals",stop.Token))Check(no.StatusCode==HttpStatusCode.Unauthorized,"session expires after one hour");
        }finally{stop.Cancel();try{await serving;}catch(OperationCanceledException){}}
        Console.WriteLine(JsonSerializer.Serialize(new{slack_https_public_checks=checks,status="PASS",real_lan_phone_test=false,system_certificate_store_modified=false}));
    }
}
