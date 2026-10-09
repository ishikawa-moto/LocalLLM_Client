using System.Text;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

internal sealed partial class CanonicalStore
{
    internal sealed record StoredEvent(string EventId, long Sequence, EventInput Input);
    internal sealed record AcceptedPolicy(Policy Policy, string Hash);
    private int activeExportLeases;
    public Workspace GetWorkspace(string workspaceId) {
        lock(gate) return JsonSerializer.Deserialize<Workspace>(Convert.ToString(Scalar(
            "SELECT identity_json FROM workspaces WHERE workspace_id=$id;",null,("$id",workspaceId)))
            ?? throw new InvalidDataException("Unknown workspace"))!;
    }
    public string TaskRequest(string taskId) {
        lock(gate) return Convert.ToString(Scalar("SELECT request_json FROM tasks WHERE task_id=$id;",null,("$id",taskId)))
            ?? throw new InvalidDataException("Unknown task request");
    }
    public StoredEvent ReadTaskEvent(string taskId,string eventId) {
        lock(gate) {
            using var cmd=Command("SELECT sequence,payload_json,payload_hash FROM events WHERE task_id=$task AND event_id=$event;",null,("$task",taskId),("$event",eventId));
            using var reader=cmd.ExecuteReader();
            if(!reader.Read())throw new UnauthorizedAccessException("Event is not attached to this task");
            var json=reader.GetString(1);
            if(Hash(Encoding.UTF8.GetBytes(json))!=reader.GetString(2))throw new InvalidDataException("Canonical event integrity failed");
            return new(eventId,reader.GetInt64(0),JsonDocument.Parse(json).RootElement.GetProperty("input").Deserialize<EventInput>()!);
        }
    }
    public AcceptedPolicy RequireExportPolicy(string taskId) {
        lock(gate) {
            var task=GetTask(taskId)??throw new InvalidDataException("Unknown export task");
            var workspace=GetWorkspace(task.WorkspaceId);
            var status=ReadPolicy(workspace.RepositoryId,workspace.CanonicalRepoRoot);
            if(!status.AuditMatched || status.Mode=="deny" || status.Hash is null)
                throw new UnauthorizedAccessException("Host export policy denies this repository");
            var bytes=File.ReadAllBytes(Path.Combine(Root,"codex-egress-policies.json"));
            if(Hash(bytes)!=status.Hash)throw new UnauthorizedAccessException("Policy changed while loading");
            var policy=JsonSerializer.Deserialize<Policy[]>(bytes)!.Single(p=>p.RepositoryId==workspace.RepositoryId &&
                string.Equals(Path.GetFullPath(p.CanonicalRepoRoot),Path.GetFullPath(workspace.CanonicalRepoRoot),StringComparison.OrdinalIgnoreCase));
            return new(policy,status.Hash);
        }
    }
    private sealed class ExportLease(CanonicalStore owner,FileStream file):IDisposable {
        public void Dispose(){lock(owner.gate){file.Dispose();owner.activeExportLeases--;}}
    }
    public IDisposable AcquireExportLease(string taskId,string expectedPolicyHash) {
        lock(gate) {
            if(RequireExportPolicy(taskId).Hash!=expectedPolicyHash)throw new UnauthorizedAccessException("Stale export policy");
            var path=Path.Combine(Root,"codex-egress-policies.json");GuardPath(path);
            var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            try {
                if(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)).ToLowerInvariant()!=expectedPolicyHash)
                    throw new UnauthorizedAccessException("Export policy bytes changed");
                activeExportLeases++;return new ExportLease(this,file);
            } catch {file.Dispose();throw;}
        }
    }
    public void BindExportApproval(string taskId,string manifestHash,string policyHash,DateTimeOffset expiresAt,string humanActor) {
        ValidateHash(manifestHash);ValidateHash(policyHash);
        if(string.IsNullOrWhiteSpace(humanActor) || expiresAt<=DateTimeOffset.UtcNow || expiresAt>DateTimeOffset.UtcNow.AddMinutes(30))
            throw new UnauthorizedAccessException("Human export approval is invalid or expired");
        lock(gate) {
            if(GetTask(taskId) is null || RequireExportPolicy(taskId).Hash!=policyHash)throw new UnauthorizedAccessException("Approval policy/task drift");
            var export=Convert.ToString(Scalar("SELECT manifest_json FROM external_exports WHERE task_id=$task AND policy_hash=$policy AND export_id IN (SELECT export_id FROM export_manifest_binding WHERE manifest_hash=$hash);",null,("$task",taskId),("$policy",policyHash),("$hash",manifestHash)));
            if(string.IsNullOrEmpty(export))throw new UnauthorizedAccessException("Unknown prepared export manifest");
            Execute("INSERT INTO export_approvals VALUES($id,$task,$manifest,$policy,$until,$actor,NULL);",null,
                ("$id",Guid.NewGuid().ToString("N")),("$task",taskId),("$manifest",manifestHash),("$policy",policyHash),("$until",expiresAt.ToUniversalTime().ToString("O")),("$actor",humanActor));
        }
    }
    public HostEgress.Prepared? PendingPacket(string taskId,string policyHash,string provider,string originalHash,string[] sourceIds,string[] files) {
        lock(gate) {
            var candidates=new List<(string Json,string Hash)>();
            using(var cmd=Command("SELECT e.manifest_json,b.manifest_hash FROM external_exports e JOIN export_manifest_binding b ON b.export_id=e.export_id WHERE e.task_id=$task AND e.policy_hash=$policy AND NOT EXISTS(SELECT 1 FROM events x WHERE x.task_id=e.task_id AND x.producer='windows_host' AND x.idempotency_key=e.export_id||'_dispatch') ORDER BY e.rowid DESC LIMIT 128;",null,("$task",taskId),("$policy",policyHash)))
            using(var r=cmd.ExecuteReader())while(r.Read())candidates.Add((r.GetString(0),r.GetString(1)));
            foreach(var candidate in candidates) {
                var manifest=JsonSerializer.Deserialize<HostEgress.Manifest>(candidate.Json)!;
                if(Hash(Encoding.UTF8.GetBytes(candidate.Json))!=candidate.Hash)throw new InvalidDataException("Pending export manifest drift");
                if(manifest.ExportedEventIds.Length!=1 || !manifest.ExportedSourceIds.SequenceEqual(sourceIds,StringComparer.Ordinal) ||
                    !manifest.ExportedFileRefs.Select(f=>f.Path).SequenceEqual(files,StringComparer.OrdinalIgnoreCase))continue;
                var row=ReadTaskEvent(taskId,manifest.ExportedEventIds[0]);
                if(row.Input.EventType!="host_approved_export_input" || row.Input.Producer!="windows_host" || !row.Input.EvidenceRefs.SequenceEqual(new[]{originalHash},StringComparer.Ordinal) ||
                    !row.Input.Metadata.TryGetProperty("provider",out var name) || name.GetString()!=provider)continue;
                return new(manifest,candidate.Hash,new UTF8Encoding(false,true).GetString(ReadEvidence(manifest.ExportHash)));
            }
            return null;
        }
    }
    public void SaveExport(HostEgress.Manifest manifest,string manifestHash,string payloadHash) {
        var json=JsonSerializer.Serialize(manifest);
        if(Hash(Encoding.UTF8.GetBytes(json))!=manifestHash)throw new InvalidDataException("Manifest hash mismatch");
        var task=GetTask(manifest.TaskId)??throw new InvalidDataException("Unknown export task");
        CommitEvent(new(manifest.TaskId,task.WorkspaceId,"windows_host",manifest.ExportId,"external_export_prepared","export_audit","host_verified",[manifestHash,payloadHash],JsonSerializer.SerializeToElement(new {manifest_hash=manifestHash,export_hash=payloadHash})),null,0,0,0,0,tx=> {
            Execute("INSERT INTO external_exports VALUES($id,$task,$policy,$json);",tx,("$id",manifest.ExportId),("$task",manifest.TaskId),("$policy",manifest.PolicyHash),("$json",json));
            Execute("INSERT INTO export_manifest_binding VALUES($id,$hash,$payload);",tx,("$id",manifest.ExportId),("$hash",manifestHash),("$payload",payloadHash));
        });
    }
    public void AuditDispatch(HostEgress.Manifest manifest,string manifestHash,string provider) {
        var task=GetTask(manifest.TaskId)??throw new InvalidDataException("Unknown dispatch task");
        var receipt=CommitEvent(new(manifest.TaskId,task.WorkspaceId,"windows_host",manifest.ExportId+"_dispatch","external_dispatch_authorized","export_audit","host_verified",[manifestHash,manifest.ExportHash],JsonSerializer.SerializeToElement(new {provider,export_id=manifest.ExportId,manifest_hash=manifestHash})),null,0,0,0,0,tx=> {
            var persisted=Convert.ToString(Scalar("SELECT manifest_json FROM external_exports WHERE export_id=$id AND task_id=$task AND policy_hash=$policy;",tx,("$id",manifest.ExportId),("$task",manifest.TaskId),("$policy",manifest.PolicyHash)));
            if(persisted!=JsonSerializer.Serialize(manifest))throw new UnauthorizedAccessException("Unregistered or changed export manifest");
            if(manifest.PolicyMode=="manual") {
                var approval=Convert.ToString(Scalar("SELECT approval_id FROM export_approvals WHERE task_id=$task AND manifest_hash=$manifest AND policy_hash=$policy AND consumed_at IS NULL AND expires_at>$now ORDER BY expires_at DESC LIMIT 1;",tx,("$task",manifest.TaskId),("$manifest",manifestHash),("$policy",manifest.PolicyHash),("$now",DateTimeOffset.UtcNow.ToString("O"))));
                if(string.IsNullOrEmpty(approval))throw new UnauthorizedAccessException("Exact human export approval required");
                Execute("UPDATE export_approvals SET consumed_at=$now WHERE approval_id=$id;",tx,("$now",DateTimeOffset.UtcNow.ToString("O")),("$id",approval));
            }
        });
        if(receipt.Duplicate)throw new UnauthorizedAccessException("Export dispatch cannot be replayed");
    }
    internal void RequireCurrentAssignment(string taskId,string assignmentId,int generation,string workspaceId) {
        lock(gate)if(Convert.ToInt32(Scalar("SELECT count(*) FROM assignments WHERE task_id=$task AND assignment_id=$id AND generation=$generation AND workspace_id=$ws AND status!='STALE';",null,("$task",taskId),("$id",assignmentId),("$generation",generation),("$ws",workspaceId)))!=1)
            throw new UnauthorizedAccessException("Rescue assignment is stale or unrelated");
    }
    internal Workspace FindRescueWorkspace(string root,string repositoryId) {
        lock(gate) {
            var matches=new List<Workspace>();
            using var cmd=Command("SELECT identity_json FROM workspaces WHERE repository_id=$repo AND role='codex_rescue';",null,("$repo",repositoryId));using var reader=cmd.ExecuteReader();
            while(reader.Read()){var w=JsonSerializer.Deserialize<Workspace>(reader.GetString(0))!;if(string.Equals(Path.GetFullPath(w.WorkspaceRoot),Path.GetFullPath(root),StringComparison.OrdinalIgnoreCase))matches.Add(w);}
            return matches.SingleOrDefault()??throw new UnauthorizedAccessException("Root is not this task's registered rescue");
        }
    }
    private const string EgressSchema="""
        CREATE TABLE IF NOT EXISTS export_manifest_binding(export_id TEXT PRIMARY KEY REFERENCES external_exports,manifest_hash TEXT UNIQUE NOT NULL,payload_hash TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS export_approvals(approval_id TEXT PRIMARY KEY,task_id TEXT REFERENCES tasks,manifest_hash TEXT NOT NULL,policy_hash TEXT NOT NULL,expires_at TEXT NOT NULL,human_actor TEXT NOT NULL,consumed_at TEXT);
        CREATE TRIGGER IF NOT EXISTS external_exports_no_update BEFORE UPDATE ON external_exports BEGIN SELECT RAISE(ABORT,'immutable export'); END;
        CREATE TRIGGER IF NOT EXISTS external_exports_no_delete BEFORE DELETE ON external_exports BEGIN SELECT RAISE(ABORT,'retained export'); END;
        CREATE TRIGGER IF NOT EXISTS export_binding_no_update BEFORE UPDATE ON export_manifest_binding BEGIN SELECT RAISE(ABORT,'immutable manifest'); END;
        CREATE TRIGGER IF NOT EXISTS export_binding_no_delete BEFORE DELETE ON export_manifest_binding BEGIN SELECT RAISE(ABORT,'retained manifest'); END;
        """;
}