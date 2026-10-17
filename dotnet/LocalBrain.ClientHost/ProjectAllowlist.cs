using System.Diagnostics;
using System.Text.Json;

namespace LocalBrain.ClientHost;

internal sealed class ProjectAllowlist(ClientConfig config)
{
    public IReadOnlyList<string> Read() => File.Exists(config.ProjectsFile)
        ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(config.ProjectsFile), ClientConfig.JsonOptions) ?? [] : [];
    public string Resolve(string value)
    {
        var path = Path.GetFullPath(value);
        var root = Path.GetFullPath(Git(path, "rev-parse", "--show-toplevel").Trim());
        if (!Read().Any(item => string.Equals(Path.GetFullPath(item), root, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("Git workspace is not in the LocalBrain project allowlist");
        return root;
    }
    public string Register(string value)
    {
        var path = Path.GetFullPath(value); var root = Path.GetFullPath(Git(path, "rev-parse", "--show-toplevel").Trim());
        var projects = Read().Select(Path.GetFullPath).ToList();
        if (!projects.Contains(root, StringComparer.OrdinalIgnoreCase)) projects.Add(root);
        Directory.CreateDirectory(Path.GetDirectoryName(config.ProjectsFile)!);
        File.WriteAllText(config.ProjectsFile, JsonSerializer.Serialize(projects.Order(StringComparer.OrdinalIgnoreCase), ClientConfig.JsonOptions));
        return root;
    }
    internal static string CurrentHead(string root) => Git(root, "--no-optional-locks", "rev-parse", "HEAD").Trim();
    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding=System.Text.Encoding.UTF8,StandardErrorEncoding=System.Text.Encoding.UTF8 };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"safe.directory={cwd}");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["GIT_TERMINAL_PROMPT"]="0";start.Environment["GIT_OPTIONAL_LOCKS"]="0";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git failed to start");
        process.StandardInput.Close(); // MCP owns stdin; Git must never inherit its open protocol stream.
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        var completed=Task.WhenAll(output,error,process.WaitForExitAsync());
        if(!completed.Wait(TimeSpan.FromSeconds(10))){
            if(!process.HasExited){process.Kill(entireProcessTree:true);process.WaitForExit(3000);}
            throw new TimeoutException("Git workspace read exceeded10seconds; preserve the request for recovery");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Result); return output.Result;
    }
}
