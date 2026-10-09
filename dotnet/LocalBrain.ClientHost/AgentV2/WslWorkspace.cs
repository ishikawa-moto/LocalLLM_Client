using System.Diagnostics;

namespace LocalBrain.ClientHost.AgentV2;

internal static class WslWorkspace
{
    public static async Task<string> MapFileAsync(string windowsPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(windowsPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("WSL input file is missing", fullPath);
        var mapped = (await RunAsync(["-d", "Ubuntu", "--exec", "wslpath", "-a", "-u", fullPath],
            cancellationToken)).Trim();
        if (mapped.Length == 0 || !mapped.StartsWith('/') || mapped.Contains('\n'))
            throw new InvalidOperationException("WSL returned an invalid file path");
        return mapped;
    }

    public static async Task<string> ResolveAsync(ProjectAllowlist allowlist, string selectedPath,
        CancellationToken cancellationToken = default)
    {
        // Resolve through the existing registered Git-root allowlist first. WSL never
        // receives a path supplied directly by a model or an unregistered workspace.
        var root = allowlist.Resolve(selectedPath);
        return await ResolveRootAsync(root,cancellationToken);
    }

    internal static Task<string> ResolveRegisteredMainAsync(CanonicalStore store,string workspaceId,CancellationToken token=default)
    {
        var registered=store.GetWorkspace(workspaceId);
        if(registered.Role!="main" || !string.Equals(registered.WorkspaceRoot,registered.CanonicalRepoRoot,StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Expected Host-registered main workspace");
        return ResolveRootAsync(registered.WorkspaceRoot,token);
    }

    private static async Task<string> ResolveRootAsync(string root,CancellationToken cancellationToken)
    {
        for (DirectoryInfo? current = new(root); current is not null; current = current.Parent)
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("Linked workspace paths are not allowed");

        var mapped = (await RunAsync(["-d", "Ubuntu", "--exec", "wslpath", "-a", "-u", root],
            cancellationToken)).Trim();
        if (mapped.Length == 0 || !mapped.StartsWith('/') || mapped == "/" || mapped.Contains('\n'))
            throw new InvalidOperationException("WSL returned an invalid workspace path");
        var canonical = (await RunAsync(["-d", "Ubuntu", "--exec", "realpath", "-e", mapped],
            cancellationToken)).Trim();
        if (!string.Equals(mapped.TrimEnd('/'), canonical.TrimEnd('/'), StringComparison.Ordinal))
            throw new UnauthorizedAccessException("WSL workspace resolves through a symlink");
        var gitRoot = (await RunAsync(["-d", "Ubuntu", "--exec", "git", "-c",
            $"safe.directory={canonical}", "-C", canonical, "rev-parse", "--show-toplevel"],
            cancellationToken)).Trim();
        if (!string.Equals(canonical.TrimEnd('/'), gitRoot.TrimEnd('/'), StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Pi cwd would not be the registered Git root");
        return canonical;
    }

    internal static async Task<string> ResolveRegisteredRescueAsync(CanonicalStore store,string workspaceId,CancellationToken token=default) {
        var registered=store.GetWorkspace(workspaceId);
        if(registered.Role!="codex_rescue" || CanonicalStore.Within(registered.CanonicalRepoRoot,registered.WorkspaceRoot))throw new UnauthorizedAccessException("Expected independent Host-registered rescue");
        CanonicalStore.GuardPath(registered.WorkspaceRoot);
        var mapped=(await RunAsync(["-d","Ubuntu","--exec","wslpath","-a","-u",registered.WorkspaceRoot],token)).Trim();
        if(!mapped.StartsWith('/') || mapped=="/" || mapped.Contains('\n'))throw new InvalidDataException("Invalid rescue WSL path");
        var canonical=(await RunAsync(["-d","Ubuntu","--exec","realpath","-e",mapped],token)).Trim();
        var gitEnvironment=await RescueGitEnvironmentAsync(store,workspaceId,token);
        var gitRoot=(await RunAsync(["-d","Ubuntu","--exec","/usr/bin/env",..gitEnvironment,"git","-c","safe.directory="+canonical,"-C",canonical,"rev-parse","--show-toplevel"],token)).Trim();
        if(mapped!=canonical || canonical!=gitRoot)throw new UnauthorizedAccessException("Rescue WSL identity drift");
        return canonical;
    }
    internal static async Task<string[]> RescueGitEnvironmentAsync(CanonicalStore store,string workspaceId,CancellationToken token=default) {
        var w=store.GetWorkspace(workspaceId);if(w.Role!="codex_rescue")throw new UnauthorizedAccessException("Expected rescue workspace");
        var gitDir=System.Text.Encoding.UTF8.GetString(await AttemptStartingState.GitAsync(w.WorkspaceRoot,["rev-parse","--absolute-git-dir"],token)).Trim();
        var common=System.Text.Encoding.UTF8.GetString(await AttemptStartingState.GitAsync(w.CanonicalRepoRoot,["rev-parse","--path-format=absolute","--git-common-dir"],token)).Trim();
        CanonicalStore.GuardPath(gitDir);CanonicalStore.GuardPath(common);CanonicalStore.GuardPath(w.WorkspaceRoot);
        if(!CanonicalStore.Within(Path.Combine(common,"worktrees"),gitDir))throw new UnauthorizedAccessException("Rescue Git metadata escaped registered repository");
        var mappedDir=(await RunAsync(["-d","Ubuntu","--exec","wslpath","-a","-u",gitDir],token)).Trim();
        var mappedRoot=(await RunAsync(["-d","Ubuntu","--exec","wslpath","-a","-u",w.WorkspaceRoot],token)).Trim();
        if(!mappedDir.StartsWith('/') || !mappedRoot.StartsWith('/') || mappedDir.Contains('\n') || mappedRoot.Contains('\n'))throw new UnauthorizedAccessException("Invalid Host-derived Git environment");
        return ["GIT_DIR="+mappedDir,"GIT_WORK_TREE="+mappedRoot];
    }
    private static async Task<string> RunAsync(string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo("wsl.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("WSL failed to start");
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var value = await stdout;
            _ = await stderr; // Do not log command output: paths may be private.
            if (process.ExitCode != 0) throw new InvalidOperationException("WSL path validation failed");
            return value;
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
