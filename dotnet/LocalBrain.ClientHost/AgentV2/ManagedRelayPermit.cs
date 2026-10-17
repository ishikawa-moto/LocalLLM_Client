using System.Security.Cryptography;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

// A one-use Host capability, not a caller's untrusted 'managed' HTTP header.
internal static class ManagedRelayPermit
{
    internal static string Root=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LocalBrain","managed-relay-permits");
    internal static string Issue(string hash,string taskId)
    {
        CanonicalStore.GuardPath(Root);Directory.CreateDirectory(Root);
        if(Directory.EnumerateFiles(Root).Take(10001).Count()>10000)throw new IOException("Managed relay permit storage budget exhausted");
        var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        TaskCommandGate.WriteNew(Path.Combine(Root,token+".json"),new{body_hash=hash,task_id=taskId,expires=DateTimeOffset.UtcNow.AddMinutes(2)});return token;
    }
    internal static bool Consume(string? token,byte[] body)
    {
        if(token is null)return false;
        if(!System.Text.RegularExpressions.Regex.IsMatch(token,"^[a-f0-9]{64}$"))throw new UnauthorizedAccessException("Invalid managed relay capability");
        var path=Path.Combine(Root,token+".json");CanonicalStore.GuardPath(path);
        File.Move(path,path+".used"); // Atomic one-use claim. Unknown/replayed tokens are not fallback traffic.
        try{using var d=JsonDocument.Parse(File.ReadAllBytes(path+".used"));var r=d.RootElement;
            if(r.GetProperty("body_hash").GetString()!=CanonicalStore.Hash(body)||r.GetProperty("expires").GetDateTimeOffset()<DateTimeOffset.UtcNow)
                throw new UnauthorizedAccessException("Managed relay capability expired or body changed");
            return true;
        }finally{File.Delete(path+".used");}
    }
}
