using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalBrain.ClientHost.AgentV2;

internal static class PublicRescueFixture
{
    private const string FixtureTree = "df198e70d111900066e7a33aaa9927b15048cd20";

    internal static async Task PrepareAsync(string root)
    {
        root = Path.GetFullPath(root);
        ReviewQualification.GuardRoot(root);
        var repository = Path.Combine(root, "repo");
        var control = Path.Combine(root, "rescue-control");
        ReviewQualification.GuardRoot(repository);
        if (Directory.Exists(control)) throw new IOException("Rescue trial needs a fresh control directory");

        var head = await RequireCleanFixtureAsync(repository);

        var request = AgentTaskRequest.Parse("""
            {
              "requirement": "Fix Arithmetic.Add so that it adds the two signed integers.",
              "acceptance_criteria": ["All three cases in ArithmeticFixture pass", "Only Arithmetic.cs changes"],
              "risk": "LOW",
              "approved_high_risk": false,
              "allowed_files": ["Arithmetic.cs"],
              "required_tests": [{"kind": "dotnet_test", "target": "ArithmeticFixture.csproj"}]
            }
            """);
        using var store = new CanonicalStore(control);
        var task = "public_rescue_" + Guid.NewGuid().ToString("N");
        using var journal = new HostTaskJournal(store, repository, task, request, head);
        store.ChangePolicies([new(HostTaskJournal.RepositoryId(repository), repository, "manual", request.AllowedFiles,
            ["task_evidence_only", "file:Arithmetic.cs"])], "public_synthetic_rescue_fixture");
        journal.Record("public_rescue_trial_input", new {
            provenance = "Intentional arithmetic fault in a synthetic fixture",
            qwen_failure_observed = false,
            production_qualification = false
        }, "host_verified");
        var starting = await AttemptStartingState.SaveAsync(journal, 1);
        var rescue = new HostRescue(journal);
        var ticketHash = await rescue.ReserveAsync(starting);
        var ticket = rescue.Load(ticketHash);
        var prepared = rescue.PreparedExport(ticket);
        using var packet = JsonDocument.Parse(prepared.Payload);
        var sources = packet.RootElement.GetProperty("starting_source");
        if (prepared.Manifest.PolicyMode != "manual" || sources.GetArrayLength() != 1 ||
            sources[0].GetProperty("Path").GetString() != "Arithmetic.cs" ||
            sources[0].GetProperty("Content").GetString() != File.ReadAllText(Path.Combine(repository, "Arithmetic.cs")))
            throw new InvalidDataException("Public trial packet escaped its exact source scope");
        try
        {
            using var lease = new HostEgress(store).AuthorizeDispatch(prepared, "codex_protocol");
            throw new InvalidOperationException("Host authorized the public trial without a human receipt");
        }
        catch (UnauthorizedAccessException)
        {
            // A real run must still acquire the separate human approval.
        }
        Console.WriteLine(JsonSerializer.Serialize(new {
            task_id = task,
            base_commit = head,
            ticket_hash = ticketHash,
            manifest_hash = prepared.ManifestHash,
            payload_hash = prepared.Manifest.ExportHash,
            file_scope = request.AllowedFiles,
            unapproved_dispatch_rejected = true,
            external_requests = 0,
            production_qualification = false
        }));
    }

