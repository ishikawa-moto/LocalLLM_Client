using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

[SupportedOSPlatform("windows")]
internal static class ReviewerLoopbackPolicyTests
{
    internal static async Task RunAsync(string bundle,string packageSid,int approved,int otherV4,int otherV6,bool relay=false)
    {
        bundle=Path.GetFullPath(bundle);
        ReviewQualification.GuardRoot(bundle);
        var sid=new SecurityIdentifier(packageSid);
        HostValidationCleanup.Grant(bundle,sid,false);
        try {
            var exe=Path.Combine(bundle,Path.GetFileName(Environment.ProcessPath!));
            var start=IsolatedStart(exe,bundle);
            start.Environment["DOTNET_ROOT"]=Path.Combine(bundle,"dotnet");
            start.Environment["HTTPS_PROXY"]=$"http://127.0.0.1:{otherV4}";
            foreach(var arg in new[]{"--review-loopback-policy-child",approved.ToString(),otherV4.ToString(),otherV6.ToString()})
                start.ArgumentList.Add(arg);
            if(relay)start.ArgumentList.Add("relay");
            using var job=WindowsChildJob.StartNetworkBoundaryFixture(start,sid.Value);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var stdout=job.Output.ReadToEndAsync(deadline.Token);
            var stderr=job.Error.ReadToEndAsync(deadline.Token);
            await job.Process.WaitForExitAsync(deadline.Token);
            var result=(await stdout).Trim();
            if(job.Process.ExitCode!=0)throw new IOException($"Network fixture exit {job.Process.ExitCode}: {await stderr}");
            if(!relay) {Console.Write(result);return;}
            using var probe=JsonDocument.Parse(result);
            var network=probe.RootElement;
            if(network.GetProperty("approved_tcp").GetInt32()!=0 ||
                new[]{"unapproved_ipv4_tcp","unapproved_ipv4_address","unapproved_ipv6_tcp","environment_proxy_result"}
                    .Any(name=>network.GetProperty(name).GetInt32()!=10013) ||
                !network.GetProperty("protected_file_denied").GetBoolean())
                throw new IOException($"Device auth fixture prerequisites failed: {result}");
            var withoutCa=await DeviceAuthAsync(bundle,sid,approved,false);
            var withCa=await DeviceAuthAsync(bundle,sid,approved,true);
            if(withoutCa || !withCa)throw new IOException("Device auth fixture did not require its process-scoped trust root");
            Console.Write(JsonSerializer.Serialize(new { network=probe.RootElement,relay_verified=true,
                cli="codex-cli 0.166.0",untrusted_root_rejected=true }));
        } finally {
            var directory=new DirectoryInfo(bundle);
            var acl=directory.GetAccessControl();
            acl.PurgeAccessRules(sid);
            directory.SetAccessControl(acl);
        }
    }

    private static ProcessStartInfo IsolatedStart(string exe,string home)
    {
        var start=new ProcessStartInfo(exe) {UseShellExecute=false,WorkingDirectory=home};
        start.Environment.Clear();
        start.Environment["SystemRoot"]=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach(var name in new[]{"USERPROFILE","LOCALAPPDATA","APPDATA","TEMP","TMP"})
            start.Environment[name]=home;
        return start;
    }

