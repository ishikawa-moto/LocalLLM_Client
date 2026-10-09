using System.Diagnostics;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

internal static class AgentV2Commands
{
    public static async Task RunAsync(string[] args, ProjectAllowlist allowlist, ClientConfig config)
    {
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "health";
        if(command=="reference-learning-export"){
            if(args.Length!=3)throw new ArgumentException("reference-learning-export requires registered workspace and existing local candidate hash");
            var root=allowlist.Resolve(args[1]);using var store=new CanonicalStore(CanonicalStore.DefaultRoot);
            Console.WriteLine(JsonSerializer.Serialize(await ExternalReferenceLearning.ExportLocalAsync(store,root,args[2]),ClientConfig.JsonOptions));return;
        }
        if(command=="reference-learning"){
            if(args.Length!=3)throw new ArgumentException("reference-learning requires registered workspace and explicit observation selection JSON path");
            var root=allowlist.Resolve(args[1]);var path=Path.GetFullPath(args[2]);CanonicalStore.GuardPath(path);
            using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(file.Length is 0 or >250_000)throw new InvalidDataException("Learning selection exceeds Host budget");
            var bytes=new byte[(int)file.Length];file.ReadExactly(bytes);
            var selection=ExternalReferenceLearning.Parse(DecisionProtocol.Utf8.GetString(bytes));
            using var store=new CanonicalStore(CanonicalStore.DefaultRoot);
            Console.WriteLine(JsonSerializer.Serialize(await ExternalReferenceLearning.PrepareAsync(store,root,selection),ClientConfig.JsonOptions));return;
        }
        if(command=="reference-check"){
            if(args.Length!=3)throw new ArgumentException("reference-check requires registered workspace and explicit request JSON path");
            var root=allowlist.Resolve(args[1]);var path=Path.GetFullPath(args[2]);CanonicalStore.GuardPath(path);
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(input.Length is 0 or >250_000)throw new InvalidDataException("Reference request exceeds Host budget");
            var bytes=new byte[(int)input.Length];input.ReadExactly(bytes);
            var request=ExternalClaimVerification.Parse(DecisionProtocol.Utf8.GetString(bytes));
            using var store=new CanonicalStore(CanonicalStore.DefaultRoot);
            var task=store.ResolveExternalReferenceOwner(HostTaskJournal.WorkspaceId(root),request.ReferenceEventId);
            Console.WriteLine(JsonSerializer.Serialize(await ExternalClaimVerification.CheckAsync(store,task.TaskId,root,request),ClientConfig.JsonOptions));return;
        }
        if(command=="reference-import"){
            if(args.Length!=3)throw new ArgumentException("reference-import requires registered workspace and prepared reference bundle directory");
            var root=allowlist.Resolve(args[1]);var bundle=Path.GetFullPath(args[2]);CanonicalStore.GuardPath(bundle);
            byte[] ReadBounded(string name,int limit){var path=Path.Combine(bundle,name);CanonicalStore.GuardPath(path);
                using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
                if(file.Length is 0||file.Length>limit)throw new InvalidDataException("Reference bundle exceeds Host input budget");
                var bytes=new byte[(int)file.Length];file.ReadExactly(bytes);return bytes;}
            var original=ReadBounded("original.pdf",ExternalReferenceCorpus.MaxBytes);var text=ReadBounded("extracted.md",ExternalReferenceCorpus.MaxBytes);
            var manifest=ReadBounded("manifest.json",1_000_000);
            using var store=new CanonicalStore(CanonicalStore.DefaultRoot);
            var task=store.LatestTask(HostTaskJournal.WorkspaceId(root))??throw new InvalidDataException("No canonical reference task; import cannot replace an existing task owner");
            Console.WriteLine(JsonSerializer.Serialize(ExternalReferenceCorpus.Import(store,task.TaskId,original,text,manifest),ClientConfig.JsonOptions));return;
        }
        if(command=="validation-register"){
            if(args.Length!=3)throw new ArgumentException("validation-register requires registered workspace and Host publisher policy JSON path");
            var root=allowlist.Resolve(args[1]);var path=Path.GetFullPath(args[2]);CanonicalStore.GuardPath(path);
            if(new FileInfo(path).Length>2_000_000)throw new InvalidDataException("Publisher policy exceeds Host input budget");
            var policy=JsonSerializer.Deserialize<HostValidationPolicy.Policy>(await File.ReadAllTextAsync(path),ClientConfig.JsonOptions)??throw new InvalidDataException("Missing Host publisher policy");
            using var store=new CanonicalStore(CanonicalStore.DefaultRoot);
            Console.WriteLine(JsonSerializer.Serialize(await HostValidationRegistration.RegisterAsync(store,root,policy),ClientConfig.JsonOptions));return;
        }
        if (command == "learning-human") {
            if (args.Length != 3) throw new ArgumentException("learning-human requires registered workspace and decision JSON path");
            var root = allowlist.Resolve(args[1]);
            var path = Path.GetFullPath(args[2]); CanonicalStore.GuardPath(path);
            if (new FileInfo(path).Length > 16_000) throw new InvalidDataException("Decision input exceeds Host budget");
            var input = HumanLearning.Parse(await File.ReadAllTextAsync(path));
            using var store = new CanonicalStore(CanonicalStore.DefaultRoot);
            var task = store.LatestTask(HostTaskJournal.WorkspaceId(root)) ?? throw new InvalidDataException("No canonical task");
            var hash = new HumanLearning(store, task.TaskId).CaptureInteractive(input);
            Console.WriteLine(JsonSerializer.Serialize(new { local_manifest_hash = hash, adoption_status = "LOCAL_ONLY_NOT_ADOPTED" }));
            return;
        }
        if(command is "egress-pending" or "approve-export" or "rescue-export" or "rescue-import" or "rescue-validate" or "rescue-promote" or "learning-extract" or "adoption-prepare" or "adoption-prepare-human" or "adoption-confirm" or "adoption-dispatch") {
            var root=allowlist.Resolve(args.ElementAtOrDefault(1)??Environment.CurrentDirectory);
            using var store=new CanonicalStore(CanonicalStore.DefaultRoot);
            var canonicalWorkspaceId=HostTaskJournal.WorkspaceId(root);
            var task=command is "adoption-confirm" or "adoption-dispatch"
                ?store.ResolveAdoptionOwner(canonicalWorkspaceId,args.ElementAtOrDefault(2)??throw new ArgumentException("Exact intent/approval identity required"),command=="adoption-dispatch")
                :store.LatestTask(canonicalWorkspaceId)??throw new InvalidDataException("No canonical task");
            var request=AgentTaskRequest.Parse(store.TaskRequest(task.TaskId));var state=JsonDocument.Parse(task.StateJson);
            var head=state.RootElement.GetProperty("git_head").GetString()??throw new InvalidDataException("No canonical HEAD");
            using var journal=new HostTaskJournal(store,root,task.TaskId,request,head);
            if(config.PersonalValidation)journal.EnablePersonalValidation();
            if(command=="egress-pending") {
                var manifestEvents=store.EventIds(task.TaskId).Select(id=>store.ReadTaskEvent(task.TaskId,id)).Where(e=>e.Input.EventType=="external_export_prepared");
                Console.WriteLine(JsonSerializer.Serialize(manifestEvents.Select(e=>new{e.EventId,e.Input.Metadata}),ClientConfig.JsonOptions));return;
            }
            if(command=="adoption-prepare"){
                // Bound proposal receipt is supplied over bounded stdin; this does not adopt knowledge.
                var receipt=await RescueInput.ReadAsync(Console.OpenStandardInput());
                var requestId=args.ElementAtOrDefault(2)??throw new ArgumentException("Exact adoption request ID required");
                Console.WriteLine(JsonSerializer.Serialize(new{intent_hash=new DecisionAdoption(store,task.TaskId).PrepareProposal(receipt,requestId),adoption_status="PENDING_EXPLICIT_KNOWLEDGE_ADOPTION_CONFIRMATION"}));return;
            }
            var boundHash=args.ElementAtOrDefault(2)??throw new ArgumentException("Exact manifest/ticket/intent/approval identity required");
            if(command=="adoption-prepare-human"){
                var references=await RescueInput.ReadAsync(Console.OpenStandardInput());
                var requestId=args.ElementAtOrDefault(3)??throw new ArgumentException("Exact adoption request ID required");
                Console.WriteLine(JsonSerializer.Serialize(new{intent_hash=new DecisionAdoption(store,task.TaskId).PrepareHuman(boundHash,references,requestId),adoption_status="PENDING_EXPLICIT_KNOWLEDGE_ADOPTION_CONFIRMATION"}));return;
            }
            if(command=="adoption-confirm"){
                if(Console.IsInputRedirected || Console.IsOutputRedirected)throw new InvalidOperationException("Knowledge adoption confirmation requires an interactive human terminal");
                var adapter=new DecisionAdoption(store,task.TaskId);Console.WriteLine(adapter.DescribeIntent(boundHash));
                Console.WriteLine("Review this exact SecondBrain knowledge adoption intent. Type its complete SHA-256 to approve once: "+boundHash);
                if(Console.ReadLine()!=boundHash)throw new UnauthorizedAccessException("Exact knowledge adoption confirmation was not provided");
                Console.WriteLine(JsonSerializer.Serialize(new{approval_id=adapter.RecordConfirmed(boundHash,Environment.UserName,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),900)}));return;
            }
            if(command=="adoption-dispatch"){
                Console.WriteLine(await HostDecisionTransport.DispatchAsync(journal,boundHash,config));return;
            }
            if(command=="approve-export") {
                // Host administrative command, never a model/MCP operation.
                var manifest=JsonSerializer.Deserialize<HostEgress.Manifest>(store.ReadEvidence(boundHash))??throw new InvalidDataException("Manifest missing");
                if(manifest.TaskId!=task.TaskId)throw new UnauthorizedAccessException("Approval task mismatch");
                if(Console.IsInputRedirected || Console.IsOutputRedirected)throw new InvalidOperationException("Export approval requires an interactive human terminal");
                Console.WriteLine(JsonSerializer.Serialize(manifest,ClientConfig.JsonOptions));
                Console.WriteLine("Review the redacted export payload and exact scope. Type its complete manifest SHA-256 to authorize this export once: "+boundHash);
                if(Console.ReadLine()!=boundHash)throw new UnauthorizedAccessException("Exact human export approval was not confirmed");
                store.BindExportApproval(task.TaskId,boundHash,manifest.PolicyHash,DateTimeOffset.UtcNow.AddMinutes(15),Environment.UserName);
                journal.Record("human_export_approval",new{manifest_hash=boundHash,policy_hash=manifest.PolicyHash},"human_explicit");return;
            }
            var rescue=new HostRescue(journal);var ticket=rescue.Load(boundHash);
            if(command=="rescue-export") {
                var export=rescue.PreparedExport(ticket);using var lease=new HostEgress(store).AuthorizeDispatch(export,"codex_protocol");Console.Write(export.Payload);return;
            }
            if(command=="rescue-import") {
                var json=await RescueInput.ReadAsync(Console.OpenStandardInput());
                var candidate=JsonSerializer.Deserialize<HostRescue.Candidate>(json)??throw new InvalidDataException("Candidate missing");
                Console.WriteLine(JsonSerializer.Serialize(new{candidate_hash=await rescue.ImportAsync(boundHash,candidate)}));return;
            }
            var evidenceHash=args.ElementAtOrDefault(3)??throw new ArgumentException("Candidate/validation hash required");
            if(command=="rescue-validate"){Console.WriteLine(JsonSerializer.Serialize(new{validation_hash=await rescue.ValidateAsync(boundHash,evidenceHash)}));return;}
            if(command=="learning-extract"){Console.WriteLine(JsonSerializer.Serialize(new{learning_candidate_hash=await new DifferentialLearning(journal).ExtractAsync(boundHash,evidenceHash),adoption_status="QUARANTINED_NOT_ADOPTED"}));return;}
            await rescue.PromoteAsync(boundHash,evidenceHash);Console.WriteLine(JsonSerializer.Serialize(new{promoted=true,task_completion="Requires ordinary completion gates"}));return;
        }
        if (command == "validation")
        {
            var root = allowlist.Resolve(args.ElementAtOrDefault(1) ?? Environment.CurrentDirectory);
            var validation = LocalValidationV2.ReadCanonical(root);
            Console.WriteLine(JsonSerializer.Serialize(new { workspace = root, eligible_for_completion = validation.IsComplete,
                validation }, ClientConfig.JsonOptions));
            if (!validation.IsComplete) Environment.ExitCode = 2;
            return;
        }
        if (command == "laya-health")
        {
            await LayaHealthAsync();
            return;
        }
        if (command == "reviewer-route-health")
        {
            await using var supervisor = new LayaSupervisor();
            var sample = new { risk = "normal", files_changed = 2, diff_lines = 40,
                tests_passed = true, languages = ".cs", task_kind = "bug_fix",
                public_api = false, security = false, concurrency = false };
            var prediction = await supervisor.PredictReviewRouteAsync(sample);
            if (prediction.Route is not ("copilot" or "sol" or "both") ||
                !double.IsFinite(prediction.Confidence) ||
                prediction.Confidence is < 0 or > 1)
                throw new InvalidDataException("Laya reviewer route response is invalid");
            Console.WriteLine(JsonSerializer.Serialize(new { laya_reviewer_route = "ready",
                route = prediction.Route, confidence = prediction.Confidence,
                latency_ms = prediction.LatencyMs }, ClientConfig.JsonOptions));
            return;
        }
        if (command == "sol-health")
        {
            var ready = await SolReviewer.HealthAsync();
            Console.WriteLine(JsonSerializer.Serialize(new { sol = ready ? "ready" : "unavailable" },
                ClientConfig.JsonOptions));
            if (!ready) Environment.ExitCode = 2;
            return;
        }
        if (command == "approve-high")
        {
            var root = allowlist.Resolve(args.ElementAtOrDefault(1) ?? Environment.CurrentDirectory);
            var requestPath = args.ElementAtOrDefault(2)
                ?? throw new ArgumentException("approve-high requires a request JSON file path");
            if (new FileInfo(requestPath).Length > 64_000 ||
                new FileInfo(requestPath).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("HIGH approval request file is linked or oversized");
            var request = AgentTaskRequest.Parse(await File.ReadAllTextAsync(requestPath));
            await HighRiskApproval.ApproveAsync(root, request);
            return;
        }
        if (command == "knowledge-health")
        {
            var query = await Console.In.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(query) || query.Length > 240)
                throw new InvalidDataException("Knowledge health query must contain 1 to 240 characters");
            var packet = await SecondBrainEvidence.FetchAsync(config, query);
            Console.WriteLine(JsonSerializer.Serialize(new { available = packet.HasEvidence,
                chunk_count = packet.ChunkIds.Length,
                markdown_bytes = System.Text.Encoding.UTF8.GetByteCount(packet.Markdown) },
                ClientConfig.JsonOptions));
            return;
        }
        if (command == "decide-batch")
        {
            await using var supervisor = new LayaSupervisor();
            while (await Console.In.ReadLineAsync() is { } line)
            {
                var state = ParseSupervisorState(line);
                Console.WriteLine(JsonSerializer.Serialize(await SupervisorDecider.DecideAsync(state, supervisor),
                    ClientConfig.JsonOptions));
            }
            return;
        }
        if (command == "decide")
        {
            var input = await Console.In.ReadToEndAsync();
            var state = ParseSupervisorState(input);
            Console.WriteLine(JsonSerializer.Serialize(await SupervisorDecider.DecideAsync(state),
                ClientConfig.JsonOptions));
            return;
        }
        // WSL path helpers inherit standard input. Consume the CLI request first.
        var prompt = command is "ask" or "critic" or "test" or "run" or "resume" or "recover" ?
            await Console.In.ReadToEndAsync() : null;
        var workspace = await WslWorkspace.ResolveAsync(allowlist,
            args.ElementAtOrDefault(1) ?? Environment.CurrentDirectory);
        if (command == "path")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { wsl_cwd = workspace }, ClientConfig.JsonOptions));
            return;
        }
        if (command == "pi-health")
        {
            await PiHealthAsync(workspace);
            return;
        }
        if (command == "test")
        {
            var test = JsonSerializer.Deserialize<RequiredTest>(prompt!, ClientConfig.JsonOptions)
                ?? throw new InvalidDataException("Validation test is invalid");
            var root = allowlist.Resolve(args.ElementAtOrDefault(1) ?? Environment.CurrentDirectory);
            var result = await ToolRouter.RunAsync(root, workspace, test);
            Console.WriteLine(JsonSerializer.Serialize(result, ClientConfig.JsonOptions));
            if (!result.Passed) Environment.ExitCode = 2;
            return;
        }
        if (command is "run" or "resume" or "recover")
        {
            var request = AgentTaskRequest.Parse(prompt!);
            var root = allowlist.Resolve(args.ElementAtOrDefault(1) ?? Environment.CurrentDirectory);
            var result = await AgentTaskRunner.RunAsync(root, workspace, request, config,
                resume: command != "run", recover: command == "recover");
            Console.WriteLine(JsonSerializer.Serialize(result, ClientConfig.JsonOptions));
            if (!result.EligibleForCompletion) Environment.ExitCode = 2;
            return;
        }
        if (command is "ask" or "critic")
        {
            // The prompt is accepted on stdin so it never appears in a process command line.
            var result = await PiRpcRunner.RunAsync(workspace, prompt!, command == "critic");
            Console.WriteLine(JsonSerializer.Serialize(new { workspace, role = command,
                response = result.Text, result.ToolCalls, result.ToolErrors, result.StderrLines,
                result.FilesRead, result.FilesChanged, result.SameFileReadCount, result.SameCommandCount,
                result.SameErrorCount, result.ToolCallsSinceProgress, result.NoProgress,
                result.SessionId }, ClientConfig.JsonOptions));
            if (result.ToolErrors > 0 || string.IsNullOrWhiteSpace(result.Text)) Environment.ExitCode = 2;
            return;
        }
        throw new ArgumentException("agent-v2 commands: path, pi-health, laya-health, reviewer-route-health, sol-health, approve-high, knowledge-health, ask, critic, decide, decide-batch, test, run, resume, recover, validation");
    }

    private static SupervisorState ParseSupervisorState(string input)
    {
        using var document = JsonDocument.Parse(input);
        var root = document.RootElement;
        foreach (var key in new[] { "high_risk_action", "user_approved", "completion_candidate",
            "required_tests_passed", "tests_passed", "last_action_changed_state" })
            if (!root.TryGetProperty(key, out var value) ||
                value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException($"Supervisor state requires boolean {key}");
        return root.Deserialize<SupervisorState>(ClientConfig.JsonOptions)
            ?? throw new InvalidDataException("Supervisor state is invalid");
    }

    private static async Task PiHealthAsync(string workspace)
    {
        var provider = Path.Combine(AppContext.BaseDirectory, "scripts", "localbrain-provider.js");
        var relay = Path.Combine(AppContext.BaseDirectory, "localbrain.exe");
        var wslProvider = await WslWorkspace.MapFileAsync(provider);
        var wslRelay = await WslWorkspace.MapFileAsync(relay);
        var wslPolicy = await WslWorkspace.MapFileAsync(Path.Combine(AppContext.BaseDirectory,
            "scripts", "localbrain-policy.mjs"));
        var start = new ProcessStartInfo("wsl.exe");
        foreach (var arg in new[] { "-d", "Ubuntu", "--cd", workspace, "--exec", "/usr/bin/env",
            $"LOCALBRAIN_RELAY_EXE={wslRelay}", "/home/worker/.volta/bin/pi", "--mode", "rpc",
            "--offline", "--approve", "--provider", "localbrain", "--model", "local-qwen38",
            "--extension", wslProvider, "--extension", wslPolicy, "--tools", "read" }) start.ArgumentList.Add(arg);
        await using var worker = JsonlWorker.Start(start);
        var response = await worker.SendAsync(new() { ["type"] = "get_state" }, TimeSpan.FromSeconds(30));
        if (!response.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Pi RPC health check failed");
        var data = response.GetProperty("data");
        var model = data.GetProperty("model");
        if (model.ValueKind != JsonValueKind.Object ||
            model.GetProperty("id").GetString() != "local-qwen38" ||
            model.GetProperty("provider").GetString() != "localbrain")
            throw new InvalidOperationException("Pi did not select the LocalBrain model");
        Console.WriteLine(JsonSerializer.Serialize(new { pi_rpc = "ready", workspace,
            model = data.GetProperty("model").GetProperty("id").GetString(),
            provider = "localbrain",
            session_id = data.GetProperty("sessionId").GetString(), stderr_lines = worker.StderrLineCount },
            ClientConfig.JsonOptions));
    }

    private static async Task LayaHealthAsync()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "scripts", "laya_worker.py");
        if (!File.Exists(script)) throw new FileNotFoundException("Laya worker script is not deployed", script);
        var wslScript = await WslPathAsync(script);
        var start = new ProcessStartInfo("wsl.exe");
        foreach (var arg in new[] { "-d", "Ubuntu", "--exec",
            "/home/worker/localbrain-v2/laya-venv/bin/python", "-u", wslScript, "--threads", "2" })
            start.ArgumentList.Add(arg);
        var ready = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = JsonlWorker.Start(start, message =>
        {
            if (message.TryGetProperty("type", out var kind) && kind.GetString() == "ready")
                ready.TrySetResult(message);
        });
        var startup = await ready.Task.WaitAsync(TimeSpan.FromMinutes(5));
        var state = new { phase = "debugging", tool_calls = 4, files_read = 2, files_changed = 1,
            tests_run = 1, tests_passed = false, same_error_count = 0, same_command_count = 0,
            replans = 0, context_usage = 0.2, progress = "medium", last_action_changed_state = true };
        var answer = await worker.SendAsync(new() { ["state"] = state }, TimeSpan.FromSeconds(60));
        if (answer.TryGetProperty("error", out _)) throw new InvalidOperationException("Laya inference failed");
        Console.WriteLine(JsonSerializer.Serialize(new { laya = "ready", startup,
            action = answer.GetProperty("action").GetString(),
            confidence = answer.GetProperty("confidence").GetDouble(),
            latency_ms = answer.GetProperty("latency_ms").GetInt32() }, ClientConfig.JsonOptions));
    }

    private static async Task<string> WslPathAsync(string windowsPath)
    {
        var start = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-d", "Ubuntu", "--exec", "wslpath", "-a", "-u", windowsPath })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("WSL failed to start");
        var output = await process.StandardOutput.ReadToEndAsync();
        _ = await process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0) throw new InvalidOperationException("WSL path conversion failed");
        return output.Trim();
    }
}


