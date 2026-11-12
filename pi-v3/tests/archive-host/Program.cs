using System.Formats.Tar;
using System.Text;
using LocalBrain.ClientHost.AgentV3;

static MemoryStream Make(params (string Name, TarEntryType Type, string Data)[] entries) {
    var stream=new MemoryStream();
    using(var writer=new TarWriter(stream,leaveOpen:true)) foreach(var item in entries) {
        TarEntry entry=item.Name.Any(c=>c>127) ? new PaxTarEntry(item.Type,item.Name) : new UstarTarEntry(item.Type,item.Name);
        if(item.Type==TarEntryType.RegularFile) entry.DataStream=new MemoryStream(Encoding.UTF8.GetBytes(item.Data));
        if(item.Type==TarEntryType.SymbolicLink) entry.LinkName="../private";
        writer.WriteEntry(entry);
    }
    stream.Position=0;return stream;
}
static async Task Reject(params (string,TarEntryType,string)[] items) {
    using var stream=Make(items);
    try { await ArchiveInspection.InspectAsync(stream,100000,10,100,CancellationToken.None); }
    catch(InvalidDataException) { return; }
    throw new Exception("unsafe archive was accepted");
}
using(var stream=Make(("src",TarEntryType.Directory,""),("src/a.txt",TarEntryType.RegularFile,"hello"))) {
    var result=await ArchiveInspection.InspectAsync(stream,100000,5,5,CancellationToken.None);
    if(result.Count!=2 || result[1].Sha256!="2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824") throw new Exception("safe file changed");
}
await Reject(("../private",TarEntryType.RegularFile,"x"));
await Reject(("CON.txt",TarEntryType.RegularFile,"x"));
await Reject(("COM¹.txt",TarEntryType.RegularFile,"x"));
await Reject(("LPT²/file",TarEntryType.RegularFile,"x"));
await Reject(("CONIN$",TarEntryType.RegularFile,"x"));
foreach(var reserved in new[]{"COM¹.txt","LPT²/file","CONIN$"}) {
    try { ArchiveInspection.ValidateName(reserved,false); throw new Exception("reserved name accepted"); }
    catch(InvalidDataException) { }
}
await Reject(("link",TarEntryType.SymbolicLink,""));
await Reject(("a",TarEntryType.RegularFile,"x"),("A",TarEntryType.RegularFile,"y"));
await Reject(("a/b",TarEntryType.RegularFile,"x"),("a",TarEntryType.RegularFile,"y"));
await Reject((".LocalBrain/data",TarEntryType.RegularFile,"x"));
using(var stream=Make(("a",TarEntryType.RegularFile,"hello"))) {
    try { await ArchiveInspection.InspectAsync(stream,100000,2,4,CancellationToken.None); throw new Exception("byte limit bypassed"); }
    catch(InvalidDataException) { }
}
using(var stream=Make(("a",TarEntryType.RegularFile,"x"))) using(var stop=new CancellationTokenSource()) {
    stop.Cancel();
    try { await ArchiveInspection.InspectAsync(stream,100000,2,2,stop.Token); throw new Exception("cancellation ignored"); }
    catch(OperationCanceledException) { }
}
{
    var header=new byte[512];header[156]=(byte)'x';Encoding.ASCII.GetBytes("10000000000\0").CopyTo(header,124);
    using var stream=new MemoryStream(header);
    try { await ArchiveInspection.InspectAsync(stream,512,2,0,CancellationToken.None); throw new Exception("oversized PAX header accepted"); }
    catch(InvalidDataException) { }
}
try { ArchiveInspection.ValidateName(string.Join('/',Enumerable.Repeat("x",33)),false); throw new Exception("excessive path depth accepted"); }
catch(InvalidDataException) { }
if(args.Length==1) {
    using var stream=File.OpenRead(args[0]);
    var result=await ArchiveInspection.InspectAsync(stream,100000,3,100,CancellationToken.None);
    if(result.Count!=1 || result[0].Name!="日本語.txt")throw new Exception("Unicode PAX path was not retained");
}
Console.WriteLine("Host archive inspection safety fixtures passed");
