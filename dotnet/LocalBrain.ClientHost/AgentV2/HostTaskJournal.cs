using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalBrain.ClientHost.AgentV2;

// Checkpoint JSON inside a repository is a compatibility projection. This journal commits first.
internal sealed class HostTaskJournal : IDisposable
{
    private static readonly AsyncLocal<HostTaskJournal?> current = new();
    public static HostTaskJournal? Current => current.Value;
    private readonly HostTaskJournal? previous;
    private readonly CanonicalStore store;
    private readonly string taskId;
    public string ExpectedHead { get; }
    internal bool PersonalValidationEnabled { get; private set; }
    internal void EnablePersonalValidation()
    {
        if(PersonalValidationEnabled)return;
        Record("trusted_personal_validation_enabled",new{mode="trusted_personal_repositories",authority="windows_host_configuration",hostile_repository_isolation_guaranteed=false},"host_verified");
        PersonalValidationEnabled=true;
    }
    private readonly string requestJson;
    private readonly bool requiresApproval;
    private readonly string workspaceId;
    private readonly string root;
    private readonly long previousTools;
    private long toolsThisRun;
    private long turnsRecorded;
    private readonly string invocationId = Guid.NewGuid().ToString("N");
    private long eventNumber;
    private LocalValidationV2? validation;
    private bool observesTools;
    private readonly Dictionary<string, string> toolStarts = new(StringComparer.Ordinal);
    internal static CanonicalStore.TaskSnapshot? ReadLatest(string root) =>
        CanonicalStore.ReadLatest(CanonicalStore.DefaultRoot, WorkspaceId(root));
    internal static string WorkspaceId(string root) => "ws_" + CanonicalStore.Hash(
        Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\').ToUpperInvariant()));
    internal static string RepositoryId(string root) => "repo_" + WorkspaceId(root)[3..];

    public HostTaskJournal(CanonicalStore store, string root, string taskId, AgentTaskRequest request, string head)
    {
        this.store = store; this.taskId = taskId; this.root=root; workspaceId = WorkspaceId(root); ExpectedHead=head;
        store.RegisterWorkspace(new(RepositoryId(root), workspaceId, Path.GetFullPath(root),
            Path.GetFullPath(root), "main", ""));
        requestJson = AgentTaskRunner.RequestJson(request);
        requiresApproval=request.EffectiveRisk=="HIGH";
        store.CreateTask(taskId, workspaceId, CanonicalStore.Hash(Encoding.UTF8.GetBytes(requestJson)), requestJson);
        var saved = store.GetTask(taskId)!;
        previousTools = saved.ToolCalls; turnsRecorded = saved.ActorTurns;
        previous = current.Value; current.Value = this;
        Record("host_invocation", new { head, request_hash = saved.RequestHash }, "host_verified");
    }

    public void Checkpoint(object state) => CheckpointCore(state);
    private CanonicalStore.EventReceipt CheckpointCore(object state,CanonicalStore.EventInput? epochEvent=null)
    {
        var json = JsonSerializer.SerializeToNode(state, ClientConfig.JsonOptions)!.AsObject();
        var previousState=JsonNode.Parse(store.GetTask(taskId)!.StateJson)?.AsObject();
        if(previousState is not null)foreach(var entry in previousState)
            if(entry.Key!="tool_calls" && !json.ContainsKey(entry.Key))json[entry.Key]=entry.Value?.DeepClone();
        if(previousState?["false_verified"]?.GetValue<bool>()==true)json["false_verified"]=true;
        var turns = json["actor_turns"]?.GetValue<long>() ?? turnsRecorded;
        var tools = json["tool_calls"]?.GetValue<long>() ?? toolsThisRun;
        if (turns < turnsRecorded || tools < toolsThisRun)
            throw new InvalidDataException("Checkpoint cannot reset task counters");
        json["cumulative_tool_calls"] = observesTools ? store.GetTask(taskId)!.ToolCalls : previousTools + tools;
        json["canonical_workspace_id"] = workspaceId;
        if (validation is not null) json["validation"] = JsonSerializer.SerializeToNode(validation);
        var text = json.ToJsonString();
        var evidence = store.PutEvidence(Encoding.UTF8.GetBytes(text));
        var input=epochEvent is null?Input("task_checkpoint",new {phase=json["phase"]?.GetValue<string>()},"host_observed",[evidence]):epochEvent with {EvidenceRefs=[..epochEvent.EvidenceRefs,evidence]};
        var receipt=store.Checkpoint(input, text, turns - turnsRecorded, observesTools ? 0 : tools - toolsThisRun);
        turnsRecorded = turns; toolsThisRun = tools;return receipt;
    }

    public void Record(string type, object metadata, string verification = "unverified")
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata);
        var evidence = store.PutEvidence(bytes);
        store.Append(Input(type, new { byte_length = bytes.Length }, verification, [evidence]));
    }

    public void SaveValidation(LocalValidationV2 result)
    {
        Record("validation_result", result, "host_verified");
        validation = result;
    }

    public void RecordPrompt(string prompt, bool critic)
    {
        var hash = store.PutEvidence(Encoding.UTF8.GetBytes(prompt));
        store.RecordPrompt(Input("host_prompt", new { scope="host_instruction_packet", critic },
            "host_verified", [hash]), hash);
    }

    public void RecordProviderPrompt(string payload, int measuredTokens)
    {
        var hash=store.PutEvidence(Encoding.UTF8.GetBytes(payload));
        store.RecordPrompt(Input("provider_prompt",new { scope="final_provider_payload", measured_tokens=measuredTokens },
            "runtime_observed",[hash]),hash);
    }

    public bool HasApproval => store.HasApproval(taskId,AgentTaskRunner.HashTextForHost(requestJson),ExpectedHead);
    public void BindApproval(string id,DateTimeOffset expiresAt)
    {
        store.BindApproval(id,taskId,AgentTaskRunner.HashTextForHost(requestJson),ExpectedHead,expiresAt);
        Record("human_approval_bound",new {approval_id=id,expires_at=expiresAt},"host_verified");
    }
    public void RequireMutationApproval()
    {
        if(requiresApproval && !HasApproval)throw new UnauthorizedAccessException("Task approval is unavailable or expired");
    }
    public string Recall(string hash,int offset)=>store.Recall(taskId,hash,offset);
    public string[] EvidenceRefs()=>store.TaskEvidence(taskId);
    public TaskMemoryQ8 CreateMemory()=>new(store.Root);
    internal CanonicalStore.EventReceipt CommitMemoryEpoch(TaskMemoryEpoch.Seed seed) {
        var task=store.GetTask(taskId)!;
        var previousEpoch=JsonNode.Parse(task.StateJson)?["taskmemory_epoch"]?["number"]?.GetValue<int>()??0;
        if(previousEpoch<0 || previousEpoch==int.MaxValue)throw new InvalidDataException("Invalid canonical memory epoch counter");
        var epoch=previousEpoch+1;
        var seedHash=store.PutEvidence(Encoding.UTF8.GetBytes(seed.Json));
        if(seedHash!=seed.Hash)throw new InvalidDataException("Epoch seed integrity mismatch");
        var input=Input("taskmemory_epoch_seed",new {epoch,seed_hash=seedHash,seed.SourceFrontier,seed.QueryHash,algorithm=TaskMemoryRetrieval.Algorithm,verification_scope="Canonical source identity/progress only; recorded content claims retain original authority"},"host_verified",[seedHash]);
        // Seed event, its immutable references and canonical epoch state commit in one sequencer transaction.
        return CheckpointCore(new {taskmemory_epoch=new {number=epoch,seed_hash=seedHash,source_frontier=seed.SourceFrontier,query_hash=seed.QueryHash,algorithm=TaskMemoryRetrieval.Algorithm}},input);
    }
    public async Task<string[]> SelectMemoryRefsAsync(TaskMemoryQ8 memory,string query,CancellationToken token) {
        try {
            if(!memory.Enabled)return [];
            var boundedQuery=query.Length<=8192?query:query[..8192];
            var retrieval=store.RetrieveMemoryCandidates(taskId,boundedQuery);
            var retrieved=retrieval.Candidates;
            var task=store.GetTask(taskId)!;
            var seed=TaskMemoryEpoch.Build(taskId,requestJson,ExpectedHead,task,boundedQuery,retrieved,retrieval.SourceFrontier);
            var receipt=CommitMemoryEpoch(seed);var seedHash=seed.Hash;
            // Every recall starts a fresh owned native state. No KV cache is recovery authority.
            await memory.DisposeAsync();
            var candidates=retrieved.Append(new TaskMemorySelection.Candidate(taskId,receipt.EventId,receipt.Sequence,"taskmemory_epoch_seed","host_verified",[seedHash],seed.Json.Length<=2048?seed.Json:seed.Json[..2048])).ToArray();
            var instruction=TaskMemorySelection.Prompt(taskId,boundedQuery,candidates,seed.Json);
            var prediction=await memory.PredictAsync(instruction,candidates.Select(c=>c.EventId).ToArray(),(payload,tokens)=> {
                var hash=store.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(payload));
                store.RecordPrompt(Input("taskmemory_prompt",new {scope="native_final_payload",measured_tokens=tokens,candidate_event_ids=candidates.Select(c=>c.EventId)},"runtime_observed",[hash]),hash);
            },token);
            Record("taskmemory_response",new {prediction.Content,prediction.PromptTokens},"model_claim");
            var selected=TaskMemorySelection.Validate(taskId,candidates,prediction.Content);
            Record("taskmemory_selection",new {selected.EventIds,selected.EvidenceRefs,verification_scope="ID membership and source integrity only; content claims retain original authority"},"host_verified");
            return selected.EvidenceRefs;
        } catch(OperationCanceledException) when(token.IsCancellationRequested) {throw;}
        catch(Exception e) when(e is not OutOfMemoryException) {
            Record("taskmemory_unavailable",new {reason=e.GetType().Name,fallback="canonical_task_evidence"},"host_observed");
            await memory.DisposeAsync();return [];
        }
    }
    public async Task<string[]> RecallRefsAsync(TaskMemoryQ8 memory,string query,CancellationToken token) {
        var selected=await SelectMemoryRefsAsync(memory,query,token);
        return selected.Take(16).Concat(EvidenceRefs().TakeLast(24)).Distinct(StringComparer.Ordinal).ToArray();
    }
    public void CheckpointRuntime(GitEvidence.Snapshot git,string? phase=null,object? pendingMutation=null)
    {
        var state=JsonNode.Parse(store.GetTask(taskId)!.StateJson)!.AsObject();
        if(phase is not null)state["phase"]=phase;
        state["changed_files"]=JsonSerializer.SerializeToNode(git.ChangedFiles);
        state["review_diff_sha256"]=CanonicalStore.Hash(Encoding.UTF8.GetBytes(git.ReviewDiff));
        state["pending_mutation"]=pendingMutation is null?null:JsonSerializer.SerializeToNode(pendingMutation);
        Checkpoint(state);
    }
    public void MutationIntent(GitEvidence.Snapshot git,string relative,byte[] bytes,string[] scope)
    {
        var expected=new Dictionary<string,string>();
        foreach(var file in scope) {
            var path=Path.GetFullPath(Path.Combine(root,file));CanonicalStore.GuardPath(path);
            expected[file]=File.Exists(path)?CanonicalStore.Hash(File.ReadAllBytes(path)):"missing";
        }
        expected[relative]=CanonicalStore.Hash(bytes);
        CheckpointRuntime(git,pendingMutation:new {expected_file_hashes=expected});
    }
    public long CumulativeTools=>store.GetTask(taskId)!.ToolCalls;
    public void RecordContext(string snapshotJson,int promptTokens)
    {
        using var doc=JsonDocument.Parse(snapshotJson);var value=doc.RootElement;
        if(value.GetProperty("context_limit").GetInt32()!=32768 || value.GetProperty("estimated_prompt_tokens").GetInt32()!=promptTokens)
            throw new InvalidDataException("Context snapshot does not match the provider payload");
        var tokens=new Dictionary<string,int>();
        foreach(var name in new[]{"system_prompt","tool_schema","tool_result","conversation","retrieval","framing"}) {
            var n=value.GetProperty("tokens").GetProperty(name).GetInt32();
            if(n<0)throw new InvalidDataException("Invalid context token component");tokens[name]=n;
        }
        if(tokens.Values.Sum()!=promptTokens)throw new InvalidDataException("Context token components do not match full payload");
        var shares=new Dictionary<string,double>();
        foreach(var name in new[]{"tool_schema","tool_result","conversation","retrieval"})shares[name]=(double)tokens[name]/promptTokens;
        var exposed=value.GetProperty("tool_count_exposed").GetInt32();
        if(exposed<0 || exposed>1000)throw new InvalidDataException("Invalid exposed tool count");
        var previous=ContextTelemetryState.Read(root,taskId);
        var snapshot=new {timestamp=DateTimeOffset.UtcNow,task_id=taskId,context_metrics_available=true,
            context_limit=32768,estimated_prompt_tokens=promptTokens,context_usage=(double)promptTokens/32768,
            tokens,shares,tool_count_exposed=exposed,measurement_ms=0,
            context_growth_since_last_check=previous is null?(double?)null:(double)promptTokens/32768-previous.ContextUsage};
        Record("context_measurement",snapshot,"runtime_observed");
        var directory=Path.Combine(root,".localbrain");CanonicalStore.GuardPath(directory);Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"context-current.json");CanonicalStore.GuardPath(path);
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {File.WriteAllText(temp,JsonSerializer.Serialize(snapshot));File.Move(temp,path,true);}
        finally {if(File.Exists(temp))File.Delete(temp);}
    }

    public void RotateSession(string oldSessionId, string newSessionId, GitEvidence.Snapshot git)
    {
        var snapshot=store.GetTask(taskId)!;
        var frozen=JsonNode.Parse(snapshot.StateJson)!.AsObject();
        var packet=new { task_id=taskId,workspace_id=workspaceId,request_json=requestJson,
            frozen_session_id=oldSessionId,next_session_id=newSessionId,git_head=git.Head,
            current_diff_hash=CanonicalStore.Hash(Encoding.UTF8.GetBytes(git.ReviewDiff)),
            current_diff=git.ReviewDiff,changed_files=git.ChangedFiles,
            counters=new { snapshot.ActorTurns,snapshot.ToolCalls,snapshot.ExternalReviews,snapshot.Handoffs },
            validation=frozen["validation"]?.DeepClone(),canonical_event_ids=store.EventIds(taskId) };
        var evidence=store.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(packet));
        frozen["phase"]="handoff_ready";frozen["pi_session_id"]=newSessionId;
        frozen["handoff_count"] = snapshot.Handoffs+1;
        store.Checkpoint(Input("context_handoff",new {old_session_id=oldSessionId,new_session_id=newSessionId},
            "host_verified",[evidence]),frozen.ToJsonString(),handoffsDelta:1);
    }

    public void RecordToolEvent(JsonElement message)
    {
        var type = message.GetProperty("type").GetString()!;
        var toolId = message.GetProperty("toolCallId").GetString()!;
        observesTools = true;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        var hash = store.PutEvidence(bytes);
        var key = invocationId + "_" + type + "_" + CanonicalStore.Hash(Encoding.UTF8.GetBytes(toolId));
        var input = new CanonicalStore.EventInput(taskId, workspaceId, "windows_host", key,
            "pi_" + type, "tool_execution", "runtime_observed", [hash],
            JsonSerializer.SerializeToElement(new { tool_call_id=toolId }), ProducerEventId:toolId,
            CorrelationId:invocationId,
            CausedByEventId:type=="tool_execution_end" && toolStarts.TryGetValue(toolId,out var cause)?cause:null);
        var receipt = store.CountedAppend(input, tools:type=="tool_execution_start"?1:0);
        if (type=="tool_execution_start") toolStarts[toolId]=receipt.EventId;
    }

    internal CanonicalStore Canonical => store;
    internal string TaskId => taskId;
    internal string WindowsRoot => root;
    internal sealed class PacketDispatch(string packet,IDisposable lease):IDisposable {
        public string Packet {get;}=packet;
        public void Dispose()=>lease.Dispose();
    }
    internal HostEgress.Prepared PrepareExternalPacket(string packet,string provider,string[]? sources=null,string[]? files=null) {
        var accepted=store.RequireExportPolicy(taskId);var sourceIds=sources??[];
        var hash=store.PutEvidence(Encoding.UTF8.GetBytes(packet));
        var normalizedFiles=(files??[]).Select(p=>HostEgress.NormalizePath(root,p)).ToArray();
        var pending=store.PendingPacket(taskId,accepted.Hash,provider,hash,sourceIds,normalizedFiles);
        if(pending is not null)return pending;
        var receipt=store.Append(Input("host_approved_export_input",new {approved_source_ids=sourceIds,approved_file_refs=normalizedFiles.Select(p=>new HostEgress.FileRef(p,hash)).ToArray(),provider},"host_verified",[hash]));
        return new HostEgress(store).Prepare(taskId,[receipt.EventId],sourceIds,normalizedFiles.Select(p=>new HostEgress.FileRef(p,hash)).ToArray(),["Host-selected bounded review/task packet"],true);
    }
    internal PacketDispatch BeginExternalPacket(string packet,string provider,string[]? sources=null,string[]? files=null) {
        var export=PrepareExternalPacket(packet,provider,sources,files);
        return new(export.Payload,new HostEgress(store).AuthorizeDispatch(export,provider));
    }
    internal void ExternalAdapterUnavailable(string provider,string reason) => Record("external_adapter_unavailable",new {provider,reason},"host_verified");

    public void ReviewStarted(string provider)
    {
        store.CountedAppend(Input("external_review_started", new { provider }, "host_observed", []), reviews:1);
    }

    private CanonicalStore.EventInput Input(string type, object metadata, string verification, string[] evidence)
        => new(taskId, workspaceId, "windows_host", invocationId + "_" + Interlocked.Increment(ref eventNumber),
            type, "execution_evidence", verification, evidence, JsonSerializer.SerializeToElement(metadata),
            CorrelationId: invocationId);
    public void Dispose() { current.Value = previous; }
}
