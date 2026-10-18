using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;
using LocalBrain.ClientHost.Ocr;

internal static class OcrSandboxTests
{
    internal static async Task PublicAsync(string assets){
        assets=Path.GetFullPath(assets);var root=Path.Combine(Path.GetTempPath(),"localbrain-ocr-public-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var scratch=Path.Combine(root,"scratch");Directory.CreateDirectory(scratch);
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;
        using(var control=new TcpClient()){await control.ConnectAsync(IPAddress.Loopback,port);using var accepted=await listener.AcceptTcpClientAsync();}
        var python=Path.Combine(assets,"python","python.exe");
        var script="""
            import socket,json,ctypes
            k=ctypes.WinDLL('kernel32');a=ctypes.WinDLL('advapi32');h=ctypes.c_void_p();n=ctypes.c_ulong();v=ctypes.c_ulong()
            k.GetCurrentProcess.restype=ctypes.c_void_p
            a.OpenProcessToken.argtypes=[ctypes.c_void_p,ctypes.c_ulong,ctypes.POINTER(ctypes.c_void_p)]
            assert a.OpenProcessToken(k.GetCurrentProcess(),8,ctypes.byref(h))
            assert a.GetTokenInformation(h,29,ctypes.byref(v),4,ctypes.byref(n))
            result={'is_appcontainer':v.value,'connections':[]}
            for host,port in [('127.0.0.1',PORT),('1.1.1.1',443)]:
                s=socket.socket();s.settimeout(2)
                try:s.connect((host,port));result['connections'].append({'blocked':False})
                except OSError as e:result['connections'].append({'blocked':True,'winerror':getattr(e,'winerror',None),'errno':e.errno,'error_type':type(e).__name__})
                finally:s.close()
            print(json.dumps(result))
            """;
        script=script.Replace("PORT",port.ToString());
        using var box=new OcrSandbox(assets,root,scratch,Guid.NewGuid().ToString("N"));
        var start=new ProcessStartInfo(python){UseShellExecute=false,WorkingDirectory=scratch,CreateNoWindow=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        start.ArgumentList.Add("-I");start.ArgumentList.Add("-B");start.ArgumentList.Add("-c");start.ArgumentList.Add(script);
        start.Environment.Clear();start.Environment["SystemRoot"]=Environment.GetFolderPath(Environment.SpecialFolder.Windows);start.Environment["TEMP"]=scratch;start.Environment["TMP"]=scratch;
        foreach(var key in new[]{"LOCALAPPDATA","APPDATA","ProgramData","USERPROFILE"})start.Environment[key]=scratch;
        start.Environment["PATH"]=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32");
        start.Environment["ProgramFiles"]=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        start.Environment["ProgramFiles(x86)"]=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        using var child=WindowsChildJob.Start(start,ocrLimits:new(768L*1024*1024,3,box.Sid));
        var stdout=child.Output.ReadToEndAsync();var stderr=child.Error.ReadToEndAsync();
        await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var output=await stdout;var error=await stderr;
        if(child.Process.ExitCode!=0)throw new Exception("Public OCR sandbox child failed: "+error[..Math.Min(error.Length,1000)]);
        using var result=JsonDocument.Parse(output);
        Console.WriteLine(output);
        var connections=result.RootElement.GetProperty("connections").EnumerateArray().ToArray();
        bool Denied(JsonElement c)=>c.GetProperty("winerror").ValueKind==JsonValueKind.Number&&c.GetProperty("winerror").GetInt32()==10013;
        if(result.RootElement.GetProperty("is_appcontainer").GetInt32()!=1||connections.Any(c=>!c.GetProperty("blocked").GetBoolean())||!Denied(connections[1])||listener.Pending())throw new Exception("OCR OS network restriction did not qualify");
        using(var control=new TcpClient()){await control.ConnectAsync(IPAddress.Loopback,port);using var accepted=await listener.AcceptTcpClientAsync();}
        Console.WriteLine(JsonSerializer.Serialize(new{status="PASS_OCR_REAL_APPCONTAINER_EXTERNAL_NETWORK_DENIED",result=result.RootElement,loopback="observed no accept; normal-user listener controls succeed before/after; timeout is not labelled explicit OS denial",normal_runtime_changed=false,private_data_used=false}));
    }
}
