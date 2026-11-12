using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalBrain.ClientHost;
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

    internal static async Task HandoffAsync(string root, string ticketHash, string manifestHash,
        string operation, string? evidenceHash = null, string? codexExecutable = null, string directProvider = "sol")
    {
        root = Path.GetFullPath(root);
        ReviewQualification.GuardRoot(root);
        var repository = Path.Combine(root, "repo");
        var control = Path.Combine(root, "rescue-control");
        ReviewQualification.GuardRoot(control);
        var head = await RequireCleanFixtureAsync(repository);
        if (!File.Exists(Path.Combine(control, "canonical.db")))
            throw new InvalidDataException("Prepare the public Rescue trial first");

        using var store = new CanonicalStore(control);
        var ticket = JsonSerializer.Deserialize<HostRescue.Ticket>(store.ReadEvidence(ticketHash)) ??
            throw new InvalidDataException("Public Rescue ticket missing");
        var events = store.EventIds(ticket.TaskId).Select(id => store.ReadTaskEvent(ticket.TaskId, id)).ToArray();
        if (!ticket.TaskId.StartsWith("public_rescue_", StringComparison.Ordinal) || ticket.BaseCommit != head ||
            !ticket.MainRoot.Equals(repository, StringComparison.OrdinalIgnoreCase) ||
            store.LatestTask(HostTaskJournal.WorkspaceId(repository))?.TaskId != ticket.TaskId ||
            !events.Any(e => e.Input.Producer == "windows_host" && e.Input.EventType == "public_rescue_trial_input"))
            throw new UnauthorizedAccessException("Only the prepared public trial is allowed");

        var request = AgentTaskRequest.Parse(store.TaskRequest(ticket.TaskId));
        using var journal = new HostTaskJournal(store, repository, ticket.TaskId, request, head);
        var rescue = new HostRescue(journal);
        var export = rescue.PreparedExport(rescue.Load(ticketHash));
        var direct = operation == "--public-rescue-solve" && manifestHash.StartsWith("direct:", StringComparison.Ordinal);
        if (directProvider != "sol" && !direct)
            throw new ArgumentException("A separate provider is supported only for direct public repair");
        if (direct) manifestHash = manifestHash["direct:".Length..];
        using var packet = JsonDocument.Parse(export.Payload);
        var source = packet.RootElement.GetProperty("starting_source");
        if (export.ManifestHash != manifestHash || export.Manifest.PolicyMode != "manual" ||
            request.EffectiveRisk != "LOW" || request.RequiredTests.Length != 1 ||
            request.RequiredTests[0].Kind != "dotnet_test" || request.RequiredTests[0].Target != "ArithmeticFixture.csproj" ||
            !request.AllowedFiles.SequenceEqual(["Arithmetic.cs"]) || source.GetArrayLength() != 1 ||
            source[0].GetProperty("Path").GetString() != "Arithmetic.cs" ||
            source[0].GetProperty("Content").GetString() != await File.ReadAllTextAsync(Path.Combine(repository, "Arithmetic.cs")))
            throw new UnauthorizedAccessException("Public handoff must use the exact prepared fixture packet");

        if (direct)
        {
            if (codexExecutable is null || Console.IsInputRedirected || Console.IsOutputRedirected)
                throw new InvalidOperationException("Public direct edit requires the installed Codex CLI and interactive approval");
            RequirePublicValidationConfig(root);
            journal.EnablePersonalValidation();
            HostValidationPolicy.RequireRegisteredPublisher(journal);
            var edit = rescue.PrepareDirectEdit(ticketHash, provider: directProvider);
            Console.WriteLine($"Public fixture only. {ReviewerCli.RescueTarget(edit).Model} edits the scoped Arithmetic.cs copy and the Host runs the required tests.");
            Console.WriteLine(edit.Payload);
            new HostEgress(store).ApproveInteractive(edit.Manifest, edit.ManifestHash, journal);
            var result = await rescue.RunExternalAsync(ticketHash, developmentCodex:codexExecutable,
                editManifestHash:edit.ManifestHash);
            Console.WriteLine(JsonSerializer.Serialize(result, ClientConfig.JsonOptions));
            if (result.Status != "VALIDATED_IN_RESCUE") Environment.ExitCode = 2;
            return;
        }

        var packetPath = Path.Combine(root, "approved-sol-packet.json");
        CanonicalStore.GuardPath(packetPath);
        if (operation == "--public-rescue-export" || codexExecutable is not null)
        {
            if (File.Exists(packetPath))
                throw new IOException("The approved packet already exists");
            if (codexExecutable is not null && (!Path.IsPathFullyQualified(codexExecutable) ||
                !Path.GetFileName(codexExecutable).Equals("codex.exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(codexExecutable) || File.Exists(Path.Combine(root, "sol-response.json"))))
                throw new InvalidOperationException("An existing Codex executable and an unused response path are required");
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
                throw new InvalidOperationException("Public handoff requires an interactive human terminal");
            Console.WriteLine(codexExecutable is null ? "Public fixture handoff only." :
                $"Public fixture only. Send this packet to {ReviewerCli.Model("sol")} using the development Codex CLI.");
            Console.WriteLine(export.Payload);
            var egress = new HostEgress(store);
            egress.ApproveInteractive(export.Manifest, export.ManifestHash, journal);
            using var lease = egress.AuthorizeDispatch(export, "codex_protocol");
            await using (var file = new FileStream(packetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await file.WriteAsync(Encoding.UTF8.GetBytes(export.Payload));
            journal.Record("public_rescue_packet_handed_off", new { manifestHash, export.Manifest.ExportHash },
                "host_verified");
            if (codexExecutable is not null)
                await RunDevelopmentSolAsync(root, codexExecutable, export, journal);
            Console.WriteLine(packetPath);
            return;
        }

        var dispatch = events.SingleOrDefault(e => e.Input.Producer == "windows_host" &&
            e.Input.EventType == "external_dispatch_authorized" &&
            e.Input.EvidenceRefs.SequenceEqual([manifestHash, export.Manifest.ExportHash]) &&
            e.Input.Metadata.GetProperty("provider").GetString() == "codex_protocol");
        if (dispatch is null || !File.Exists(packetPath) ||
            await RescueInput.ReadAsync(File.OpenRead(packetPath), 64_000) != export.Payload)
            throw new UnauthorizedAccessException("Import requires the exact approved public handoff");
        if (evidenceHash is not null)
        {
            await ValidateOrLearnAsync(root, ticketHash, manifestHash, dispatch.EventId, evidenceHash,
                operation, store, journal, rescue);
            return;
        }
        var replyPath = Path.Combine(root, "sol-response.json");
        CanonicalStore.GuardPath(replyPath);
        var reply = await RescueInput.ReadAsync(File.OpenRead(replyPath), 48_000);
        var candidate = HostRescue.ParseExternalCandidate(reply);
        var candidateHash = await rescue.ImportAsync(ticketHash, candidate);
        var replyHash = store.PutEvidence(Encoding.UTF8.GetBytes(reply));
        journal.Record("public_rescue_manual_response_imported", new {
            manifestHash, dispatch_event_id = dispatch.EventId, replyHash, candidateHash,
            provider_identity_verified_by_host = false
        }, "host_verified");
        Console.WriteLine(JsonSerializer.Serialize(new { candidate_hash = candidateHash, reply_hash = replyHash }));
    }

    private static async Task RunDevelopmentSolAsync(string root, string executable,
        HostEgress.Prepared export, HostTaskJournal journal)
    {
        var reply = await ReviewerCli.RunDevelopmentRescueCliAsync(journal, export, executable);
        journal.Record("public_rescue_development_cli_finished", new {
            manifest_hash = export.ManifestHash, reply.ExitCode, reply.Status, reply.Reason
        }, "host_observed");
        if (reply.Status != "OK")
            throw new InvalidOperationException($"Codex CLI exit {reply.ExitCode}: {reply.Reason}");
        _ = HostRescue.ParseExternalCandidate(reply.Answer);
        var bytes = Encoding.UTF8.GetBytes(reply.Answer);
        var responseHash = journal.Canonical.PutEvidence(bytes);
        journal.Record("public_rescue_development_cli_response", new {
            manifest_hash = export.ManifestHash, response_hash = responseHash
        }, "untrusted_candidate");
        var path = Path.Combine(root, "sol-response.json");
        CanonicalStore.GuardPath(path);
        await using var response = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await response.WriteAsync(bytes);
    }

    private static async Task ValidateOrLearnAsync(string root, string ticketHash, string manifestHash,
        string dispatchEventId, string evidenceHash, string operation, CanonicalStore store,
        HostTaskJournal journal, HostRescue rescue)
    {
        var events = store.EventIds(journal.TaskId).Select(id => store.ReadTaskEvent(journal.TaskId, id)).ToArray();
        var validate = operation == "--public-rescue-validate";
        var candidateHash = evidenceHash;
        if (!validate)
        {
            var validation = JsonSerializer.Deserialize<HostRescue.Validation>(store.ReadEvidence(evidenceHash)) ??
                throw new InvalidDataException("Public Rescue validation missing");
            if (validation.TicketHash != ticketHash)
                throw new UnauthorizedAccessException("Validation is for a different public Rescue ticket");
            candidateHash = validation.CandidateHash;
        }
        var imported = events.Where(e => e.Input.Producer == "windows_host" &&
            e.Input.EventType == "public_rescue_manual_response_imported").Any(e => {
                using var receipt = JsonDocument.Parse(store.ReadEvidence(e.Input.EvidenceRefs.Single()));
                return receipt.RootElement.GetProperty("manifestHash").GetString() == manifestHash &&
                    receipt.RootElement.GetProperty("dispatch_event_id").GetString() == dispatchEventId &&
                    receipt.RootElement.GetProperty("candidateHash").GetString() == candidateHash;
            });
        if (!imported)
            throw new UnauthorizedAccessException("No matching imported public response");

        RequirePublicValidationConfig(root);
        journal.EnablePersonalValidation();
        if (validate)
        {
            var validationHash = await rescue.ValidateAsync(ticketHash, evidenceHash);
            Console.WriteLine(JsonSerializer.Serialize(new { validation_hash = validationHash }));
            return;
        }
        var learning = new DifferentialLearning(journal);
        var learningHash = await learning.ExtractAsync(ticketHash, evidenceHash);
        _ = learning.RequireCandidate(learningHash);
        Console.WriteLine(JsonSerializer.Serialize(new {
            learning_candidate_hash = learningHash,
            adoption_status = "QUARANTINED_NOT_ADOPTED"
        }));
    }

    private static void RequirePublicValidationConfig(string root)
    {
        var configPath = Path.Combine(root, "clientsettings.json");
        CanonicalStore.GuardPath(configPath);
        if (!string.Equals(configPath, Environment.GetEnvironmentVariable("LOCALBRAIN_CLIENT_CONFIG"),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The public trial requires its own explicit Host configuration");
        var config = ClientConfig.Load();
        var agentRoot = Path.Combine(root, "pi-agent");
        CanonicalStore.GuardPath(agentRoot);
        CanonicalStore.GuardPath(config.AuditLog);
        CanonicalStore.GuardPath(config.ProjectsFile);
        var wslAgentRoot = "/mnt/" + char.ToLowerInvariant(agentRoot[0]) + agentRoot[2..].Replace('\\', '/');
        if (agentRoot[1] != ':' || !config.PersonalValidation || !CanonicalStore.Within(root, config.AuditLog) ||
            !CanonicalStore.Within(root, config.ProjectsFile) || config.PiRuntime?.AgentDirectory != wslAgentRoot)
            throw new UnauthorizedAccessException("Public trial configuration escaped the trial root");
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
