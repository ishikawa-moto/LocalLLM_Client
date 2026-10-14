using System.Net;
using System.Text.Json;
using LocalBrain.ClientHost;

internal static class McpServerTests
{
    public static async Task RunAsync()
    {
        var input = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}
        {"jsonrpc":"2.0","method":"notifications/initialized"}
        {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"brain_get","arguments":{"chunk_id":"missing-fixture"}}}
        {"jsonrpc":"2.0","id":"string-error-id","method":"tools/call","params":{"name":"brain_get","arguments":{"chunk_id":"missing-fixture"}}}
        {"jsonrpc":"2.0","id":0,"method":"tools/call","params":{"name":"unknown-fixture-tool","arguments":{}}}
        {"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"brain_status","arguments":{}}}
        {"jsonrpc":"2.0","id":5,"method":"tools/call","params":{}}
        not valid json
        {"jsonrpc":"2.0","id":7,"method":"tools/list","params":{}}
        """;
        using var gateway = new GatewayClient(new Uri("https://gateway.invalid/"), new FixtureHandler());
        using var output = new StringWriter();
        var oldIn = Console.In;
        var oldOut = Console.Out;
        try
        {
            Console.SetIn(new StringReader(input));
            Console.SetOut(output);
            await new McpServer(gateway).RunAsync();
        }
        finally { Console.SetIn(oldIn); Console.SetOut(oldOut); }
        var rows = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Check(rows.Length == 8, "Initialized notification must not produce a response");
            Check(rows[1].RootElement.GetProperty("id").GetInt32() == 2 && rows[1].RootElement.TryGetProperty("error", out _), "HTTP error retains numeric request ID");
            Check(rows[2].RootElement.GetProperty("id").GetString() == "string-error-id", "String error ID retained after request document disposal");
            Check(rows[3].RootElement.GetProperty("id").GetInt32() == 0, "Zero request ID is retained");
            Check(rows[4].RootElement.GetProperty("id").GetInt32() == 4 && rows[4].RootElement.TryGetProperty("result", out _), "Status succeeds after an error");
            Check(rows[5].RootElement.GetProperty("id").GetInt32() == 5, "Malformed tool parameters retain the known request ID");
            Check(rows[6].RootElement.GetProperty("id").ValueKind == JsonValueKind.Null, "Malformed JSON does not reuse the previous request ID");
            Check(rows[7].RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() == 23, "Public MCP includes bound preparation");
            var tools=rows[7].RootElement.GetProperty("result").GetProperty("tools");
            foreach(var name in new[]{"brain_prepare_decision","brain_propose_decision"}) {
                var tool=tools.EnumerateArray().Single(t=>t.GetProperty("name").GetString()==name);
                var modes=tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("request_mode").GetProperty("enum");
                Check(modes.EnumerateArray().Select(x=>x.GetString()).SequenceEqual(new[]{"proposal","user_request"}),"Both preparation entry points expose the exact human-request declaration");
                Check(tool.GetProperty("description").GetString()!.Contains("brain_decision_status",StringComparison.Ordinal),"Saving tools require actual status before claiming saved");
            }
        }
        finally { foreach (var row in rows) row.Dispose(); }
        Console.WriteLine("MCP error request IDs, error-then-status continuity and unchanged tool contract: 8 checks PASS; fixture HTTP only.");
    }

    private static void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var missing = request.RequestUri!.AbsolutePath == "/v1/brain/get";
            return Task.FromResult(new HttpResponseMessage(missing ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new StringContent(missing ? "{\"error\":\"Unknown fixture chunk\"}" : "{\"status\":\"ok\"}")
            });
        }
    }
}