    internal static async Task ValidateBaselineAsync(string root, string ticketHash, string publisherHash)
    {
        root = Path.GetFullPath(root);
        ReviewQualification.GuardRoot(root);
        var repository = Path.Combine(root, "repo");
        var control = Path.Combine(root, "rescue-control");
        ReviewQualification.GuardRoot(control);
        var head = await RequireCleanFixtureAsync(repository);
        if (!File.Exists(Path.Combine(control, "canonical.db")))
            throw new InvalidDataException("Prepare the public Rescue trial first");

        var path = Path.Combine(root, "publisher-policy.json");
        CanonicalStore.GuardPath(path);
        if (new FileInfo(path).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Trial publisher policy exceeds input budget");
        var bytes = await File.ReadAllBytesAsync(path);
        if (!CanonicalStore.Hash(bytes).Equals(publisherHash, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Trial publisher policy hash changed");
        var policy = JsonSerializer.Deserialize<HostValidationPolicy.Policy>(bytes,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidDataException("Trial publisher policy missing");
        var publisherRoot = Path.Combine(control, "validation-inputs", "public-publisher");
        if (policy.SdkVersion != "10.0.303" ||
            policy.Sdk.Root != Path.Combine(publisherRoot, "sdk") ||
            policy.Packages.Root != Path.Combine(publisherRoot, "packages") ||
            policy.Feed.Root != Path.Combine(publisherRoot, "feed"))
            throw new UnauthorizedAccessException("Validation publisher must belong to this public trial");

        using var store = new CanonicalStore(control);
        var ticket = JsonSerializer.Deserialize<HostRescue.Ticket>(store.ReadEvidence(ticketHash)) ??
            throw new InvalidDataException("Public Rescue ticket missing");
        if (!ticket.TaskId.StartsWith("public_rescue_", StringComparison.Ordinal) || ticket.BaseCommit != head ||
            !ticket.MainRoot.Equals(repository, StringComparison.OrdinalIgnoreCase) ||
            !store.EventIds(ticket.TaskId).Select(id => store.ReadTaskEvent(ticket.TaskId, id)).Any(e =>
                e.Input.Producer == "windows_host" && e.Input.EventType == "public_rescue_trial_input"))
            throw new UnauthorizedAccessException("Only the prepared public trial is allowed");

        var request = AgentTaskRequest.Parse(store.TaskRequest(ticket.TaskId));
        var test = request.RequiredTests.Single();
        if (test.Kind != "dotnet_test" || test.Target != "ArithmeticFixture.csproj")
            throw new UnauthorizedAccessException("Unexpected public baseline test");
        if (store.LatestTask(HostTaskJournal.WorkspaceId(repository))?.TaskId != ticket.TaskId)
            throw new UnauthorizedAccessException("The public Rescue task is no longer current");
        var canonicalPolicyHash = CanonicalStore.Hash(JsonSerializer.SerializeToUtf8Bytes(policy));
        var registered = store.CurrentValidationPublisher(HostTaskJournal.RepositoryId(repository));
        if (registered is null)
            await HostValidationRegistration.RegisterAsync(store, repository, policy);
        else if (registered.PolicyHash != canonicalPolicyHash)
            throw new UnauthorizedAccessException("A different publisher policy is already registered");
        using var journal = new HostTaskJournal(store, repository, ticket.TaskId, request, head);
        _ = new HostRescue(journal).Load(ticketHash);
        journal.EnablePersonalValidation();
        var result = await HostValidationPolicy.RunAsync(journal, repository, "", test);
        var validation = store.EventIds(ticket.TaskId).Select(id => store.ReadTaskEvent(ticket.TaskId, id))
            .Last(e => e.Input.Producer == "windows_host" && e.Input.EventType == "validation_isolation_result");
        using var recorded = JsonDocument.Parse(store.ReadEvidence(validation.Input.EvidenceRefs.Single()));
        var metadata = recorded.RootElement;
        var run = metadata.GetProperty("run").GetString()!;
        if (!CanonicalStore.Within(Path.Combine(control, "validation-runs"), run))
            throw new UnauthorizedAccessException("Baseline evidence escaped its control root");
        var output = Path.Combine(run, "validate", "captured.stdout.bin");
        var assertsFailed = File.Exists(output) && Regex.IsMatch(await File.ReadAllTextAsync(output),
            @"Failed:\s*3\s*,\s*Passed:\s*0\s*,\s*Skipped:\s*0\s*,\s*Total:\s*3",
            RegexOptions.CultureInvariant);
        if (await RequireCleanFixtureAsync(repository) != head)
            throw new UnauthorizedAccessException("Baseline validation changed the fixture");
        var observed = assertsFailed && !result.Passed && result.ExitCode == 1;
        Console.WriteLine(JsonSerializer.Serialize(new {
            task_id = ticket.TaskId,
            publisher_policy_hash = canonicalPolicyHash,
            validation_event = validation.EventId,
            run,
            reason = metadata.GetProperty("reason").GetString(),
            expected_fixture_failures_observed = observed,
            result,
            external_requests = 0
        }));
        if (!observed) Environment.ExitCode = 2;
    }

    private static async Task<string> RequireCleanFixtureAsync(string repository)
    {
        ReviewQualification.GuardRoot(repository);
        string GitText(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimEnd();
        var head = GitText(await AttemptStartingState.GitAsync(repository, ["rev-parse", "HEAD"]));
        var tree = GitText(await AttemptStartingState.GitAsync(repository, ["rev-parse", "HEAD^{tree}"]));
        var status = GitText(await AttemptStartingState.GitAsync(repository, ["status", "--porcelain"]));
        if (tree != FixtureTree || status.Length != 0)
            throw new UnauthorizedAccessException("Only the clean public arithmetic fixture is accepted");
        return head;
    }
}
