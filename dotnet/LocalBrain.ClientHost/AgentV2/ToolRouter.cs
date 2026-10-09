using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace LocalBrain.ClientHost.AgentV2;

/// <summary>Runs only named, user-specified validation operations on the intended OS.</summary>
internal static class ToolRouter
{
    private const int MaxOutputBytes = 2_000_000;
    internal sealed record TestResult(string Kind, string Route, bool Passed, int ExitCode,
        long StdoutBytes, long StderrBytes, string StdoutSha256, string StderrSha256,
        long ElapsedMs, int? ExecutedTests, string[] FailureCodes);

    public static Task<TestResult> RunAsync(string windowsRoot,string wslRoot,RequiredTest test,CancellationToken token=default) =>
        HostValidationIsolation.RunAsync(windowsRoot,wslRoot,test,token);

    internal static TestResult IsolationResult(RequiredTest test,string route,string reason,int exit,byte[] stdout,byte[] stderr,long elapsed,byte[] validationStdout) {
        // Restore output is evidence, but cannot establish that validation executed tests.
        var count=test.Kind=="dotnet_test"?PassedTestCount(Encoding.UTF8.GetString(validationStdout)):null;
        var passed=reason=="completed"&&exit==0&&(test.Kind!="dotnet_test"||count>0);
        var codes=passed?Array.Empty<string>():reason!="completed"?["isolation_"+reason]:DetectFailureCodes(test.Kind,Encoding.UTF8.GetString(stdout),Encoding.UTF8.GetString(stderr),exit,count);
        return new(test.Kind,route,passed,exit,stdout.LongLength,stderr.LongLength,
            Convert.ToHexString(SHA256.HashData(stdout)).ToLowerInvariant(),Convert.ToHexString(SHA256.HashData(stderr)).ToLowerInvariant(),elapsed,count,codes);
    }
    private static int? PassedTestCount(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var matches = Regex.Matches(output,
            @"Failed:\s*(\d+)\s*,\s*Passed:\s*(\d+)\s*,\s*Skipped:\s*(\d+)\s*,\s*Total:\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var total = 0;
        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Groups[1].Value, out var failed) ||
                !int.TryParse(match.Groups[2].Value, out var passed) ||
                !int.TryParse(match.Groups[4].Value, out var count) ||
                failed != 0 || passed <= 0 || count < passed) return null;
            total += count;
        }
        return total > 0 ? total : null;
    }

    private static string[] DetectFailureCodes(string kind, string stdout, string stderr,
        int exitCode, int? executedTests)
    {
        var output = stdout + "\n" + stderr;
        var codes = new List<string>();
        if (kind is "dotnet_build" or "dotnet_test")
        {
            foreach (Match match in Regex.Matches(output, @"\b(?:CS|NU|NETSDK|MSB)\d{3,5}\b",
                RegexOptions.CultureInvariant))
                if (!codes.Contains(match.Value, StringComparer.Ordinal) && codes.Count < 3)
                    codes.Add(match.Value);
            if (kind == "dotnet_test" && executedTests is null)
                codes.Add(exitCode == 0 ? "no_tests_detected" : "test_failed_or_no_summary");
        }
        else if (kind == "npm_test")
        {
            foreach (var (marker, code) in new[] {
                ("AssertionError", "assertion_failed"),
                ("SyntaxError", "syntax_error"),
                ("TypeError", "type_error"),
                ("ReferenceError", "reference_error"),
                ("MODULE_NOT_FOUND", "module_not_found"),
                ("ENOENT", "missing_file") })
                if (output.Contains(marker, StringComparison.Ordinal) && codes.Count < 3)
                    codes.Add(code);
        }
        else if (kind == "git_diff_check") codes.Add("git_diff_error");
        if (codes.Count == 0) codes.Add("process_exit_nonzero");
        return codes.ToArray();
    }

}
