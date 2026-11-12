namespace LocalBrain.ClientHost.AgentV2;

internal static class AutomaticCompletion
{
    internal static bool ChangesVerificationInputs(AgentTaskRequest request, IEnumerable<string> changedFiles)
    {
        var targets=request.RequiredTests.Where(test=>test.Target is not null)
            .Select(test=>test.Target!.Replace('\\','/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var changed in changedFiles)
        {
            var path=changed.Replace('\\','/');
            var name=Path.GetFileName(path);
            if(targets.Contains(path) || path.Split('/').Any(part=>
                part.Equals("tests",StringComparison.OrdinalIgnoreCase) ||
                part.Equals("specs",StringComparison.OrdinalIgnoreCase) ||
                part.Equals("test",StringComparison.OrdinalIgnoreCase)) ||
                name.Contains("Tests.",StringComparison.OrdinalIgnoreCase) ||
                name.Contains(".Test.",StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Test",StringComparison.OrdinalIgnoreCase) ||
                name.Contains(".spec.",StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".csproj",StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".props",StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".targets",StringComparison.OrdinalIgnoreCase) ||
                name.Equals("global.json",StringComparison.OrdinalIgnoreCase) ||
                name.Equals("nuget.config",StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static bool HasPassingTests(AgentTaskRequest request,IReadOnlyList<ToolRouter.TestResult> results) =>
        results.Count==request.RequiredTests.Length && results.Select(test=>test.Kind)
            .SequenceEqual(request.RequiredTests.Select(test=>test.Kind),StringComparer.Ordinal) &&
        results.Any(test=>test.Kind=="dotnet_test" && test.ExecutedTests>0) &&
        results.All(test=>test.Passed && test.ExitCode==0);

    internal static void Record(HostTaskJournal journal,GitEvidence.Snapshot snapshot,
        IReadOnlyList<ToolRouter.TestResult> tests) =>
        journal.Record("host_automatic_completion",new {
            snapshot.Head,snapshot.ChangedFiles,diff_sha256=AgentTaskRunner.HashTextForHost(snapshot.ReviewDiff),
            tests=tests.Select(test=>new{test.Kind,test.ExitCode,test.ExecutedTests,test.StdoutSha256}),
            independent_critic="PASS"
        },"host_verified");
}
