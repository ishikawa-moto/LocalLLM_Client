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
    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"safe.directory={cwd}");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git failed to start");
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(error); return output;
    }
}
