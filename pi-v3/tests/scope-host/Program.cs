using LocalBrain.ClientHost.AgentV3;

var id="00112233-4455-6677-8899-aabbccddeeff";
var generation="ffeeddcc-bbaa-9988-7766-554433221100";
var stopArgs=V3ScopeControl.StopArguments("Ubuntu-24.04",id,generation);
if(stopArgs.Length!=8 || stopArgs[0]!="-d" || stopArgs[1]!="Ubuntu-24.04" || stopArgs[6]!="--" || stopArgs[7]!="lb-v3-"+id+"-"+generation+".scope")
    throw new Exception("scope command was not safely formed");
if(V3ScopeControl.ScopeName(id,generation)==V3ScopeControl.ScopeName(id,Guid.NewGuid().ToString("D")))
    throw new Exception("different generations reused the same scope name");
foreach(var name in new[]{"--system","Ubuntu;rm","Ubuntu abc",""}) {
    try { V3ScopeControl.StopArguments(name,id,generation); throw new Exception("invalid distro accepted"); }
    catch(ArgumentException) { }
}
foreach(var task in new[]{"../bad","00112233445566778899aabbccddeeff","00112233-4455-6677-8899-AABBCCDDEEFF"}) {
    try { V3ScopeControl.StopArguments("Ubuntu",task,generation); throw new Exception("invalid task accepted"); }
    catch(ArgumentException) { }
}
foreach(var invalid in new[]{"../bad","00112233445566778899aabbccddeeff","FFEEDDCC-BBAA-9988-7766-554433221100"}) {
    try { V3ScopeControl.StopArguments("Ubuntu",id,invalid); throw new Exception("invalid generation accepted"); }
    catch(ArgumentException) { }
}
Console.WriteLine("Host WSL scope argument guards passed");
