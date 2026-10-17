namespace LocalBrain.ClientHost.AgentV2;

internal static class TaskWriteScope
{
    internal static bool Contains(IEnumerable<string> scopes,string relative)
    {
        var path=relative.Replace('\\','/');
        if(Path.IsPathRooted(path)||path.Split('/').Any(p=>p is ".." or ".git" or ".localbrain"))return false;
        return scopes.Any(s=>{var v=s.Replace('\\','/');return string.Equals(v,path,StringComparison.OrdinalIgnoreCase)||v=="**"||
            v.EndsWith("/**",StringComparison.Ordinal)&&path.StartsWith(v[..^2],StringComparison.OrdinalIgnoreCase);});
    }
}
