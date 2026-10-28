using LocalBrain.ClientHost.AgentV3;

var id="00112233-4455-6677-8899-aabbccddeeff";
var stopArgs=V3ScopeControl.StopArguments("Ubuntu-24.04",id);
if(stopArgs.Length!=8 || stopArgs[0]!="-d" || stopArgs[1]!="Ubuntu-24.04" || stopArgs[6]!="--" || stopArgs[7]!="lb-v3-"+id+".scope")
    throw new Exception("scope command was not safely formed");
foreach(var name in new[]{"--system","Ubuntu;rm","Ubuntu abc",""}) {
    try { V3ScopeControl.StopArguments(name,id); throw new Exception("invalid distro accepted"); }
    catch(ArgumentException) { }
}
foreach(var task in new[]{"../bad","00112233445566778899aabbccddeeff","00112233-4455-6677-8899-AABBCCDDEEFF"}) {
    try { V3ScopeControl.StopArguments("Ubuntu",task); throw new Exception("invalid task accepted"); }
    catch(ArgumentException) { }
}
Console.WriteLine("Host WSL scope argument guards passed");
