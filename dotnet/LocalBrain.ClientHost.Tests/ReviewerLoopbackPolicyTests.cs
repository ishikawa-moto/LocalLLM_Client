using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

[SupportedOSPlatform("windows")]
internal static class ReviewerLoopbackPolicyTests
{
    internal static async Task RunAsync(string bundle,string packageSid,int approved,int otherV4,int otherV6)
    {
        bundle=Path.GetFullPath(bundle);
        ReviewQualification.GuardRoot(bundle);
        var sid=new SecurityIdentifier(packageSid);
        HostValidationCleanup.Grant(bundle,sid,false);
        try {
            var exe=Path.Combine(bundle,Path.GetFileName(Environment.ProcessPath!));
            var start=new ProcessStartInfo(exe) { UseShellExecute=false, WorkingDirectory=bundle };
            start.Environment.Clear();
            start.Environment["SystemRoot"]=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            start.Environment["DOTNET_ROOT"]=Path.Combine(bundle,"dotnet");
            foreach(var name in new[]{"USERPROFILE","LOCALAPPDATA","APPDATA","TEMP","TMP"})
                start.Environment[name]=bundle;
            start.Environment["HTTPS_PROXY"]=$"http://127.0.0.1:{otherV4}";
            foreach(var arg in new[]{"--review-loopback-policy-child",approved.ToString(),otherV4.ToString(),otherV6.ToString()})
                start.ArgumentList.Add(arg);
            using var job=WindowsChildJob.StartNetworkBoundaryFixture(start,sid.Value);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var stdout=job.Output.ReadToEndAsync(deadline.Token);
            var stderr=job.Error.ReadToEndAsync(deadline.Token);
            await job.Process.WaitForExitAsync(deadline.Token);
            Console.Write((await stdout).Trim());
            if(job.Process.ExitCode!=0)throw new IOException($"Network fixture exit {job.Process.ExitCode}: {await stderr}");
        } finally {
            var directory=new DirectoryInfo(bundle);
            var acl=directory.GetAccessControl();
            acl.PurgeAccessRules(sid);
            directory.SetAccessControl(acl);
        }
    }

    internal static async Task ChildAsync(int approved,int otherV4,int otherV6)
    {
        var positive=await ConnectAsync(IPAddress.Loopback,approved);
        var v4=await ConnectAsync(IPAddress.Loopback,otherV4);
        var alias4=await ConnectAsync(IPAddress.Parse("127.0.0.2"),approved);
        var v6=await ConnectAsync(IPAddress.IPv6Loopback,otherV6);
        var udp=await SendUdpAsync(approved);
        var proxy=await TryEnvironmentProxyAsync(otherV4);
        Console.WriteLine(JsonSerializer.Serialize(new {
            approved_tcp=positive, unapproved_ipv4_tcp=v4, unapproved_ipv4_address=alias4,
            unapproved_ipv6_tcp=v6, direct_udp_dns_send=udp, environment_proxy_result=proxy,
            internet_client_capability_verified=true,model_requests=0
        }));
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
