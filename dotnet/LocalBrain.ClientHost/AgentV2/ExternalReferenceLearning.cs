using System.Text;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

// Administrative local candidate assembly. No transport, inference, policy grant or adoption.
internal static class ExternalReferenceLearning
{
    internal const int MaxSourceBytes=16_000,MaxManifestBytes=100_000;
    internal sealed record Input(int Version,string RequestId,string Title,string Project,string[] ObservationEventIds);
    internal sealed record Source(string source_event_id,long sequence,string original_source_id,string repository_path,
        string repository_hash,string head,string diff_hash,string filename,string content,string content_sha256);
    internal sealed record Manifest(int schema_version,string task_id,string workspace_id,string request_id,string title,
        string project,string body,Source[] sources,string[] constituent_source_ids,string[] evidence_hashes,
        string verification_scope,bool original_document_claims_verified=false,bool knowledge_adopted=false,
        bool reviewer_required=true,string adoption_status="LOCAL_CANDIDATE_NOT_DISPATCHED");
    internal sealed record Result(string EventId,string ManifestHash,int SourceCount,bool Duplicate,bool KnowledgeAdopted=false);

    internal static Input Parse(string json)
    {
        var value=DecisionProtocol.Parse(json);
        DecisionProtocol.Keys(value,"version","request_id","title","project","observation_event_ids");
        if(value.GetProperty("version").GetInt32()!=1)throw new InvalidDataException("Unknown reference-learning version");
        var requestId=DecisionProtocol.Id(DecisionProtocol.Text(value,"request_id",80));
        var title=DecisionProtocol.Text(value,"title",200);if(title.IndexOfAny(['\r','\n'])>=0)throw new InvalidDataException("Title must be one line");var project=DecisionProtocol.Text(value,"project",80);
        if(!System.Text.RegularExpressions.Regex.IsMatch(project,"\\A[A-Za-z0-9_.-]{1,80}\\z"))
            throw new InvalidDataException("Invalid project");
        var refs=value.GetProperty("observation_event_ids");
        if(refs.ValueKind!=JsonValueKind.Array||refs.GetArrayLength() is <1 or >4)
            throw new InvalidDataException("Reference learning needs one to four explicit observations");
        var ids=refs.EnumerateArray().Select(v=>v.ValueKind==JsonValueKind.String?v.GetString()!:throw new InvalidDataException("Observation identity must be text")).ToArray();
        if(ids.Any(id=>id.Length is <1 or >100||id.Contains('\0'))||ids.Distinct(StringComparer.Ordinal).Count()!=ids.Length)
            throw new InvalidDataException("Duplicate or invalid observation identity");
        if(HostEgress.Redact(json).Count!=0)throw new UnauthorizedAccessException("Sensitive reference-learning input");
        return new(1,requestId,title,project,ids);
    }

