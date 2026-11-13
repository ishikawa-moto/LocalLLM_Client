using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.AgentV2;

internal static class McpStdioTests
{
    internal static async Task AgentEntryAsync(string directory,string repo,ClientConfig config)
    {
        var configPath=Path.Combine(directory,"agent-stdio-config.json");
        File.WriteAllText(configPath,JsonSerializer.Serialize(config,ClientConfig.JsonOptions));
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"localbrain.exe")){
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8};
        start.ArgumentList.Add("agent-v2-mcp");start.Environment["LOCALBRAIN_CLIENT_CONFIG"]=configPath;
        using var child=Process.Start(start)??throw new Exception("Agent MCP pipe fixture did not launch");
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try{
            var text=string.Concat(Enumerable.Repeat("日本語の読み取り検証・引用\"と改行\n・絵文字🐕。",180));
            var payload=JsonSerializer.Serialize(new{jsonrpc="2.0",id="unicode_agent_731",method="tools/call",
                @params=new{name="localbrain_agent_v2_investigate",arguments=new{workspace=repo,task=new{read_only=true,requirement=text}}}},
                new JsonSerializerOptions{Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
            await child.StandardInput.WriteLineAsync(payload.AsMemory(),stop.Token);
            // Keep stdin open, as Continue does. Invalid scope must produce a correlated error before EOF, without starting work.
            var line=await child.StandardOutput.ReadLineAsync(stop.Token)??throw new Exception("No Agent MCP response");
            using var reply=JsonDocument.Parse(line);
            if(reply.RootElement.GetProperty("id").GetString()!="unicode_agent_731" ||
                !reply.RootElement.GetProperty("error").GetProperty("message").GetString()!.StartsWith("InvalidDataException:",StringComparison.Ordinal))
                throw new Exception("Actual Agent MCP entry corrupted Unicode or lost the request ID");
            child.StandardInput.Close();await child.WaitForExitAsync(stop.Token);
            Console.WriteLine("Actual Agent MCP UTF-8 entry PASS: Japanese/emoji >4KiB, open input pipe, correlated invalid-scope response and no task dispatch.");
        }finally{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}}
    }
    public static async Task FixtureAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Console.InputEncoding = Encoding.GetEncoding(932);
        }
        McpStdio.Configure();
        var line = await Console.In.ReadLineAsync() ?? throw new InvalidOperationException("No fixture input");
        using var doc = JsonDocument.Parse(line);
        Console.WriteLine(JsonSerializer.Serialize(new {
            id = doc.RootElement.GetProperty("id").Clone(),
            echo = doc.RootElement.GetProperty("content").GetString(),
            input_code_page = Console.InputEncoding.CodePage,
            output_code_page = Console.OutputEncoding.CodePage
        }));
    }

    public static async Task RunAsync()
    {
        var text = string.Concat(Enumerable.Repeat("日本語のインシデント。引用\"と改行\nWindows\\path・絵文字🐕。", 180));
        var wire = JsonSerializer.Serialize(new { id = "unicode-fixture", content = text },
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var info = new ProcessStartInfo(Environment.ProcessPath!) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(McpStdioTests).Assembly.Location);
        info.ArgumentList.Add("--mcp-stdio-fixture");
        using var child = Process.Start(info) ?? throw new InvalidOperationException("No UTF-8 fixture child");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var output = child.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = child.StandardError.ReadToEndAsync(deadline.Token);
            await child.StandardInput.WriteLineAsync(wire.AsMemory(), deadline.Token);
            child.StandardInput.Close();
            await child.WaitForExitAsync(deadline.Token);
            var raw = await output;
            if (child.ExitCode != 0) throw new InvalidOperationException("UTF-8 fixture failed: " + await error);
            using var response = JsonDocument.Parse(raw);
            if (response.RootElement.GetProperty("id").GetString() != "unicode-fixture" ||
                response.RootElement.GetProperty("echo").GetString() != text ||
                response.RootElement.GetProperty("input_code_page").GetInt32() != 65001 ||
                response.RootElement.GetProperty("output_code_page").GetInt32() != 65001)
                throw new InvalidOperationException("Redirected MCP Unicode payload changed");
            Console.WriteLine("MCP real UTF-8 pipe: Japanese/emoji/quotes/newlines/backslashes, >4KiB and inherited Windows CP932 override PASS; no backend request.");
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
    }
}
