namespace LocalBrain.ClientHost.AgentV2;

// The operator selects the checkout and control root before any MCP request.
internal sealed class RunnerMcpScope(string workspace)
{
    private string Workspace { get; } = workspace;

    internal static async Task StartAsync(string directory)
    {
        ReviewQualification.GuardRoot(directory);
        var root = Path.GetFullPath(directory);
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Runner trial requires a local directory");
        var repo = Path.Combine(root, "repo");
        var control = Path.Combine(root, "control");
        var settings = Path.Combine(root, "clientsettings.json");
        var projects = Path.Combine(root, "projects.json");
        var audit = Path.Combine(root, "audit.jsonl");
        var piAgent = Path.Combine(root, "pi-agent");
        foreach (var path in new[] { repo, control, settings, projects, audit, Path.Combine(repo, ".git"),
                     piAgent, Path.Combine(piAgent, "sessions") })
            CanonicalStore.GuardPath(path);
        if (!Directory.Exists(Path.Combine(repo, ".git")))
            throw new UnauthorizedAccessException("Runner requires a dedicated standalone Git checkout");
        var config = ClientConfig.Load(settings);
        if (!SamePath(config.ProjectsFile, projects) || !SamePath(config.AuditLog, audit) ||
            config.PiRuntime is null || config.V3Development?.Enabled == true || !config.PersonalValidation)
            throw new UnauthorizedAccessException("Runner requires trial-local files, private Pi and trusted Git validation");
        if (!Directory.Exists(piAgent) || config.PiRuntime.AgentDirectory !=
            await WslWorkspace.MapPathAsync(piAgent, CancellationToken.None))
            throw new UnauthorizedAccessException("Runner Pi agent directory must be the trial's pi-agent directory");
        var allowlist = new ProjectAllowlist(config);
        var entries = allowlist.Read();
        if (entries.Count != 1 || !SamePath(entries[0], repo) || !SamePath(allowlist.Resolve(repo), repo))
            throw new UnauthorizedAccessException("Runner allowlist must contain only the dedicated checkout");
        McpStdio.Configure();
        await new AgentV2McpServer(allowlist, config, control, runner: new(repo)).RunAsync();
    }

    internal void RequireWorkspace(string selected)
    {
        if (!Path.IsPathFullyQualified(selected) || !SamePath(selected, Workspace))
            throw new UnauthorizedAccessException("Workspace is outside this Runner");
        CanonicalStore.GuardPath(Workspace);
        CanonicalStore.GuardPath(Path.Combine(Workspace, ".git"));
    }

    internal void RequireTask(AgentTaskRequest request)
    {
        var scope = request.CommandExecution;
        if (request.EffectiveRisk != "LOW" || request.ApprovedHighRisk || scope is null ||
            scope.MaxCommands != 0 || scope.OutputDirectories.Length != 0 ||
            !scope.WorkingDirectories.SequenceEqual(["."]) ||
            scope.MaxTaskMinutes > 10 || scope.MaxActorTurns > 6 || scope.MaxOutputBytes > 16_384 ||
            request.AllowedFiles.Length > 8 || request.AllowedFiles.Any(f => f.Contains('*')) ||
            request.RequiredTests.Length != 1 || request.RequiredTests[0].Kind != "git_diff_check")
            throw new UnauthorizedAccessException("Runner requires a bounded LOW file task, zero commands and git_diff_check");
        foreach (var file in request.AllowedFiles) HostEgress.NormalizePath(Workspace, file);
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
}
