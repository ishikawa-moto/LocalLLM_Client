using System.Text;
using System.Text.Json;
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

        string GitText(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimEnd();
        var head = GitText(await AttemptStartingState.GitAsync(repository, ["rev-parse", "HEAD"]));
        var tree = GitText(await AttemptStartingState.GitAsync(repository, ["rev-parse", "HEAD^{tree}"]));
        var status = GitText(await AttemptStartingState.GitAsync(repository, ["status", "--porcelain"]));
        if (tree != FixtureTree || status.Length != 0)
            throw new UnauthorizedAccessException("Only the clean public arithmetic fixture is accepted");

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
}
