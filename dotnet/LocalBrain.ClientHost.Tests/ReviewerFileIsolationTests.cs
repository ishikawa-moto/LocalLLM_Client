using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;
using LocalBrain.ClientHost.Ocr;
using Microsoft.Win32.SafeHandles;

[SupportedOSPlatform("windows")]
internal static class ReviewerFileIsolationTests
{
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,
        uint disposition,uint flags,IntPtr template);

    internal static async Task RunAsync(string root,bool allowSystemRegistryRead)
    {
        root=Path.GetFullPath(root);
        ReviewQualification.GuardRoot(root);
        var bundle=Path.Combine(root,"bundle");
        var exe=Path.Combine(bundle,Path.GetFileName(Environment.ProcessPath!));
        var input=Directory.CreateDirectory(Path.Combine(root,"input")).FullName;
        var scratch=Directory.CreateDirectory(Path.Combine(root,"scratch")).FullName;
        var protectedRoot=Directory.CreateDirectory(Path.Combine(root,"protected")).FullName;
        var packet=Path.Combine(input,"approved.txt");
        var canary=Path.Combine(protectedRoot,"canary.txt");
        var marker=Path.Combine(scratch,"marker.txt");
        var approved="approved public fixture\n"u8.ToArray();
        File.WriteAllBytes(packet,approved);
        File.WriteAllText(canary,"protected synthetic canary");
        var acl=new FileInfo(canary).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-2-1"),FileSystemRights.Read,AccessControlType.Allow));
        new FileInfo(canary).SetAccessControl(acl);
        using var listener=new TcpListener(IPAddress.Loopback,0);
        listener.Start();
        var port=((IPEndPoint)listener.LocalEndpoint).Port;
        using (var positive=new TcpClient())await positive.ConnectAsync(IPAddress.Loopback,port);
        var pipeName="localbrain.fixture."+Guid.NewGuid().ToString("N");
        var pipePath=@"\\.\pipe\"+pipeName;
        using (var control=new NamedPipeServerStream(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.CurrentUserOnly))
            if(OpenCode(pipePath,0xC0000000,3)!=0)throw new IOException("Host could not open the broker control pipe");
        using var broker=new NamedPipeServerStream(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.CurrentUserOnly);
        if(OpenCode(canary,0x80000000,3)!=0 || OpenCode(canary,0x40000000,3)!=0)
            throw new IOException("Canary controls were not accessible to the Host");

        using var container=new OcrSandbox(bundle,input,scratch,Guid.NewGuid().ToString("N"));
        var start=new ProcessStartInfo(exe) {UseShellExecute=false,WorkingDirectory=input,RedirectStandardInput=true};
        ConfigureEnvironment(start,bundle,scratch);
        foreach(var arg in new[]{"--review-file-isolation-child",packet,canary,marker,port.ToString(),pipePath})start.ArgumentList.Add(arg);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var job=WindowsChildJob.StartOfflineIsolatedReviewer(start,container.Sid,allowSystemRegistryRead);
        var output=job.Output.ReadToEndAsync(deadline.Token);
        var error=job.Error.ReadToEndAsync(deadline.Token);
        await job.Input!.WriteAsync(approved,deadline.Token);
        job.Input.Dispose();
        await job.Process.WaitForExitAsync(deadline.Token);
        var stdout=await output;
        var stderr=await error;
        if(job.Process.ExitCode!=0 || string.IsNullOrWhiteSpace(stdout))
            throw new IOException($"Isolated fixture exit {job.Process.ExitCode}: {stderr}");
        using var doc=JsonDocument.Parse(stdout);
        var proof=doc.RootElement;
        var hash=Convert.ToHexString(SHA256.HashData(approved));
        var passed=proof.GetProperty("packet_hash").GetString()==hash && proof.GetProperty("stdin_hash").GetString()==hash &&
            proof.GetProperty("packet_write").GetInt32()==5 && proof.GetProperty("canary_read").GetInt32()==5 &&
            proof.GetProperty("canary_write").GetInt32()==5 && proof.GetProperty("new_outside").GetInt32()==5 &&
            proof.GetProperty("child").GetInt32()==367 && proof.GetProperty("loopback").GetInt32()>0 &&
            proof.GetProperty("broker_pipe").GetInt32() is 2 or 5 && !broker.IsConnected &&
            !File.Exists(marker) && !File.Exists(canary+".new") && File.ReadAllText(canary)=="protected synthetic canary";
        var sol=Path.Combine(bundle,"codex.exe");
        object? solStartup=null;
        if(passed && File.Exists(sol)) {
            var login=new ProcessStartInfo(sol) {UseShellExecute=false,WorkingDirectory=input};
            ConfigureEnvironment(login,bundle,scratch);
            login.Environment["CODEX_HOME"]=Directory.CreateDirectory(Path.Combine(scratch,"sol-home")).FullName;
            var auth=await ReviewerCli.InspectOfflineSolLoginAsync(login,container.Sid,allowSystemRegistryRead,deadline.Token);
            var message=auth.Answer;
            solStartup=new{exit_code=auth.ExitCode,expected_not_signed_in=message.Contains("Not logged in",StringComparison.OrdinalIgnoreCase),
                executable_sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sol))),auth_fixture_empty=true,
                panic_observed=message.Contains("panicked",StringComparison.OrdinalIgnoreCase),
                socket_error_observed=message.Contains("10107",StringComparison.Ordinal)};
        }
        var result=JsonSerializer.Serialize(new{status=passed?"PASS":"FAIL",model_requests=0,
            canary_read_granted_to_all_application_packages=true,loopback_host_control_connected=true,
            system_registry_read=allowSystemRegistryRead,proof,sol_startup=solStartup});
        Console.WriteLine(result);
        if(!passed)throw new IOException("Unexpected Windows isolation result");
    }

    private static void ConfigureEnvironment(ProcessStartInfo start,string bundle,string scratch)
    {
        start.Environment.Clear();
        start.Environment["SystemRoot"]=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        start.Environment["DOTNET_ROOT"]=Path.Combine(bundle,"dotnet");
        foreach(var name in new[]{"TEMP","TMP","USERPROFILE","LOCALAPPDATA","APPDATA"})
            start.Environment[name]=scratch;
    }

    internal static async Task ChildAsync(string packet,string canary,string marker,int port,string pipe)
    {
        using var stdin=new MemoryStream();
        await Console.OpenStandardInput().CopyToAsync(stdin);
        var packetHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packet)));
        var packetWrite=OpenCode(packet,0x40000000,3);
        var canaryRead=OpenCode(canary,0x80000000,3);
        var canaryWrite=OpenCode(canary,0x40000000,3);
        var newOutside=OpenCode(canary+".new",0x40000000,1);
        var brokerPipe=OpenCode(pipe,0xC0000000,3);
        var child=0;
        try {
            var start=new ProcessStartInfo(Environment.ProcessPath!) {UseShellExecute=false};
            start.ArgumentList.Add("--review-child-policy-marker");
            start.ArgumentList.Add(marker);
            using var spawned=Process.Start(start)!;
            await spawned.WaitForExitAsync();
        } catch(Win32Exception failure) {child=failure.NativeErrorCode;}
        var loopback=0;
        var socketStage="socket_initialization";
        try {
            using var tcp=new TcpClient();
            socketStage="connect";
            using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await tcp.ConnectAsync(IPAddress.Loopback,port,limit.Token);
        } catch(SocketException failure) {loopback=failure.NativeErrorCode;}
        catch(TypeInitializationException failure) when(failure.InnerException is SocketException socket) {loopback=socket.NativeErrorCode;}
        catch(OperationCanceledException) {loopback=-1;}
        Console.WriteLine(JsonSerializer.Serialize(new{packet_hash=packetHash,
            stdin_hash=Convert.ToHexString(SHA256.HashData(stdin.ToArray())),packet_write=packetWrite,
            canary_read=canaryRead,canary_write=canaryWrite,new_outside=newOutside,child,loopback,
            socket_stage=socketStage,broker_pipe=brokerPipe}));
    }

    private static int OpenCode(string path,uint access,uint disposition)
    {
        using var handle=CreateFileW(path,access,7,IntPtr.Zero,disposition,0,IntPtr.Zero);
        return handle.IsInvalid?Marshal.GetLastWin32Error():0;
    }
}