    internal static async Task<Result> PrepareAsync(CanonicalStore store,string root,Input input)
    {
        input=Parse(JsonSerializer.Serialize(new{version=input.Version,request_id=input.RequestId,title=input.Title,
            project=input.Project,observation_event_ids=input.ObservationEventIds}));
        root=Path.GetFullPath(root);CanonicalStore.GuardPath(root);
        var workspaceId=HostTaskJournal.WorkspaceId(root);
        var owner=store.ResolveExternalObservationOwner(workspaceId,input.ObservationEventIds[0]);
        if(!string.Equals(Path.GetFullPath(store.GetWorkspace(owner.WorkspaceId).WorkspaceRoot),root,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Canonical observation workspace differs");
        var sources=new List<Source>();var refs=new HashSet<string>(StringComparer.Ordinal);
        var eventIds=new List<string>();
        foreach(var id in input.ObservationEventIds)
        {
            var task=store.ResolveExternalObservationOwner(workspaceId,id);
            if(task.TaskId!=owner.TaskId)throw new UnauthorizedAccessException("Observations must belong to one explicit task");
            var row=store.ReadTaskEvent(owner.TaskId,id);var m=row.Input.Metadata;
            if(!m.GetProperty("repository_quote_found").GetBoolean()||m.GetProperty("semantic_claims_verified").GetBoolean()
                ||m.GetProperty("knowledge_adopted").GetBoolean())throw new InvalidDataException("Only supported literal observations are eligible");
            var requestHash=m.GetProperty("request_hash").GetString()!;var repositoryHash=m.GetProperty("repository_hash").GetString()!;
            var diffHash=m.GetProperty("diff_hash").GetString()!;
            foreach(var h in new[]{requestHash,repositoryHash,diffHash})
                if(!row.Input.EvidenceRefs.Contains(h,StringComparer.Ordinal))throw new InvalidDataException("Observation evidence binding missing");
            var request=JsonSerializer.Deserialize<ExternalClaimVerification.Request>(store.ReadEvidence(requestHash))
                ??throw new InvalidDataException("Observation request missing");
            var original=store.ReadTaskEvent(owner.TaskId,request.ReferenceEventId);
            if(original.Input.EventType!="external_reference_imported"||original.Input.Producer!="windows_host"
                ||original.Input.VerificationStatus!="unverified"||m.GetProperty("reference_event_id").GetString()!=request.ReferenceEventId
                ||original.Input.Metadata.GetProperty("source_id").GetString()!=m.GetProperty("original_source_id").GetString())
                throw new InvalidDataException("Original corpus identity differs");
            var relative=HostEgress.NormalizePath(root,request.RepositoryPath);var file=Path.Combine(root,relative);CanonicalStore.GuardPath(file);
            using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(stream.Length is 0 or >ExternalReferenceCorpus.MaxBytes)throw new InvalidDataException("Observation file exceeds budget");
                var bytes=new byte[(int)stream.Length];stream.ReadExactly(bytes);
                if(CanonicalStore.Hash(bytes)!=repositoryHash||request.ExpectedRepositoryHash!=repositoryHash
                    ||!bytes.SequenceEqual(store.ReadEvidence(repositoryHash))
                    ||!DecisionProtocol.Utf8.GetString(bytes).Contains(request.RepositoryQuote,StringComparison.Ordinal))
                    throw new InvalidDataException("Current repository differs from the supported observation");
            }
            var head=Encoding.UTF8.GetString(await AttemptStartingState.GitAsync(root,["rev-parse","HEAD"])).Trim();
            var diff=await AttemptStartingState.GitAsync(root,["diff","HEAD","--binary","--no-ext-diff","--no-textconv","--",relative]);
            if(head!=m.GetProperty("head").GetString()||CanonicalStore.Hash(diff)!=diffHash)
                throw new InvalidDataException("Current repository HEAD/diff differs");
            // This is the whole claim: historical literal containment, not the PDF's semantic assertion.
            var statement="At repository HEAD "+head+", file "+JsonSerializer.Serialize(relative)+
                " contains the exact text "+JsonSerializer.Serialize(request.RepositoryQuote)+".";
            if(HostEgress.Redact(statement).Count!=0)throw new UnauthorizedAccessException("Sensitive observed text");
            var content=statement+"\n";
            if(Encoding.UTF8.GetByteCount(content)>MaxSourceBytes)throw new InvalidDataException("Learning source exceeds receiver UTF8 budget");
            sources.Add(new(id,row.Sequence,m.GetProperty("original_source_id").GetString()!,relative,repositoryHash,head,diffHash,
                "observation-"+CanonicalStore.Hash(Encoding.UTF8.GetBytes(id))[..24]+".md",content,CanonicalStore.Hash(Encoding.UTF8.GetBytes(content))));
            eventIds.Add("event:"+id);
            foreach(var h in row.Input.EvidenceRefs){_=store.ReadEvidence(h);refs.Add(h);}
        }
        // Recheck all selected sources after collection so ordinary HEAD/file changes cannot assemble a mixed candidate.
        foreach(var source in sources)
        {
            var file=Path.Combine(root,source.repository_path);CanonicalStore.GuardPath(file);
            using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(stream.Length is 0 or >ExternalReferenceCorpus.MaxBytes)throw new InvalidDataException("Rechecked observation exceeds budget");
            var bytes=new byte[(int)stream.Length];stream.ReadExactly(bytes);
            var head=Encoding.UTF8.GetString(await AttemptStartingState.GitAsync(root,["rev-parse","HEAD"])).Trim();
            var diff=await AttemptStartingState.GitAsync(root,["diff","HEAD","--binary","--no-ext-diff","--no-textconv","--",source.repository_path]);
            if(CanonicalStore.Hash(bytes)!=source.repository_hash||head!=source.head||CanonicalStore.Hash(diff)!=source.diff_hash)
                throw new InvalidDataException("Repository changed while assembling the learning candidate");
        }
        if(sources.Select(s=>s.repository_path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=sources.Count
            ||sources.Select(s=>s.repository_hash).Distinct(StringComparer.Ordinal).Count()!=sources.Count)
            throw new InvalidDataException("The same file/content cannot supply duplicate corroboration sources");
        var manifest=new Manifest(1,owner.TaskId,workspaceId,input.RequestId,input.Title,input.Project,
            string.Join("\n\n",sources.Select(s=>s.content.TrimEnd('\n'))),sources.ToArray(),
            eventIds.Concat(refs.Order(StringComparer.Ordinal).Select(h=>"blob:"+h)).ToArray(),refs.Order(StringComparer.Ordinal).ToArray(),
            "Historical exact repository quotation at observed HEAD/file hash only; original external claims are not verified");
        var manifestBytes=JsonSerializer.SerializeToUtf8Bytes(manifest);
        if(manifestBytes.Length>MaxManifestBytes)throw new InvalidDataException("Learning manifest exceeds receiver UTF8 budget");
        var hash=CanonicalStore.Hash(manifestBytes);
        var key="reference_learning_"+input.RequestId;
        var old=store.EventIds(owner.TaskId).Select(e=>store.ReadTaskEvent(owner.TaskId,e))
            .SingleOrDefault(e=>e.Input.EventType=="external_reference_learning_candidate_prepared"&&e.Input.IdempotencyKey==key);
        if(old is not null)
        {
            if(!old.Input.EvidenceRefs.Contains(hash,StringComparer.Ordinal))throw new InvalidDataException("Request ID already binds another candidate");
            return new(old.EventId,hash,sources.Count,true);
        }
        var blob=store.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(manifest));
        var receipt=store.Append(new(owner.TaskId,workspaceId,"windows_host",key,"external_reference_learning_candidate_prepared",
            "learning_candidate","host_observed",[blob,..refs.Order(StringComparer.Ordinal)],
            JsonSerializer.SerializeToElement(new{manifest_hash=blob,source_count=sources.Count,original_document_claims_verified=false,
                knowledge_adopted=false,reviewer_required=true,adoption_status="LOCAL_CANDIDATE_NOT_DISPATCHED"}),
            CausedByEventId:input.ObservationEventIds[0]));
        return new(receipt.EventId,blob,sources.Count,receipt.Duplicate);
    }

    internal sealed record PacketResult(string PacketDirectory,string ManifestHash,int SourceCount,bool Duplicate,bool KnowledgeAdopted=false);
    internal static async Task<PacketResult> ExportLocalAsync(CanonicalStore store,string root,string manifestHash)
    {
        manifestHash=DecisionProtocol.HashText(manifestHash);root=Path.GetFullPath(root);CanonicalStore.GuardPath(root);
        var bytes=store.ReadEvidence(manifestHash);if(bytes.Length>MaxManifestBytes)throw new InvalidDataException("Learning manifest exceeds receiver budget");var manifest=JsonSerializer.Deserialize<Manifest>(bytes)
            ??throw new InvalidDataException("Learning candidate missing");
        var task=store.GetTask(manifest.task_id)??throw new UnauthorizedAccessException("Learning task missing");
        if(task.WorkspaceId!=HostTaskJournal.WorkspaceId(root)||manifest.workspace_id!=task.WorkspaceId
            ||!string.Equals(Path.GetFullPath(store.GetWorkspace(task.WorkspaceId).WorkspaceRoot),root,StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Learning packet belongs to another workspace");
        var row=store.EventIds(task.TaskId).Select(id=>store.ReadTaskEvent(task.TaskId,id)).SingleOrDefault(e=>
            e.Input.EventType=="external_reference_learning_candidate_prepared"&&e.Input.Producer=="windows_host"
            &&e.Input.WorkerId is null&&e.Input.ProducerEventId is null&&e.Input.EvidenceRefs.Contains(manifestHash,StringComparer.Ordinal))
            ??throw new UnauthorizedAccessException("Learning packet has no Host preparation event");
        if(manifest.schema_version!=1||manifest.original_document_claims_verified||manifest.knowledge_adopted||!manifest.reviewer_required
            ||manifest.adoption_status!="LOCAL_CANDIDATE_NOT_DISPATCHED"||manifest.sources.Length is <1 or >4)
            throw new InvalidDataException("Learning packet cannot grant truth or adoption");
        foreach(var source in manifest.sources)
        {
            var observation=store.ReadTaskEvent(task.TaskId,source.source_event_id);
            if(observation.Sequence!=source.sequence||observation.Input.EventType!="external_claim_repository_observed"
                ||observation.Input.Producer!="windows_host"||observation.Input.VerificationStatus!="host_observed"
                ||!observation.Input.Metadata.GetProperty("repository_quote_found").GetBoolean())
                throw new InvalidDataException("Learning source event binding differs");
            var file=Path.Combine(root,HostEgress.NormalizePath(root,source.repository_path));CanonicalStore.GuardPath(file);
            using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(stream.Length is 0 or >ExternalReferenceCorpus.MaxBytes)throw new InvalidDataException("Learning source exceeds budget");
            var actual=new byte[(int)stream.Length];stream.ReadExactly(actual);
            var head=Encoding.UTF8.GetString(await AttemptStartingState.GitAsync(root,["rev-parse","HEAD"])).Trim();
            var diff=await AttemptStartingState.GitAsync(root,["diff","HEAD","--binary","--no-ext-diff","--no-textconv","--",source.repository_path]);
            if(CanonicalStore.Hash(actual)!=source.repository_hash||head!=source.head||CanonicalStore.Hash(diff)!=source.diff_hash
                ||CanonicalStore.Hash(Encoding.UTF8.GetBytes(source.content))!=source.content_sha256
                ||source.filename!="observation-"+CanonicalStore.Hash(Encoding.UTF8.GetBytes(source.source_event_id))[..24]+".md")
                throw new InvalidDataException("Current learning source or exact bytes differs");
        }
        var outbox=Path.Combine(store.Root,"knowledge-outbox");CanonicalStore.GuardPath(outbox);Directory.CreateDirectory(outbox);
        var packet=Path.Combine(outbox,manifestHash);CanonicalStore.GuardPath(packet);
        var files=new Dictionary<string,byte[]>(StringComparer.Ordinal){{"learning-manifest.json",bytes}};
        foreach(var source in manifest.sources)files.Add(source.filename,Encoding.UTF8.GetBytes(source.content));
        void VerifyPacket()
        {
            CanonicalStore.GuardPath(packet);
            if(!Directory.GetFiles(packet).Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(files.Keys.Order(StringComparer.Ordinal))
                ||Directory.GetDirectories(packet).Length!=0)throw new InvalidDataException("Packet inventory differs");
            foreach(var pair in files){var p=Path.Combine(packet,pair.Key);CanonicalStore.GuardPath(p);
                if(!File.ReadAllBytes(p).SequenceEqual(pair.Value))throw new InvalidDataException("Existing packet contains changed bytes");}
        }
        var duplicate=Directory.Exists(packet);
        if(duplicate)VerifyPacket();
        else
        {
            var temporary=Path.Combine(outbox,"stage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
            try
            {
                foreach(var pair in files){using var file=new FileStream(Path.Combine(temporary,pair.Key),FileMode.CreateNew,FileAccess.Write,FileShare.None);file.Write(pair.Value);file.Flush(true);}
                if(!CanonicalStore.Within(outbox,temporary)||!CanonicalStore.Within(outbox,packet))throw new UnauthorizedAccessException("Packet move escaped local outbox");
                CanonicalStore.GuardPath(temporary);Directory.Move(temporary,packet);VerifyPacket();
            }
            finally{
                if(Directory.Exists(temporary)){
                    CanonicalStore.GuardPath(temporary);
                    if(!CanonicalStore.Within(outbox,temporary))throw new UnauthorizedAccessException("Packet cleanup escaped local outbox");
                    foreach(var pair in files){var p=Path.Combine(temporary,pair.Key);CanonicalStore.GuardPath(p);
                        if(File.Exists(p)&&File.ReadAllBytes(p).SequenceEqual(pair.Value))File.Delete(p);}
                    if(Directory.GetFileSystemEntries(temporary).Length==0)Directory.Delete(temporary);
                }
            }
        }
        store.Append(new(task.TaskId,task.WorkspaceId,"windows_host","reference_learning_packet_"+manifestHash,
            "external_reference_learning_packet_prepared","learning_candidate","host_observed",[manifestHash],
            JsonSerializer.SerializeToElement(new{manifest_hash=manifestHash,source_count=manifest.sources.Length,
                destination_scope="LOCAL_KNOWLEDGE_OUTBOX_ONLY_NOT_CODEX_EXPORT",knowledge_adopted=false,reviewer_required=true}),
            CausedByEventId:row.EventId));
        return new(packet,manifestHash,manifest.sources.Length,duplicate);
    }

}

internal sealed partial class CanonicalStore
{
    internal TaskSnapshot ResolveExternalObservationOwner(string workspaceId,string eventId)
    {
        lock(gate)
        {
            var owner=Convert.ToString(Scalar("SELECT task_id FROM events WHERE event_id=$id;",null,("$id",eventId)));
            var task=GetTask(owner??"")??throw new UnauthorizedAccessException("Observation owner missing");
            if(task.WorkspaceId!=workspaceId)throw new UnauthorizedAccessException("Observation belongs to another workspace");
            var row=ReadTaskEvent(task.TaskId,eventId);
            if(row.Input.Producer!="windows_host"||row.Input.EventType!="external_claim_repository_observed"
                ||row.Input.VerificationStatus!="host_observed"||row.Input.WorkerId is not null||row.Input.ProducerEventId is not null)throw new UnauthorizedAccessException("Host literal observation required");
            return task;
        }
    }
}