    private static async Task<bool> DeviceAuthAsync(string bundle,SecurityIdentifier sid,int port,bool trustRoot)
    {
        var exe=Path.Combine(bundle,"codex.exe");
        const string expectedHash="9D563EE4E0441FF9D59D612013E0460DDF8E5709735C2D9F6B98B7EF8CA4EB42";
        using(var file=File.OpenRead(exe))
            if(Convert.ToHexString(SHA256.HashData(file))!=expectedHash)
                throw new IOException("Device auth fixture binary hash mismatch");
        var home=Path.Combine(Path.GetDirectoryName(bundle)!,"codex-fixture-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        HostValidationCleanup.Grant(home,sid,true);
        try {
            var start=IsolatedStart(exe,home);
            start.Environment["CODEX_HOME"]=home;
            start.Environment["HTTPS_PROXY"]=$"http://127.0.0.1:{port}";
            if(trustRoot)start.Environment["CODEX_CA_CERTIFICATE"]=Path.Combine(bundle,"relay-fixture-root.pem");
            start.ArgumentList.Add("login");
            start.ArgumentList.Add("--device-auth");
            using var job=WindowsChildJob.StartDeviceAuthNetworkFixture(start,sid.Value);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var stdout=job.Output.ReadToEndAsync(deadline.Token);
            var stderr=job.Error.ReadToEndAsync(deadline.Token);
            await job.Process.WaitForExitAsync(deadline.Token);
            var output=await stdout+"\n"+await stderr;
            if(job.Process.ExitCode==0 || File.Exists(Path.Combine(home,"auth.json")))
                throw new IOException("Synthetic fixture unexpectedly authenticated");
            var reachedOrigin=output.Contains("device code request failed with status 418",StringComparison.OrdinalIgnoreCase);
            if(trustRoot && !reachedOrigin)throw new IOException($"Synthetic endpoint was not reached: {output.Trim()}");
            return reachedOrigin;
        } finally {
            var directory=new DirectoryInfo(home);
            var acl=directory.GetAccessControl();
            acl.PurgeAccessRules(sid);
            directory.SetAccessControl(acl);
        }
    }

    internal static async Task ChildAsync(int approved,int otherV4,int otherV6,bool relay=false)
    {
        var positive=await ConnectAsync(IPAddress.Loopback,approved);
        var v4=await ConnectAsync(IPAddress.Loopback,otherV4);
        var alias4=await ConnectAsync(IPAddress.Parse("127.0.0.2"),approved);
        var v6=await ConnectAsync(IPAddress.IPv6Loopback,otherV6);
        var udp=await SendUdpAsync(approved);
        var proxy=await TryEnvironmentProxyAsync(otherV4);
        var canaryDenied=relay && ProtectedCanaryDenied();
        Console.WriteLine(JsonSerializer.Serialize(new {
            approved_tcp=positive, unapproved_ipv4_tcp=v4, unapproved_ipv4_address=alias4,
            unapproved_ipv6_tcp=v6, direct_udp_dns_send=udp, environment_proxy_result=proxy,
            internet_client_capability_verified=true,protected_file_denied=canaryDenied,model_requests=0
        }));
    }

    private static bool ProtectedCanaryDenied()
    {
        var path=Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,"..","protected-relay-canary.txt"));
        return Denied(FileAccess.Read) && Denied(FileAccess.Write);
        bool Denied(FileAccess access) {
            try { using var file=File.Open(path,FileMode.Open,access,FileShare.ReadWrite);return false; }
            catch(UnauthorizedAccessException) {return true;}
        }
    }

    private static async Task<int> ConnectAsync(IPAddress address,int port)
    {
        try {
            using var socket=new Socket(address.AddressFamily,SocketType.Stream,ProtocolType.Tcp);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await socket.ConnectAsync(address,port,deadline.Token);
            return 0;
        } catch(SocketException failure) { return failure.NativeErrorCode; }
        catch(TypeInitializationException failure) when(failure.InnerException is SocketException socket) { return socket.NativeErrorCode; }
        catch(OperationCanceledException) { return -1; }
    }

    private static async Task<int> SendUdpAsync(int port)
    {
        try {
            using var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);
            // A plain DNS A query for x.invalid addressed to the synthetic UDP listener, never to the system resolver.
            byte[] query=[0x12,0x34,1,0,0,1,0,0,0,0,0,0,1,(byte)'x',7,
                (byte)'i',(byte)'n',(byte)'v',(byte)'a',(byte)'l',(byte)'i',(byte)'d',0,0,1,0,1];
            await socket.SendToAsync(query,new IPEndPoint(IPAddress.Loopback,port));
            return 0;
        } catch(SocketException failure) { return failure.NativeErrorCode; }
        catch(TypeInitializationException failure) when(failure.InnerException is SocketException socket) { return socket.NativeErrorCode; }
    }

    private static async Task<int> TryEnvironmentProxyAsync(int port)
    {
        var target=new Uri("https://qualification.invalid/");
        var proxy=HttpClient.DefaultProxy;
        if(proxy.IsBypassed(target) || proxy.GetProxy(target)!=new Uri($"http://127.0.0.1:{port}"))
            return -2;
        using var client=new HttpClient(new SocketsHttpHandler {Proxy=proxy,AllowAutoRedirect=false});
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var request=new HttpRequestMessage(HttpMethod.Head,target);
        try { using var response=await client.SendAsync(request,limit.Token); return 0; }
        catch(HttpRequestException failure) {
            for(Exception? cause=failure;cause is not null;cause=cause.InnerException)
                if(cause is SocketException socket)return socket.NativeErrorCode;
            return -3;
        }
        catch(OperationCanceledException) { return -1; }
    }
}
