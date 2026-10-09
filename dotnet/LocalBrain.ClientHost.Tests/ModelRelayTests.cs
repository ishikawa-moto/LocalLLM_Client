using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class ModelRelayTests
{
    internal static async Task RunAsync(string root)
    {
        root=Path.GetFullPath(root);if(Directory.Exists(root))throw new IOException("Fresh model-relay fixture required");Directory.CreateDirectory(root);
        var checks=new List<string>();var results=new List<object>();
        foreach(var stall in new[]{false,true})
        {
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;
            using var serverCancel=new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var server=Task.Run(async()=>{
                using var peer=await listener.AcceptTcpClientAsync(serverCancel.Token);await using var stream=peer.GetStream();
                var request=new List<byte>();var one=new byte[1];
                while(request.Count<8192){if(await stream.ReadAsync(one,serverCancel.Token)==0)throw new IOException("Request EOF");request.Add(one[0]);if(request.Count>=4 && Encoding.ASCII.GetString(request.TakeLast(4).ToArray())=="\r\n\r\n")break;}
                var header=Encoding.ASCII.GetString(request.ToArray());if(!header.StartsWith("POST /v1/chat/completions ",StringComparison.Ordinal) || !header.Contains("Content-Length: 2",StringComparison.OrdinalIgnoreCase))throw new Exception("Relay route or request framing incorrect");
                var frame=stall?"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n1\r\n{\r\n":"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(frame),serverCancel.Token);await stream.FlushAsync(serverCancel.Token);
                if(stall){try{await Task.Delay(Timeout.InfiniteTimeSpan,serverCancel.Token);}catch(OperationCanceledException){}}
            });
            var originalIn=Console.In;var originalOut=Console.Out;var originalExit=Environment.ExitCode;
            using var input=new StringReader(JsonSerializer.Serialize(new{method="POST",path="/v1/chat/completions",contentType="application/json",bodyBase64=Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"))})+"\n");
            using var output=new StringWriter();var watch=Stopwatch.StartNew();int exit;
            try{Console.SetIn(input);Console.SetOut(output);await ModelRelay.RunFixtureAsync(port,TimeSpan.FromSeconds(1));exit=Environment.ExitCode;}
            finally{Console.SetIn(originalIn);Console.SetOut(originalOut);Environment.ExitCode=originalExit;serverCancel.Cancel();listener.Stop();await server;}
            using var result=JsonDocument.Parse(output.ToString());
            if(stall){if(exit!=1 || result.RootElement.GetProperty("error").GetString()!="model_relay_failed" || watch.ElapsedMilliseconds>5000)throw new Exception("Headers-read stalled body escaped total deadline");checks.Add("body_stall_after_headers_has_total_deadline");}
            else{if(exit!=0 || result.RootElement.GetProperty("status").GetInt32()!=200 || Encoding.UTF8.GetString(Convert.FromBase64String(result.RootElement.GetProperty("body_base64").GetString()!))!="{}")throw new Exception("Normal relay response changed");checks.Add("normal_loopback_response_and_content_length");}
            results.Add(new{stall,exit,elapsed_ms=watch.ElapsedMilliseconds,response=result.RootElement.Clone()});
        }
        var proof=new{status="PASS_MODEL_RELAY_TOTAL_RESPONSE_DEADLINE",checks,results,fixture_deadline_seconds=1,production_deadline_seconds=540,loopback_only=true,production_changed=false};
        File.WriteAllText(Path.Combine(root,"proof.json"),JsonSerializer.Serialize(proof));Console.WriteLine(JsonSerializer.Serialize(proof));
    }
}
