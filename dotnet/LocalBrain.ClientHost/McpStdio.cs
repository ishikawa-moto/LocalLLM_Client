using System.Text;

namespace LocalBrain.ClientHost;

internal static class McpStdio
{
    public static void Configure()
    {
        // MCP stdio JSON is UTF-8 even when Windows inherits a legacy console code page.
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
    }
}
