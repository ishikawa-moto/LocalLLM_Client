using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LocalBrain.ClientHost;

internal static class McpStdioTests
{
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
