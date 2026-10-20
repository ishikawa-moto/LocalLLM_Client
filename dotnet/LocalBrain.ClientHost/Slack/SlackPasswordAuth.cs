using LocalBrain.ClientHost.AgentV2;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalBrain.ClientHost.Slack;

internal sealed record SlackPasswordCredential(string Schema,string Actor,string Revision,int Iterations,string Salt,string Hash)
{
    internal const int WorkFactor=600000;
    internal static SlackPasswordCredential Create(string password,string actor="localbrain-password-admin")
    {
        if(password.Length is <4 or >256||password.Any(char.IsControl))throw new InvalidDataException("SLACK_PASSWORD_LENGTH");
        var salt=RandomNumberGenerator.GetBytes(32);var hash=Rfc2898DeriveBytes.Pbkdf2(password,salt,WorkFactor,HashAlgorithmName.SHA256,32);
        return new("localbrain.slack-password.v1",actor,Guid.NewGuid().ToString("N"),WorkFactor,Convert.ToBase64String(salt),Convert.ToBase64String(hash));
    }
    internal void Validate()
    {
        if(Actor is null||Revision is null||Salt is null||Hash is null||Schema!="localbrain.slack-password.v1"||!Regex.IsMatch(Actor,"^[A-Za-z0-9_-]{8,64}$")||!Guid.TryParseExact(Revision,"N",out _)||Iterations is <WorkFactor or >2000000||Salt.Length>128||Hash.Length>128||Convert.FromBase64String(Salt).Length!=32||Convert.FromBase64String(Hash).Length!=32)throw new InvalidDataException("SLACK_PASSWORD_CONFIG");
    }
    internal bool Matches(string password)
    {
        Validate();if(password.Length>256)return false;
        var actual=Rfc2898DeriveBytes.Pbkdf2(password,Convert.FromBase64String(Salt),Iterations,HashAlgorithmName.SHA256,32);
        try{return CryptographicOperations.FixedTimeEquals(actual,Convert.FromBase64String(Hash));}finally{CryptographicOperations.ZeroMemory(actual);}
    }
    internal static SlackPasswordCredential Read(string path)
    {
        CanonicalStore.GuardPath(path);using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
        if(f.Length>4096)throw new InvalidDataException("SLACK_PASSWORD_CONFIG_SIZE");
        var result=JsonSerializer.Deserialize<SlackPasswordCredential>(f,SlackOptions.Json)??throw new InvalidDataException("SLACK_PASSWORD_CONFIG");result.Validate();return result;
    }
}

internal sealed class SlackPasswordAuth(Func<SlackPasswordCredential> credential)
{
    internal SlackPasswordCredential Current(){var value=credential();value.Validate();return value;}
    internal (bool Accepted,SlackPasswordCredential Credential) Verify(string password)
    {
        var current=Current();return(current.Matches(password),current);
    }
}