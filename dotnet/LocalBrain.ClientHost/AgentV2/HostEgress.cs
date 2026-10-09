using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalBrain.ClientHost.AgentV2;

// Host-only authority: never exposed as a model tool or loaded from repository configuration.
internal sealed class HostEgress(CanonicalStore store)
{
    internal sealed record FileRef(string Path,string EvidenceHash);
    internal sealed record Manifest(string ExportId,string TaskId,string RepositoryId,string PolicyId,
        string PolicyHash,string PolicyMode,string[] WriteScope,string[] ExportScope,string[] ExportedEventIds,
        string[] ExportedSourceIds,FileRef[] ExportedFileRefs,int RedactionCount,string[] RedactionClasses,
        string ExportHash,string[] ParityDeviation);
    internal sealed record Prepared(Manifest Manifest,string ManifestHash,string Payload);
    internal sealed record Redacted(string Text,int Count,string[] Classes);
    private static readonly Regex PrivateKey=new(@"-----BEGIN [^-\r\n]*PRIVATE KEY-----[\s\S]*?-----END [^-\r\n]*PRIVATE KEY-----",RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveIdentifiers=new("\\b(?<key>"+ReviewerEvidence.SensitiveKeyPattern+")\\b",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    private static readonly Regex CredentialTokens=new(@"\bBearer\s+\S{12,}|\b(?:sk-[A-Za-z0-9_-]{12,}|gh[pousr]_[A-Za-z0-9]{12,}|AKIA[A-Z0-9]{16})",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    internal static Redacted Redact(string text) {
        if(Encoding.UTF8.GetByteCount(text)>64_000)throw new InvalidDataException("Export fragment exceeds Host budget");
        var count=0;var classes=new HashSet<string>(StringComparer.Ordinal);
        string Apply(Regex regex,string name,string value)=>regex.Replace(value,m=>{count++;classes.Add(name);return "[REDACTED:"+name+"]";});
        string SensitiveKey(string key)=>Regex.IsMatch(key,@"(?i)^(?:[A-Za-z0-9_-]*(?:nonce|capability|host[_-]?token|sidefx[_-]?pipe)|pipe)$")?"host_capability":
            Regex.IsMatch(key,@"(?i)^(?:[A-Za-z0-9_-]*(?:api[_-]?key|access[_-]?token|client[_-]?secret|authorization|password))$")?"credential":"";
        string RedactText(string value,int depth) {
            if(depth>16)throw new UnauthorizedAccessException("Export JSON nesting is too deep");
            var before=count;
            try {
                var node=System.Text.Json.Nodes.JsonNode.Parse(value,documentOptions:new JsonDocumentOptions{MaxDepth=32});
                void Visit(System.Text.Json.Nodes.JsonNode? current,int level) {
                    if(level>32)throw new UnauthorizedAccessException("Export JSON nesting exceeds Host budget");
                    if(current is System.Text.Json.Nodes.JsonObject obj)foreach(var pair in obj.ToArray()) {
                        var kind=SensitiveKey(pair.Key);
                        if(kind!=""){count++;classes.Add(kind);obj[pair.Key]="[REDACTED:"+kind+"]";}
                        else if(pair.Value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var str))obj[pair.Key]=RedactText(str,depth+1);
                        else Visit(pair.Value,level+1);
                    }
                    else if(current is System.Text.Json.Nodes.JsonArray array)for(var i=0;i<array.Count;i++) {
                        if(array[i] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var str))array[i]=RedactText(str,depth+1);
                        else Visit(array[i],level+1);
                    }
                }
                if(node is System.Text.Json.Nodes.JsonValue scalar && scalar.TryGetValue<string>(out var decoded)) {
                    var result=RedactText(decoded,depth+1);return count==before?value:JsonSerializer.Serialize(result);
                }
                Visit(node,0);if(count!=before)return node!.ToJsonString();
            } catch(JsonException) { /* Plain source/output text is handled by the bounded raw redactor. */ }
            var raw=Apply(PrivateKey,"private_key",value);
            var decodedRaw=ReviewerEvidence.DecodeSourceEscapes(raw);
            var assignments=SensitiveIdentifiers.Matches(decodedRaw);
            if(assignments.Count>0) {
                foreach(Match assignment in assignments){count++;classes.Add(SensitiveKey(assignment.Groups["key"].Value));}
                // A diff/source fragment can contain arbitrary language and multiline values.
                // Remove the complete bounded fragment rather than guessing where a value ends.
                classes.Add("opaque_sensitive_fragment_removed");
                return "[REDACTED:sensitive_fragment]";
            }
            raw=Apply(CredentialTokens,"credential",decodedRaw);
            return count==before?value:raw;
        }
        text=RedactText(text,0);
        if(ReviewerEvidence.ContainsSecret(text))throw new UnauthorizedAccessException("Unredacted credential is not exportable");
        return new(text,count,classes.Order(StringComparer.Ordinal).ToArray());
    }
    internal static string NormalizePath(string root,string relative) {
        if(string.IsNullOrWhiteSpace(relative) || relative.Length>260 || Path.IsPathRooted(relative) ||
            relative.Split('/','\\').Any(p=>p is "" or "." or ".." || p.Equals(".git",StringComparison.OrdinalIgnoreCase) || p.Equals(".localbrain",StringComparison.OrdinalIgnoreCase) || p.EndsWith(' ') || p.EndsWith('.') || p.Contains(':')) ||
            Regex.IsMatch(Path.GetFileName(relative),@"^(?:\.env(?:\..*)?|id_(?:rsa|ed25519)|.*\.(?:pem|pfx|p12|key|gguf))$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))
            throw new UnauthorizedAccessException("Export path is not eligible");
        var path=Path.GetFullPath(Path.Combine(root,relative));CanonicalStore.GuardPath(path);
        if(!CanonicalStore.Within(root,path) || string.Equals(root,path,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Export path escaped repository");
        return Path.GetRelativePath(Path.GetFullPath(root),path).Replace('\\','/');
    }
    public Prepared Prepare(string taskId,string[] eventIds,string[] sourceIds,FileRef[] fileRefs,string[] deviations,bool packetOnly=false) {
        if(eventIds.Length is <1 or >64 || eventIds.Distinct(StringComparer.Ordinal).Count()!=eventIds.Length || sourceIds.Length>64 || fileRefs.Length>100)
            throw new InvalidDataException("Invalid explicit export selection");
        var task=store.GetTask(taskId)??throw new InvalidDataException("Unknown export task");
        var workspace=store.GetWorkspace(task.WorkspaceId);CanonicalStore.GuardPath(workspace.CanonicalRepoRoot);CanonicalStore.GuardPath(workspace.WorkspaceRoot);
        var accepted=store.RequireExportPolicy(taskId);var policy=accepted.Policy;
        var rows=eventIds.Select(id=>store.ReadTaskEvent(taskId,id)).OrderBy(e=>e.Sequence).ToArray();
        var eligibleTypes=new[]{"host_prompt","provider_prompt","pi_tool_execution_start","pi_tool_execution_end","host_approved_export_input","validation_result","test_started","test_result","external_review_result","secondbrain_sources"};
        foreach(var row in rows) {
            if(row.Input.WorkspaceId!=task.WorkspaceId || row.Input.Producer!="windows_host" ||
                !(policy.ExportScope.Contains("event_type:"+row.Input.EventType,StringComparer.Ordinal) || policy.ExportScope.Contains("task_evidence_only",StringComparer.Ordinal) && eligibleTypes.Contains(row.Input.EventType,StringComparer.Ordinal)))
                throw new UnauthorizedAccessException("Selected canonical event is outside export_scope");
        }
        var refs=rows.SelectMany(e=>e.Input.EvidenceRefs).ToHashSet(StringComparer.Ordinal);
        if(refs.Count>128)throw new InvalidDataException("Too many exported evidence fragments");
        var approvedSources=rows.SelectMany(e=>e.Input.Metadata.TryGetProperty("approved_source_ids",out var ids)?ids.EnumerateArray().Select(i=>i.GetString()!):[]).ToHashSet(StringComparer.Ordinal);
        if(sourceIds.Any(s=>!approvedSources.Contains(s)))throw new UnauthorizedAccessException("Source was not cited in selected Host evidence");
        var approvedFiles=rows.SelectMany(e=>e.Input.Metadata.TryGetProperty("approved_file_refs",out var files)?files.Deserialize<FileRef[]>()??[]:[])
            .Select(f=>new FileRef(NormalizePath(workspace.WorkspaceRoot,f.Path),f.EvidenceHash)).ToArray();
        foreach(var f in fileRefs) {
            var normalized=NormalizePath(workspace.WorkspaceRoot,f.Path);
            if(!approvedFiles.Any(a=>string.Equals(a.Path,normalized,StringComparison.OrdinalIgnoreCase) && a.EvidenceHash==f.EvidenceHash) || !refs.Contains(f.EvidenceHash) ||
                !(policy.ExportScope.Contains("task_evidence_only",StringComparer.Ordinal) || policy.ExportScope.Any(s=>string.Equals(s,"file:"+normalized,StringComparison.OrdinalIgnoreCase))))
                throw new UnauthorizedAccessException("File reference is outside export_scope");
        }
        var fragments=new List<object>();var texts=new List<string>();var redactionCount=0;var redactionClasses=new HashSet<string>(StringComparer.Ordinal);
        foreach(var row in rows)foreach(var hash in row.Input.EvidenceRefs) {
            var bytes=store.ReadEvidence(hash);var text=new UTF8Encoding(false,true).GetString(bytes);var redacted=Redact(text);
            redactionCount+=redacted.Count;redactionClasses.UnionWith(redacted.Classes);texts.Add(redacted.Text);
            fragments.Add(new{event_id=row.EventId,row.Sequence,event_type=row.Input.EventType,row.Input.EvidenceClass,row.Input.VerificationStatus,original_evidence_hash=hash,exported_text_hash=CanonicalStore.Hash(Encoding.UTF8.GetBytes(redacted.Text)),text=redacted.Text});
        }
        if(packetOnly && (rows.Length!=1 || texts.Count!=1 || rows[0].Input.EventType!="host_approved_export_input"))throw new InvalidDataException("Packet export requires exactly one Host packet");
        var payload=packetOnly?texts.Single():JsonSerializer.Serialize(fragments);
        if(Encoding.UTF8.GetByteCount(payload)>72_000)throw new InvalidDataException("Export exceeds Host total budget");
        var payloadHash=store.PutEvidence(Encoding.UTF8.GetBytes(payload));
        var parity=deviations.Concat(redactionCount>0?new[]{"security_redaction_changed_information_conditions"}:[]).Distinct(StringComparer.Ordinal).ToArray();
        var manifest=new Manifest(Guid.NewGuid().ToString("N"),taskId,workspace.RepositoryId,workspace.RepositoryId,accepted.Hash,policy.Mode,
            policy.WriteScope.ToArray(),policy.ExportScope.ToArray(),rows.Select(e=>e.EventId).ToArray(),sourceIds.ToArray(),fileRefs.Select(f=>f with{Path=NormalizePath(workspace.WorkspaceRoot,f.Path)}).ToArray(),
            redactionCount,redactionClasses.Order(StringComparer.Ordinal).ToArray(),payloadHash,parity);
        var manifestHash=store.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(manifest));store.SaveExport(manifest,manifestHash,payloadHash);
        return new(manifest,manifestHash,payload);
    }
    public IDisposable AuthorizeDispatch(Prepared export,string provider) {
        if(CanonicalStore.Hash(Encoding.UTF8.GetBytes(export.Payload))!=export.Manifest.ExportHash ||
            CanonicalStore.Hash(JsonSerializer.SerializeToUtf8Bytes(export.Manifest))!=export.ManifestHash ||
            !store.ReadEvidence(export.Manifest.ExportHash).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(export.Payload)))
            throw new UnauthorizedAccessException("Export identity or immutable bytes changed");
        var lease=store.AcquireExportLease(export.Manifest.TaskId,export.Manifest.PolicyHash);
        try {store.AuditDispatch(export.Manifest,export.ManifestHash,provider);return lease;}catch{lease.Dispose();throw;}
    }
}