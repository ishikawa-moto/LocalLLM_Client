using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

if(args.Length!=5 || !int.TryParse(args[0],out var servicePid) || servicePid<=0 ||
    !int.TryParse(args[1],out var controlPid) || controlPid<=0 ||
    !Regex.IsMatch(args[2],@"^control-[0-9a-f]{32}\.test\.$") ||
    !Regex.IsMatch(args[3],@"^isolated-[0-9a-f]{32}\.test\.$") ||
    !Regex.IsMatch(args[4],@"^LocalBrain\.DnsTrial\.[0-9a-f]{32}$"))
    throw new ArgumentException("Expected DNS service PID, control PID, and two synthetic names");

var provider=new Guid("1c95126e-7eea-49a9-a3fe-a378b03ddb4d");
var events=new ConcurrentQueue<DnsObservation>();
int[] ids=[3006,3008,3009,3010,3011,3016,3018];
using var session=new TraceEventSession(args[4]);
session.StopOnDispose=true;
session.EnableProvider(provider,TraceEventLevel.Verbose,ulong.MaxValue,new TraceEventProviderOptions {
    ProcessIDFilter=[servicePid,controlPid],EventIDsToEnable=ids
});
var source=session.Source;
var parser=new RegisteredTraceEventParser(source);
parser.All+=trace=>{
    if(trace.ProviderGuid!=provider || !ids.Contains((int)trace.ID))return;
    var name=(trace.PayloadByName("QueryName") as string)?.TrimEnd('.');
    var kind=string.Equals(name,args[2].TrimEnd('.'),StringComparison.OrdinalIgnoreCase)?"control":
        string.Equals(name,args[3].TrimEnd('.'),StringComparison.OrdinalIgnoreCase)?"isolated":null;
    if(kind is null)return;
    events.Enqueue(new(kind,(int)trace.ID,trace.ProcessID,
        Number("ClientPID"),Number("ResponseStatus")??Number("QueryStatus")??Number("Status")));
    int? Number(string field)=>int.TryParse(trace.PayloadByName(field)?.ToString(),out var number)?number:null;
};
var read=Task.Run(source.Process);
Console.WriteLine("DNS_TRACE_READY");
await Console.Out.FlushAsync();
using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(22));
try {
    if(await Console.In.ReadLineAsync(deadline.Token) is not null)
        throw new IOException("Only end-of-stream is accepted");
} finally {
    session.Stop();
}
await read.WaitAsync(TimeSpan.FromSeconds(5));
Console.WriteLine(JsonSerializer.Serialize(new {events_lost=source.EventsLost,observations=events,etl_written=false}));

internal sealed record DnsObservation(string kind,int id,int emitter_pid,int? client_pid,int? status);
