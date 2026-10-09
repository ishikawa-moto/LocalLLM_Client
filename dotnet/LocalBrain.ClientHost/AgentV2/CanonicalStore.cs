using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

// This API belongs to the Windows Host, never to a model tool or repository configuration.
internal sealed partial class CanonicalStore : IDisposable
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalBrain", "control-plane");
    private readonly object gate = new();
    private readonly FileStream writerLease;
    private readonly SqliteConnection db;
    public string Root { get; }
    private readonly Action<string>? fault;
    internal sealed record Workspace(string RepositoryId, string WorkspaceId, string CanonicalRepoRoot,
        string WorkspaceRoot, string Role, string BaseCommit);
    internal sealed record EventInput(string TaskId, string WorkspaceId, string Producer,
        string IdempotencyKey, string EventType, string EvidenceClass, string VerificationStatus,
        string[] EvidenceRefs, JsonElement Metadata, string? ProducerEventId = null,
        string? CorrelationId = null, string? CausedByEventId = null, string? WorkerId = null);
    internal sealed record EventReceipt(string EventId, long Sequence, bool Duplicate, string CommittedAt);
    internal sealed record TaskSnapshot(string TaskId, string WorkspaceId, string RequestHash,
        string StateJson, long ActorTurns, long ToolCalls, long Handoffs, long ExternalReviews);
    internal sealed record Policy(string RepositoryId, string CanonicalRepoRoot, string Mode,
        string[] WriteScope, string[] ExportScope);
    internal sealed record PolicyStatus(string Mode, string? Hash, bool AuditMatched);

    public CanonicalStore(string root, Action<string>? fault = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Control-plane owner must be Windows");
        Root = Path.GetFullPath(root);
        this.fault = fault;
        GuardPath(Root);
        Directory.CreateDirectory(Root);
        GuardPath(Root);
        Directory.CreateDirectory(Path.Combine(Root, "evidence"));
        GuardPath(Path.Combine(Root, "evidence"));
        GuardPath(Path.Combine(Root, "writer.lock"));
        writerLease = new FileStream(Path.Combine(Root, "writer.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            GuardPath(Path.Combine(Root, "canonical.db"));
            GuardPath(Path.Combine(Root, "canonical.db-wal"));
            GuardPath(Path.Combine(Root, "canonical.db-shm"));
            db = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = Path.Combine(Root, "canonical.db"), Pooling = false
            }.ToString());
            db.Open();
            Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
            var version = Convert.ToInt32(Scalar("PRAGMA user_version;"));
            if (version > 1) throw new InvalidDataException("Canonical schema is newer than this Host");
            using var tx = db.BeginTransaction();
            Execute(Schema + EgressSchema + DecisionAdoptionSchema, tx);
            Execute("PRAGMA user_version=1;", tx);
            tx.Commit();
        }
        catch { writerLease.Dispose(); throw; }
    }

    public void RegisterWorkspace(Workspace w)
    {
        if (w.Role is not ("main" or "validation" or "local_worker" or "remote_worker" or "codex_rescue"))
            throw new InvalidDataException("Unknown worktree role");
        ValidateId(w.RepositoryId); ValidateId(w.WorkspaceId);
        GuardPath(w.CanonicalRepoRoot); GuardPath(w.WorkspaceRoot);
        if (Within(w.WorkspaceRoot, Root) || Within(w.CanonicalRepoRoot, Root))
            throw new UnauthorizedAccessException("Canonical storage must be outside repository workspaces");
        lock (gate)
        {
            var old = Scalar("SELECT identity_json FROM workspaces WHERE workspace_id=$id;",
                null, ("$id", w.WorkspaceId)) as string;
            var json = JsonSerializer.Serialize(w);
            if (old is not null) {
                if (old != json) throw new InvalidDataException("Workspace identity is immutable");
                return;
            }
            Execute("INSERT INTO workspaces VALUES($id,$repo,$role,$json);", null,
                ("$id", w.WorkspaceId), ("$repo", w.RepositoryId), ("$role", w.Role), ("$json", json));
        }
    }

    public void CreateTask(string taskId, string workspaceId, string requestHash, string requestJson)
    {
        ValidateId(taskId); ValidateId(workspaceId);
        if (Hash(Encoding.UTF8.GetBytes(requestJson)) != requestHash)
            throw new InvalidDataException("Task request hash does not match immutable request bytes");
        using var document = JsonDocument.Parse(requestJson);
        lock (gate)
        {
            var old = GetTask(taskId);
            if (old is not null) {
                if (old.WorkspaceId != workspaceId || old.RequestHash != requestHash)
                    throw new InvalidDataException("Task identity/request is immutable");
                return;
            }
            using var tx = db.BeginTransaction();
            Execute("INSERT INTO tasks(task_id,workspace_id,request_hash,request_json,state_json) VALUES($id,$ws,$hash,$request,'{}');",
                tx, ("$id", taskId), ("$ws", workspaceId), ("$hash", requestHash), ("$request", requestJson));
            Execute("INSERT INTO task_counters(task_id) VALUES($id);", tx, ("$id", taskId));
            tx.Commit();
        }
    }

    public TaskSnapshot? GetTask(string taskId)
    {
        lock (gate)
        {
            using var cmd = Command("""
                SELECT t.workspace_id,t.request_hash,t.state_json,c.actor_turns,c.tool_calls,c.handoffs,c.external_reviews
                FROM tasks t JOIN task_counters c ON c.task_id=t.task_id WHERE t.task_id=$id;
                """, null, ("$id", taskId));
            using var r = cmd.ExecuteReader();
            return r.Read() ? new(taskId, r.GetString(0), r.GetString(1), r.GetString(2),
                r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6)) : null;
        }
    }

    public TaskSnapshot? LatestTask(string workspaceId)
    {
        lock (gate) {
            var id = Scalar("SELECT task_id FROM tasks WHERE workspace_id=$ws ORDER BY rowid DESC LIMIT 1;",
                null, ("$ws", workspaceId)) as string;
            return id is null ? null : GetTask(id);
        }
    }

    public static TaskSnapshot? ReadLatest(string storageRoot, string workspaceId)
    {
        var path = Path.Combine(Path.GetFullPath(storageRoot), "canonical.db");
        GuardPath(path); GuardPath(path+"-wal"); GuardPath(path+"-shm");
        if (!File.Exists(path)) return null;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource=path, Mode=SqliteOpenMode.ReadOnly, Pooling=false }.ToString());
        connection.Open();
        using var cmd=connection.CreateCommand();
        cmd.CommandText="""
            SELECT t.task_id,t.workspace_id,t.request_hash,t.state_json,c.actor_turns,c.tool_calls,c.handoffs,c.external_reviews
            FROM tasks t JOIN task_counters c ON c.task_id=t.task_id
            WHERE t.workspace_id=$ws ORDER BY t.rowid DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$ws",workspaceId);
        using var r=cmd.ExecuteReader();
        return r.Read()?new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),
            r.GetInt64(4),r.GetInt64(5),r.GetInt64(6),r.GetInt64(7)):null;
    }

    public string PutEvidence(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 48 * 1024 * 1024) throw new InvalidDataException("Evidence exceeds Host limit");
        var hash = Hash(bytes);
        lock (gate)
        {
            var destination = EvidencePath(hash);
            GuardPath(destination);
            if (!File.Exists(destination))
            {
                var temporary = Path.Combine(Root, "evidence", Guid.NewGuid().ToString("N") + ".tmp");
                try {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
                        stream.Write(bytes);
                        stream.Flush(flushToDisk: true);
                    }
                    fault?.Invoke("evidence_flushed");
                    File.Move(temporary, destination, overwrite: false);
                    File.SetAttributes(destination, FileAttributes.ReadOnly);
                    fault?.Invoke("evidence_renamed");
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            VerifyEvidenceFile(hash);
            Execute("INSERT OR IGNORE INTO evidence(hash,byte_length) VALUES($hash,$length);",
                null, ("$hash", hash), ("$length", bytes.Length));
            return hash;
        }
    }

    public byte[] ReadEvidence(string hash)
    {
        lock (gate) { VerifyEvidenceFile(hash); return File.ReadAllBytes(EvidencePath(hash)); }
    }

    public EventReceipt Append(EventInput input) => CommitEvent(input, null, 0, 0, 0, 0);

    public EventReceipt CountedAppend(EventInput input, long tools = 0, long reviews = 0)
    {
        if (tools < 0 || reviews < 0) throw new InvalidDataException("Counters only accumulate");
        return CommitEvent(input, null, 0, tools, 0, reviews);
    }

    public EventReceipt RecordPrompt(EventInput input, string promptHash)
    {
        ValidateHash(promptHash);
        if (!input.EvidenceRefs.Contains(promptHash)) throw new InvalidDataException("Prompt bytes must be retained");
        return CommitEvent(input, null, 0, 0, 0, 0, tx => Execute("""
            INSERT INTO prompt_ledger(prompt_id,task_id,event_id,payload_hash,source_refs_json,payload_tokens)
            SELECT $id,$task,event_id,$hash,'[]',NULL FROM events
            WHERE task_id=$task AND producer=$producer AND idempotency_key=$key;
            """,tx,("$id",Guid.NewGuid().ToString("N")),("$task",input.TaskId),("$hash",promptHash),
            ("$producer",input.Producer),("$key",input.IdempotencyKey)));
    }

    public EventReceipt Checkpoint(EventInput input, string stateJson,
        long actorTurnsDelta = 0, long toolCallsDelta = 0, long handoffsDelta = 0, long reviewsDelta = 0)
    {
        using var json = JsonDocument.Parse(stateJson);
        if (actorTurnsDelta < 0 || toolCallsDelta < 0 || handoffsDelta < 0 || reviewsDelta < 0)
            throw new InvalidDataException("Task counters only accumulate");
        return CommitEvent(input, stateJson, actorTurnsDelta, toolCallsDelta, handoffsDelta, reviewsDelta);
    }

    private EventReceipt CommitEvent(EventInput input, string? state, long turns, long tools, long handoffs, long reviews,
        Action<SqliteTransaction>? additionalMutation = null)
    {
        ValidateId(input.TaskId); ValidateId(input.WorkspaceId);
        if (string.IsNullOrWhiteSpace(input.IdempotencyKey) || input.IdempotencyKey.Length > 256 ||
            string.IsNullOrWhiteSpace(input.Producer) || input.EventType.Length > 128 ||
            input.EvidenceRefs.Length > 200 || input.Metadata.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid producer event");
        var payload = JsonSerializer.Serialize(new { input, state, turns, tools, handoffs, reviews });
        if (Encoding.UTF8.GetByteCount(payload) > 512 * 1024) throw new InvalidDataException("Event exceeds Host limit");
        var payloadHash = Hash(Encoding.UTF8.GetBytes(payload));
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            foreach (var hash in input.EvidenceRefs.Distinct(StringComparer.Ordinal)) VerifyEvidenceFile(hash);
            using (var existing = Command("""
                SELECT event_id,sequence,payload_hash,committed_at FROM events
                WHERE task_id=$task AND producer=$producer AND idempotency_key=$key;
                """, tx, ("$task", input.TaskId), ("$producer", input.Producer), ("$key", input.IdempotencyKey)))
            using (var r = existing.ExecuteReader())
            {
                if (r.Read()) {
                    if (r.GetString(2) != payloadHash) throw new InvalidDataException("Idempotency key payload conflict");
                    return new(r.GetString(0), r.GetInt64(1), true, r.GetString(3));
                }
            }
            if (Convert.ToInt64(Scalar("""
                SELECT count(*) FROM tasks t JOIN workspaces w ON w.workspace_id=$ws
                JOIN workspaces tw ON tw.workspace_id=t.workspace_id
                WHERE t.task_id=$task AND tw.repository_id=w.repository_id;
                """, tx, ("$task", input.TaskId), ("$ws", input.WorkspaceId))) != 1)
                throw new InvalidDataException("Unknown or unrelated task/workspace");
            if (input.CausedByEventId is { } cause && Convert.ToInt64(Scalar(
                "SELECT count(*) FROM events WHERE event_id=$id AND task_id=$task;",
                tx, ("$id", cause), ("$task", input.TaskId))) != 1)
                throw new InvalidDataException("Causal event does not belong to task");
            foreach (var hash in input.EvidenceRefs.Distinct(StringComparer.Ordinal))
            {
                VerifyEvidenceFile(hash);
                if (Convert.ToInt64(Scalar("SELECT count(*) FROM evidence WHERE hash=$hash;",
                    tx, ("$hash", hash))) != 1) throw new InvalidDataException("Evidence is not committed");
            }
            var sequence = Convert.ToInt64(Scalar("SELECT coalesce(max(sequence),0)+1 FROM events WHERE task_id=$task;",
                tx, ("$task", input.TaskId)));
            var id = Guid.NewGuid().ToString("N");
            var at = DateTimeOffset.UtcNow.ToString("O");
            Execute("""
                INSERT INTO events(event_id,task_id,workspace_id,producer,idempotency_key,sequence,
                event_type,evidence_class,verification_status,producer_event_id,correlation_id,
                caused_by_event_id,worker_id,payload_hash,payload_json,committed_at)
                VALUES($id,$task,$ws,$producer,$key,$seq,$type,$class,$status,$pid,$corr,$cause,$worker,$hash,$payload,$at);
                """, tx, ("$id", id), ("$task", input.TaskId), ("$ws", input.WorkspaceId),
                ("$producer", input.Producer), ("$key", input.IdempotencyKey), ("$seq", sequence),
                ("$type", input.EventType), ("$class", input.EvidenceClass), ("$status", input.VerificationStatus),
                ("$pid", input.ProducerEventId), ("$corr", input.CorrelationId), ("$cause", input.CausedByEventId),
                ("$worker", input.WorkerId), ("$hash", payloadHash), ("$payload", payload), ("$at", at));
            foreach (var hash in input.EvidenceRefs.Distinct(StringComparer.Ordinal))
                Execute("INSERT INTO event_evidence VALUES($id,$hash);", tx, ("$id", id), ("$hash", hash));
            if (state is not null)
            {
                Execute("UPDATE tasks SET state_json=$state WHERE task_id=$task;", tx,
                    ("$state", state), ("$task", input.TaskId));
            }
            if (turns != 0 || tools != 0 || handoffs != 0 || reviews != 0)
            {
                Execute("""
                    UPDATE task_counters SET actor_turns=actor_turns+$turns,tool_calls=tool_calls+$tools,
                    handoffs=handoffs+$handoffs,external_reviews=external_reviews+$reviews WHERE task_id=$task;
                    """, tx, ("$turns", turns), ("$tools", tools), ("$handoffs", handoffs),
                    ("$reviews", reviews), ("$task", input.TaskId));
            }
            additionalMutation?.Invoke(tx);
            fault?.Invoke("event_before_commit");
            tx.Commit();
            fault?.Invoke("event_after_commit");
            return new(id, sequence, false, at);
        }
    }

    // Policy changes are initiated by Host administrative code. Repository policies are never loaded.
    public void ChangePolicies(Policy[] policies, string hostActor)
    {
        if (string.IsNullOrWhiteSpace(hostActor)) throw new InvalidDataException("Host actor is required");
        foreach (var p in policies) {
            ValidateId(p.RepositoryId); GuardPath(p.CanonicalRepoRoot);
            if (p.Mode is not ("auto" or "manual" or "deny")) throw new InvalidDataException("Unknown policy mode");
            if (p.ExportScope is null || p.WriteScope is null) throw new InvalidDataException("Distinct scopes required");
        }
        if (policies.Select(p => p.RepositoryId).Distinct().Count() != policies.Length)
            throw new InvalidDataException("Duplicate repository policy");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(policies);
        var hash = Hash(bytes);
        lock (gate)
        {
            if (activeExportLeases != 0) throw new IOException("Policy change waits for active Host dispatch");
            var path = Path.Combine(Root, "codex-egress-policies.json"); GuardPath(path);
            var oldHash = File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null;
            using (var tx = db.BeginTransaction()) {
                Execute("""
                    INSERT INTO egress_policy_audit(policy_change_id,old_hash,new_hash,changed_by,changed_at,policy_json)
                    VALUES($id,$old,$new,$actor,$at,$json);
                    """, tx, ("$id", Guid.NewGuid().ToString("N")), ("$old", oldHash), ("$new", hash),
                    ("$actor", hostActor), ("$at", DateTimeOffset.UtcNow.ToString("O")),
                    ("$json", Encoding.UTF8.GetString(bytes)));
                tx.Commit();
            }
            // Audit first: a crash or unaudited edit produces a hash mismatch and disables auto.
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
                File.Move(tmp, path, overwrite: true);
            } finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
    }

    public PolicyStatus ReadPolicy(string repositoryId, string canonicalRoot)
    {
        lock (gate)
        {
            var path = Path.Combine(Root, "codex-egress-policies.json");
            GuardPath(path);
            if (!File.Exists(path) || new FileInfo(path).Length > 128 * 1024) return new("deny", null, false);
            var bytes = File.ReadAllBytes(path);
            var hash = Hash(bytes);
            var accepted = Convert.ToString(Scalar("SELECT new_hash FROM egress_policy_audit ORDER BY audit_sequence DESC LIMIT 1;"));
            if (hash != accepted) return new("deny", hash, false);
            try {
                var p = JsonSerializer.Deserialize<Policy[]>(bytes)?.SingleOrDefault(p =>
                    p.RepositoryId == repositoryId &&
                    string.Equals(Path.GetFullPath(p.CanonicalRepoRoot), Path.GetFullPath(canonicalRoot),
                        StringComparison.OrdinalIgnoreCase));
                return new(p?.Mode ?? "deny", hash, true);
            } catch (Exception e) when (e is JsonException or InvalidOperationException) { return new("deny", hash, false); }
        }
    }

    internal sealed record TaskSpec(string TaskId, string AssignmentId, string WorkerId, string WorkspaceId,
        string RepositoryId, string BaseCommit, string StartingStateHash, string StartingDiffHash,
        string[] WriteScope, string[] ReadScope, string Goal, string[] AcceptanceCriteria,
        RequiredTest[] RequiredTests, string[] Dependencies, int Attempt);
    internal sealed record WorkerResult(string TaskId, string AssignmentId, string WorkerId,
        int Generation, string BaseCommit, string StartingStateHash, string StartingDiffHash,
        string ResultDiffHash, string? ResultCommit, string[] ChangedFiles, string[] EvidenceRefs, string Summary);
    internal sealed record AssignmentReceipt(string AssignmentId, int Generation, string TaskSpecHash);
    public void RegisterWorker(string workerId, string[] capabilities, int maxParallelTasks = 1)
    {
        ValidateId(workerId);
        if (maxParallelTasks != 1) throw new InvalidDataException("v1 foundation serializes worker execution");
        lock (gate) Execute("""
            INSERT INTO workers VALUES($id,'OFFLINE',$caps,$max)
            ON CONFLICT(worker_id) DO UPDATE SET capabilities_json=$caps,max_parallel_tasks=$max;
            """, null, ("$id", workerId), ("$caps", JsonSerializer.Serialize(capabilities)), ("$max", maxParallelTasks));
    }
    public AssignmentReceipt ReserveAssignment(TaskSpec spec)
    {
        ValidateId(spec.AssignmentId); ValidateId(spec.WorkerId);
        ValidateHash(spec.StartingStateHash); ValidateHash(spec.StartingDiffHash);
        if (string.IsNullOrWhiteSpace(spec.Goal) || spec.Attempt < 1 ||
            spec.WriteScope.Length == 0 || spec.RequiredTests.Length == 0)
            throw new InvalidDataException("Incomplete immutable TaskSpec");
        var json = JsonSerializer.Serialize(spec); var hash = Hash(Encoding.UTF8.GetBytes(json));
        lock (gate) {
            using var tx = db.BeginTransaction();
            var role = Scalar("SELECT role FROM workspaces WHERE workspace_id=$ws AND repository_id=$repo;",
                tx, ("$ws", spec.WorkspaceId), ("$repo", spec.RepositoryId)) as string;
            if (role is not ("local_worker" or "remote_worker" or "codex_rescue"))
                throw new InvalidDataException("Assignment requires isolated worker workspace");
            if (Scalar("SELECT task_id FROM tasks WHERE task_id=$task;",tx,("$task",spec.TaskId)) is null ||
                Scalar("SELECT worker_id FROM workers WHERE worker_id=$worker;",tx,("$worker",spec.WorkerId)) is null)
                throw new InvalidDataException("Assignment task/worker is unknown");
            // Reservation persists a protocol packet only. It never starts ServerPC or grants completion authority.
            var generation = Convert.ToInt32(Scalar("SELECT coalesce(max(generation),0)+1 FROM assignments WHERE task_id=$task;",
                tx, ("$task", spec.TaskId)));
            Execute("UPDATE assignments SET status='STALE' WHERE task_id=$task AND status!='STALE';",tx,("$task",spec.TaskId));
            Execute("""
                INSERT INTO assignments(assignment_id,task_id,worker_id,workspace_id,generation,status,task_spec_hash,task_spec_json)
                VALUES($id,$task,$worker,$ws,$generation,'RESERVED',$hash,$json);
                """,tx,("$id",spec.AssignmentId),("$task",spec.TaskId),("$worker",spec.WorkerId),("$ws",spec.WorkspaceId),
                ("$generation",generation),("$hash",hash),("$json",json));
            foreach(var dependency in spec.Dependencies)
                Execute("INSERT INTO assignment_dependencies VALUES($id,$task);",tx,("$id",spec.AssignmentId),("$task",dependency));
            tx.Commit(); return new(spec.AssignmentId,generation,hash);
        }
    }
    public EventReceipt CollectResult(WorkerResult result)
    {
        lock (gate) {
            using var cmd = Command("""
                SELECT task_spec_json,generation,status,workspace_id,result_hash FROM assignments
                WHERE assignment_id=$id AND task_id=$task AND worker_id=$worker;
                """,null,("$id",result.AssignmentId),("$task",result.TaskId),("$worker",result.WorkerId));
            TaskSpec spec; string workspace; string status; string? oldHash; int generation;
            using(var r=cmd.ExecuteReader()) {
                if(!r.Read()) throw new InvalidDataException("Unknown assignment result");
                spec=JsonSerializer.Deserialize<TaskSpec>(r.GetString(0))!; generation=r.GetInt32(1);
                status=r.GetString(2); workspace=r.GetString(3); oldHash=r.IsDBNull(4)?null:r.GetString(4);
            }
            if(status=="STALE" || result.Generation!=generation || result.BaseCommit!=spec.BaseCommit ||
                result.StartingStateHash!=spec.StartingStateHash || result.StartingDiffHash!=spec.StartingDiffHash ||
                result.ChangedFiles.Any(p=>!spec.WriteScope.Contains(p,StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("Stale or out-of-scope worker result");
            ValidateHash(result.ResultDiffHash);
            var json=JsonSerializer.Serialize(result); var hash=PutEvidence(Encoding.UTF8.GetBytes(json));
            if(oldHash is not null && oldHash!=hash) throw new InvalidDataException("Worker result is immutable");
            var refs=result.EvidenceRefs.Append(hash).ToArray();
            return CommitEvent(new(result.TaskId,workspace,"worker_collector",result.AssignmentId,
                "worker_result_received","worker_claim","unverified",refs,
                JsonSerializer.SerializeToElement(new { result_hash=hash, generation }),
                WorkerId:result.WorkerId),null,0,0,0,0,tx => Execute("""
                    UPDATE assignments SET status='RESULT_READY',result_hash=$hash,result_json=$json
                    WHERE assignment_id=$id;
                    """,tx,("$hash",hash),("$json",json),("$id",result.AssignmentId)));
        }
    }

    public TaskMemorySelection.Candidate[] MemoryCandidates(string taskId,int limit=256)
    {
        if(limit<1 || limit>256)throw new InvalidDataException("Memory candidate limit invalid");
        return MemoryPreviews(MemoryHeaders(taskId,limit,false));
    }

    public TaskMemoryRetrieval.Result RetrieveMemoryCandidates(string taskId,string query,int limit=16)
    {
        if(limit<1 || limit>32)throw new InvalidDataException("Retrieval model candidate limit invalid");
        var headers=MemoryHeaders(taskId,4097,true);
        if(headers.Count>4096)throw new InvalidDataException("Canonical retrieval exceeds explicit4096-event scan budget; use canonical fallback");
        return new(TaskMemoryRetrieval.Select(query,headers,limit,ReadMemoryTexts(headers,true)),headers.Count==0?0:headers.Max(c=>c.Sequence),headers.Count);
    }

    private List<TaskMemorySelection.Candidate> MemoryHeaders(string taskId,int limit,bool sourceOnly)
    {
        var result=new List<TaskMemorySelection.Candidate>();
        lock(gate) {
            if(GetTask(taskId) is null)throw new InvalidDataException("Unknown memory task");
            var rows=new List<(string Id,long Sequence,string Type,string Status,string Payload)>();
            var sourceFilter=sourceOnly?" AND event_type NOT GLOB 'taskmemory_*' AND event_type <> 'task_checkpoint'":"";
            using(var cmd=Command("SELECT event_id,sequence,event_type,verification_status,json_extract(payload_json,'$.input.Metadata') FROM events WHERE task_id=$task"+sourceFilter+" ORDER BY sequence DESC LIMIT $limit;",null,("$task",taskId),("$limit",limit)))
            using(var r=cmd.ExecuteReader())while(r.Read())rows.Add((r.GetString(0),r.GetInt64(1),r.GetString(2),r.GetString(3),r.GetString(4)));
            foreach(var row in rows.OrderBy(r=>r.Sequence)) {
                var refs=new List<string>();
                using(var cmd=Command("SELECT hash FROM event_evidence WHERE event_id=$id ORDER BY hash;",null,("$id",row.Id)))
                using(var r=cmd.ExecuteReader())while(r.Read())refs.Add(r.GetString(0));
                var text=row.Payload.Length<=256?row.Payload:row.Payload[..255]+"…";
                result.Add(new(taskId,row.Id,row.Sequence,row.Type,row.Status,refs.ToArray(),text));
            }
        }
        return result;
    }

    private Dictionary<string,string> ReadMemoryTexts(List<TaskMemorySelection.Candidate> result,bool fullText)
    {
        // Immutable IDs/refs are snapshotted under the sequencer gate. Hash/preview I/O is outside it.
        var previews=new Dictionary<string,string>(StringComparer.Ordinal);long scanned=0;
        foreach(var hash in result.SelectMany(c=>c.EvidenceRefs).Distinct(StringComparer.Ordinal)) {
            var path=EvidencePath(hash);GuardPath(path);
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            scanned=checked(scanned+stream.Length);
            if(scanned>64*1024*1024)throw new InvalidDataException("Memory seed integrity scan exceeds Host budget");
            if(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()!=hash)throw new InvalidDataException("Memory evidence hash mismatch");
            stream.Position=0;
            using var reader=new StreamReader(stream,new UTF8Encoding(false,true),false,4096,leaveOpen:true);
            try {
                if(fullText){previews[hash]=reader.ReadToEnd();continue;}
                var chars=new char[1792];var count=reader.ReadBlock(chars,0,chars.Length);
                if(count>0 && char.IsHighSurrogate(chars[count-1]))count--;
                previews[hash]=new string(chars,0,count);
            } catch(DecoderFallbackException) {previews[hash]="Host-verified non-text evidence; refer to its content hash.";}
        }
        return previews;
    }
    private TaskMemorySelection.Candidate[] MemoryPreviews(List<TaskMemorySelection.Candidate> result)
    {
        var previews=ReadMemoryTexts(result,false);
        return result.Select(c=>{
            var text=c.Preview+"\nUntrusted immutable evidence excerpt:\n"+string.Join("\n",c.EvidenceRefs.Select(h=>previews[h]));
            var length=Math.Min(2048,text.Length);
            if(length>0 && char.IsHighSurrogate(text[length-1]))length--;
            return c with {Preview=text[..length]};
        }).ToArray();
    }

    public string[] TaskEvidence(string taskId)
    {
        lock(gate) {
            using var cmd=Command("SELECT x.hash FROM event_evidence x JOIN events e ON e.event_id=x.event_id WHERE e.task_id=$task GROUP BY x.hash ORDER BY MAX(e.sequence),x.hash;",null,("$task",taskId));
            using var reader=cmd.ExecuteReader();var result=new List<string>();
            while(reader.Read())result.Add(reader.GetString(0));return result.ToArray();
        }
    }
    public string Recall(string taskId,string hash,int offset)
    {
        lock(gate) {
            if(offset<0 || !TaskEvidence(taskId).Contains(hash,StringComparer.Ordinal))
                throw new UnauthorizedAccessException("Evidence is not attached to this task");
            VerifyEvidenceFile(hash);
            var text=new UTF8Encoding(false,true).GetString(File.ReadAllBytes(EvidencePath(hash)));
            if(offset>text.Length)throw new InvalidDataException("Recall offset exceeds evidence");
            var length=Math.Min(4096,text.Length-offset);
            if(length>0 && char.IsHighSurrogate(text[offset+length-1]))length--;
            return text.Substring(offset,length);
        }
    }
    public void BindApproval(string approvalId,string taskId,string requestHash,string head,DateTimeOffset expiresAt)
    {
        lock(gate) {
            var task=GetTask(taskId)??throw new InvalidDataException("Unknown approval task");
            if(task.RequestHash!=requestHash || expiresAt<=DateTimeOffset.UtcNow || expiresAt>DateTimeOffset.UtcNow.AddMinutes(30))
                throw new UnauthorizedAccessException("Approval does not match the task");
            using var tx=db.BeginTransaction();
            Execute("INSERT INTO task_approvals(approval_id,task_id,request_hash,head,expires_at,consumed_at) VALUES($id,$task,$hash,$head,$expiry,$now);",tx,
                ("$id",approvalId),("$task",taskId),("$hash",requestHash),("$head",head),("$expiry",expiresAt.ToString("O")),("$now",DateTimeOffset.UtcNow.ToString("O")));
            tx.Commit();
        }
    }
    public bool HasApproval(string taskId,string requestHash,string head)
    {
        lock(gate) {
            var expiry=Scalar("SELECT expires_at FROM task_approvals WHERE task_id=$task AND request_hash=$hash AND head=$head ORDER BY expires_at DESC LIMIT 1;",null,
                ("$task",taskId),("$hash",requestHash),("$head",head)) as string;
            return DateTimeOffset.TryParse(expiry,out var time) && time>DateTimeOffset.UtcNow;
        }
    }
    public string[] EventIds(string taskId)
    {
        lock (gate) {
            using var cmd = Command("SELECT event_id FROM events WHERE task_id=$task ORDER BY sequence;",
                null, ("$task", taskId));
            using var r = cmd.ExecuteReader(); var result = new List<string>();
            while (r.Read()) result.Add(r.GetString(0)); return result.ToArray();
        }
    }

    private string EvidencePath(string hash) { ValidateHash(hash); return Path.Combine(Root, "evidence", hash + ".blob"); }
    private void VerifyEvidenceFile(string hash)
    {
        var path = EvidencePath(hash); GuardPath(path);
        if (!File.Exists(path)) throw new InvalidDataException("Referenced evidence is missing");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() != hash)
            throw new InvalidDataException("Referenced evidence hash mismatch");
    }
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static bool Within(string root, string target) {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(target));
        return relative == "." || !Path.IsPathRooted(relative) &&
            relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    internal static void GuardPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current)) {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("Linked control-plane paths are forbidden");
        }
    }
    private static void ValidateId(string id) {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new InvalidDataException("Invalid Host identity");
    }
    private static void ValidateHash(string hash) {
        if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)) || hash != hash.ToLowerInvariant())
            throw new InvalidDataException("Invalid evidence hash");
    }
    private SqliteCommand Command(string sql, SqliteTransaction? tx = null, params (string, object?)[] args) {
        var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.Transaction = tx;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    private void Execute(string sql, SqliteTransaction? tx = null, params (string, object?)[] args) {
        using var cmd = Command(sql, tx, args); cmd.ExecuteNonQuery();
    }
    private object? Scalar(string sql, SqliteTransaction? tx = null, params (string, object?)[] args) {
        using var cmd = Command(sql, tx, args); var result = cmd.ExecuteScalar(); return result is DBNull ? null : result;
    }
    public void Dispose() { db.Dispose(); writerLease.Dispose(); }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS workspaces(
          workspace_id TEXT PRIMARY KEY,repository_id TEXT NOT NULL,role TEXT NOT NULL,identity_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS tasks(
          task_id TEXT PRIMARY KEY,workspace_id TEXT NOT NULL REFERENCES workspaces,request_hash TEXT NOT NULL,
          request_json TEXT NOT NULL,state_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS task_counters(
          task_id TEXT PRIMARY KEY REFERENCES tasks,actor_turns INTEGER NOT NULL DEFAULT 0,
          tool_calls INTEGER NOT NULL DEFAULT 0,handoffs INTEGER NOT NULL DEFAULT 0,external_reviews INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS task_approvals(
          approval_id TEXT PRIMARY KEY,task_id TEXT NOT NULL REFERENCES tasks,request_hash TEXT NOT NULL,
          head TEXT NOT NULL,expires_at TEXT NOT NULL,consumed_at TEXT);
        CREATE TABLE IF NOT EXISTS evidence(hash TEXT PRIMARY KEY,byte_length INTEGER NOT NULL CHECK(byte_length>=0));
        CREATE TABLE IF NOT EXISTS events(
          event_id TEXT PRIMARY KEY,task_id TEXT NOT NULL REFERENCES tasks,workspace_id TEXT NOT NULL REFERENCES workspaces,
          producer TEXT NOT NULL,idempotency_key TEXT NOT NULL,sequence INTEGER NOT NULL CHECK(sequence>0),
          event_type TEXT NOT NULL,evidence_class TEXT NOT NULL,verification_status TEXT NOT NULL,
          producer_event_id TEXT,correlation_id TEXT,caused_by_event_id TEXT REFERENCES events,
          worker_id TEXT,payload_hash TEXT NOT NULL,payload_json TEXT NOT NULL,committed_at TEXT NOT NULL,
          UNIQUE(task_id,sequence),UNIQUE(task_id,producer,idempotency_key));
        CREATE TABLE IF NOT EXISTS event_evidence(event_id TEXT REFERENCES events,hash TEXT REFERENCES evidence,PRIMARY KEY(event_id,hash));
        CREATE TABLE IF NOT EXISTS prompt_ledger(
          prompt_id TEXT PRIMARY KEY,task_id TEXT REFERENCES tasks,event_id TEXT REFERENCES events,payload_hash TEXT NOT NULL,
          source_refs_json TEXT NOT NULL,payload_tokens INTEGER);
        CREATE TABLE IF NOT EXISTS workers(
          worker_id TEXT PRIMARY KEY,status TEXT NOT NULL,capabilities_json TEXT NOT NULL,max_parallel_tasks INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS worker_heartbeats(
          worker_id TEXT REFERENCES workers,received_at TEXT NOT NULL,metadata_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS assignments(
          assignment_id TEXT PRIMARY KEY,task_id TEXT REFERENCES tasks,worker_id TEXT REFERENCES workers,
          workspace_id TEXT REFERENCES workspaces,generation INTEGER NOT NULL,status TEXT NOT NULL,
          task_spec_hash TEXT NOT NULL,task_spec_json TEXT NOT NULL,result_hash TEXT,result_json TEXT);
        CREATE TABLE IF NOT EXISTS assignment_dependencies(
          assignment_id TEXT REFERENCES assignments,depends_on_task_id TEXT REFERENCES tasks,PRIMARY KEY(assignment_id,depends_on_task_id));
        CREATE TABLE IF NOT EXISTS external_exports(
          export_id TEXT PRIMARY KEY,task_id TEXT REFERENCES tasks,policy_hash TEXT NOT NULL,manifest_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS egress_policy_audit(
          audit_sequence INTEGER PRIMARY KEY AUTOINCREMENT,policy_change_id TEXT UNIQUE NOT NULL,old_hash TEXT,new_hash TEXT NOT NULL,
          changed_by TEXT NOT NULL,changed_at TEXT NOT NULL,policy_json TEXT NOT NULL);
        CREATE TRIGGER IF NOT EXISTS events_no_update BEFORE UPDATE ON events BEGIN SELECT RAISE(ABORT,'immutable event'); END;
        CREATE TRIGGER IF NOT EXISTS events_no_delete BEFORE DELETE ON events BEGIN SELECT RAISE(ABORT,'immutable event'); END;
        CREATE TRIGGER IF NOT EXISTS evidence_no_update BEFORE UPDATE ON evidence BEGIN SELECT RAISE(ABORT,'immutable evidence'); END;
        CREATE TRIGGER IF NOT EXISTS evidence_no_delete BEFORE DELETE ON evidence BEGIN SELECT RAISE(ABORT,'retained evidence'); END;
        CREATE TRIGGER IF NOT EXISTS event_evidence_no_update BEFORE UPDATE ON event_evidence BEGIN SELECT RAISE(ABORT,'immutable references'); END;
        CREATE TRIGGER IF NOT EXISTS event_evidence_no_delete BEFORE DELETE ON event_evidence BEGIN SELECT RAISE(ABORT,'retained references'); END;
        CREATE TRIGGER IF NOT EXISTS policy_no_update BEFORE UPDATE ON egress_policy_audit BEGIN SELECT RAISE(ABORT,'immutable audit'); END;
        CREATE TRIGGER IF NOT EXISTS policy_no_delete BEFORE DELETE ON egress_policy_audit BEGIN SELECT RAISE(ABORT,'immutable audit'); END;
        """;
}
