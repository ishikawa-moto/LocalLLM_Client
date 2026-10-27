using System.Text;
using LocalBrain.ClientHost.AgentV3;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Reject(byte[] bytes, int max=1024) {
    using var stream = new MemoryStream(bytes);
    var reader = new PiJsonlReader(stream,max);
    try { await reader.ReadAsync(CancellationToken.None); }
    catch (InvalidDataException) { return; }
    throw new Exception("invalid record was accepted");
}
var raw=Encoding.UTF8.GetBytes("{\"type\":\"event\",\"text\":\"x\u2028y\"}\r\n{\"id\":\"second\"}\n");
using(var stream=new MemoryStream(raw)) {
    var reader=new PiJsonlReader(stream,1024);
    var first=await reader.ReadAsync(CancellationToken.None);
    var second=await reader.ReadAsync(CancellationToken.None);
    Check(first?.GetProperty("text").GetString()=="x\u2028y", "Unicode line separator split a record");
    Check(second?.GetProperty("id").GetString()=="second", "second record lost");
    Check(await reader.ReadAsync(CancellationToken.None) is null,"EOF is not clean");
}
await Reject(Encoding.UTF8.GetBytes("{bad}\n"));
await Reject(Encoding.UTF8.GetBytes("[]\n"));
await Reject(Encoding.UTF8.GetBytes("{\"a\":1}"));
await Reject(new byte[]{0xff,0x0a});
await Reject(Encoding.UTF8.GetBytes("{\"too\":\"long\"}\n"),8);
Console.WriteLine("Pi RPC framing fixtures passed");
