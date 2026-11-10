using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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

    internal static async Task SystemDnsHostAsync(string bundle,string packageSid,int dnsServicePid,string traceName,
        int relayPort,string capability,int alternatePort)
    {
        bundle=Path.GetFullPath(bundle);
        ReviewQualification.GuardRoot(bundle);
        var sid=new SecurityIdentifier(packageSid);
        HostValidationCleanup.Grant(bundle,sid,false);
        try {
            var controlName="control-"+Guid.NewGuid().ToString("N")+".test.";
            var isolatedName="isolated-"+Guid.NewGuid().ToString("N")+".test.";
            var traceStart=new ProcessStartInfo(Path.Combine(bundle,"dns-trace","DnsTrace.exe")) {
                UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true
            };
            foreach(var arg in new[]{dnsServicePid.ToString(),Environment.ProcessId.ToString(),controlName,isolatedName,traceName})
                traceStart.ArgumentList.Add(arg);
            using var trace=Process.Start(traceStart)??throw new IOException("Could not launch DNS trace fixture");
            using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(29));
            var traceErrors=trace.StandardError.ReadToEndAsync(limit.Token);
            try {
                if(await trace.StandardOutput.ReadLineAsync(limit.Token)!="DNS_TRACE_READY")
                    throw new IOException("DNS trace fixture did not start: "+await traceErrors);
                var control=await SystemDnsStatusAsync(controlName);
                var start=IsolatedStart(Path.Combine(bundle,Path.GetFileName(Environment.ProcessPath!)),bundle);
                start.Environment["DOTNET_ROOT"]=Path.Combine(bundle,"dotnet");
                foreach(var arg in new[]{"--review-loopback-policy-child","system-dns",isolatedName,relayPort.ToString(),alternatePort.ToString()})
                    start.ArgumentList.Add(arg);
                using var job=capability switch {
                    "internet"=>WindowsChildJob.StartNetworkBoundaryFixture(start,sid.Value),
                    "private"=>WindowsChildJob.StartPrivateNetworkDnsFixture(start,sid.Value),
                    "offline" or "offline-permit" or "offline-wfp"=>WindowsChildJob.StartOfflineIsolatedReviewer(start,sid.Value,allowSystemRegistryRead:true),
                    _=>throw new ArgumentException("Unknown DNS fixture capability")
                };
                var isolated=job.Output.ReadToEndAsync(limit.Token);
                var errors=job.Error.ReadToEndAsync(limit.Token);
                await job.Process.WaitForExitAsync(limit.Token);
                if(job.Process.ExitCode!=0)throw new IOException("DNS child failed: "+await errors);
                using var trial=JsonDocument.Parse(await isolated);
                trace.StandardInput.Close();
                var raw=await trace.StandardOutput.ReadToEndAsync(limit.Token);
                await trace.WaitForExitAsync(limit.Token);
                if(trace.ExitCode!=0)throw new IOException("DNS trace fixture failed: "+await traceErrors);
                using var evidence=JsonDocument.Parse(raw);
                Console.WriteLine(JsonSerializer.Serialize(new {capability,dns_service_pid=dnsServicePid,control_pid=Environment.ProcessId,
                    control_dns_status=control,isolated=trial.RootElement,trace=evidence.RootElement,model_requests=0}));
            } finally {
                if(!trace.HasExited) {
                    trace.StandardInput.Close();
                    if(!trace.WaitForExit(5000)) {trace.Kill();trace.WaitForExit();}
                }
            }
        } finally {
            var directory=new DirectoryInfo(bundle);
            var acl=directory.GetAccessControl();
            acl.PurgeAccessRules(sid);
            directory.SetAccessControl(acl);
        }
    }

    internal static async Task SystemDnsChildAsync(string name,int relayPort,int alternatePort)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(name,@"^isolated-[0-9a-f]{32}\.test\.$"))
            throw new ArgumentException("Expected a synthetic DNS name");
        if(relayPort is <49152 or >65535 || alternatePort is <49152 or >65535 || relayPort==alternatePort)
            throw new ArgumentException("Expected distinct trial listener ports");
        var loopback=await ConnectAsync(IPAddress.Loopback,relayPort);
        var alternate=await ConnectAsync(IPAddress.Loopback,alternatePort);
        Console.WriteLine(JsonSerializer.Serialize(new {pid=Environment.ProcessId,loopback_tcp=loopback,
            alternate_tcp=alternate,dns_status=await SystemDnsStatusAsync(name)}));
    }

    private static async Task<int> SystemDnsStatusAsync(string name)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(7));
        try { await Dns.GetHostAddressesAsync(name,deadline.Token);return 0; }
        catch(SocketException failure) {return failure.NativeErrorCode;}
        catch(OperationCanceledException) {return -1;}
    }

    internal static async Task NativeDnsHostAsync(string bundle,string packageSid)
    {
        bundle=Path.GetFullPath(bundle);
        ReviewQualification.GuardRoot(bundle);
        var sid=new SecurityIdentifier(packageSid);
        HostValidationCleanup.Grant(bundle,sid,false);
        try {
            using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(27));
            using var control=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
            using var isolated=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
            var controlName="control-"+Guid.NewGuid().ToString("N");
            var isolatedName="isolated-"+Guid.NewGuid().ToString("N");
            var controlServer=ReplyToProbeAsync(control,controlName,limit.Token);
            int controlResult;
            try {controlResult=await Task.Run(()=>NativeDnsQuery(Port(control),controlName)).WaitAsync(TimeSpan.FromSeconds(6),limit.Token);}
            catch(TimeoutException) {controlResult=-1;}
            var controlObserved=controlServer.IsCompletedSuccessfully && await controlServer;
            var trialServer=ReplyToProbeAsync(isolated,isolatedName,limit.Token);
            var start=IsolatedStart(Path.Combine(bundle,Path.GetFileName(Environment.ProcessPath!)),bundle);
            start.Environment["DOTNET_ROOT"]=Path.Combine(bundle,"dotnet");
            foreach(var arg in new[]{"--review-loopback-policy-child","native-dns",Port(isolated).ToString(),isolatedName})
                start.ArgumentList.Add(arg);
            using var job=WindowsChildJob.StartNetworkBoundaryFixture(start,sid.Value);
            var stdout=job.Output.ReadToEndAsync(limit.Token);
            var stderr=job.Error.ReadToEndAsync(limit.Token);
            await job.Process.WaitForExitAsync(limit.Token);
            if(job.Process.ExitCode!=0)throw new IOException($"DNS fixture exit {job.Process.ExitCode}: {await stderr}");
            using var probe=JsonDocument.Parse(await stdout);
            var reached=trialServer.IsCompletedSuccessfully && await trialServer;
            Console.WriteLine(JsonSerializer.Serialize(new {probe=probe.RootElement,
                dns_control_status=controlResult,dns_control_observed=controlObserved,isolated_query_reached_listener=reached,model_requests=0}));
            await limit.CancelAsync();
        } finally {
            var directory=new DirectoryInfo(bundle);
            var acl=directory.GetAccessControl();
            acl.PurgeAccessRules(sid);
            directory.SetAccessControl(acl);
        }
    }

    internal static async Task NativeDnsChildAsync(int port,string label)
    {
        var direct4=await ConnectAsync(IPAddress.Parse("198.51.100.17"),443);
        var direct6=await ConnectAsync(IPAddress.Parse("2001:db8::17"),443);
        int dnsStatus;
        try {dnsStatus=await Task.Run(()=>NativeDnsQuery(port,label)).WaitAsync(TimeSpan.FromSeconds(11));}
        catch(TimeoutException) {dnsStatus=-1;}
        Console.WriteLine(JsonSerializer.Serialize(new {direct_ipv4=direct4,direct_ipv6=direct6,windows_dns_status=dnsStatus}));
    }

    private static int Port(UdpClient listener)=>((IPEndPoint)listener.Client.LocalEndPoint!).Port;

    private static async Task<bool> ReplyToProbeAsync(UdpClient listener,string label,CancellationToken token)
    {
        var question=new byte[]{(byte)label.Length}.Concat(System.Text.Encoding.ASCII.GetBytes(label))
            .Concat(new byte[]{7,(byte)'i',(byte)'n',(byte)'v',(byte)'a',(byte)'l',(byte)'i',(byte)'d',0,0,1,0,1}).ToArray();
        var datagram=await listener.ReceiveAsync(token);
        var query=datagram.Buffer;
        if(query.Length<12+question.Length || !query.AsSpan(12,question.Length).SequenceEqual(question))return false;
        byte[] answer=[0xc0,12,0,1,0,1,0,0,0,0,0,4,192,0,2,123];
        byte[] reply=[query[0],query[1],0x81,0x80,0,1,0,1,0,0,0,0,..question,..answer];
        await listener.SendAsync(reply,datagram.RemoteEndPoint,token);
        return true;
    }

    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct DnsRequest
    {
        public uint Version;
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        public ushort Type;
        public ulong Options;
        public IntPtr Servers;
        public uint Interface;
        public IntPtr Callback,Context;
        public uint IsNetworkQueryRequired,RequiredNetworkIndex,CustomServersCount;
        public IntPtr CustomServers;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DnsResult
    {
        public uint Version;
        public int Status;
        public ulong Options;
        public IntPtr Records,Reserved;
    }
    [DllImport("dnsapi.dll")] private static extern int DnsQueryEx(ref DnsRequest request,ref DnsResult result,IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern void DnsFree(IntPtr records,int type);

    private static int NativeDnsQuery(int port,string label)
    {
        if(port is <49152 or >65535 || !System.Text.RegularExpressions.Regex.IsMatch(label,"^(control|isolated)-[0-9a-f]{32}$"))
            throw new ArgumentException("Unexpected DNS fixture endpoint or label");
        // DNS_CUSTOM_SERVER (UDP) with the SOCKADDR_IN stored in MaxSa.
        var server=new byte[56];
        server[0]=1;
        server[24]=2;
        server[26]=(byte)(port>>8);
        server[27]=(byte)port;
        server[28]=127;
        server[31]=1;
        var pinned=GCHandle.Alloc(server,GCHandleType.Pinned);
        var result=new DnsResult {Version=1};
        try {
            var request=new DnsRequest {Version=3,Name=label+".invalid.",Type=1,
                Options=0x8|0x800|0x1000,CustomServersCount=1,CustomServers=pinned.AddrOfPinnedObject()};
            var status=DnsQueryEx(ref request,ref result,IntPtr.Zero);
            return status!=0?status:result.Status;
        } finally {
            if(result.Records!=IntPtr.Zero)DnsFree(result.Records,1);
            pinned.Free();
        }
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
