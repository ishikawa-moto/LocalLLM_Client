using System.Text;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

internal sealed partial class CanonicalStore
{
    internal TaskSnapshot ResolveExternalReferenceOwner(string workspaceId,string eventId)
    {
        lock(gate){
            var owner=Convert.ToString(Scalar("SELECT task_id FROM events WHERE event_id=$id;",null,("$id",eventId)));
            var task=GetTask(owner??"")??throw new UnauthorizedAccessException("No canonical reference owner");
            if(task.WorkspaceId!=workspaceId)throw new UnauthorizedAccessException("Reference belongs to another workspace");
            var row=ReadTaskEvent(task.TaskId,eventId);
            if(row.Input.Producer!="windows_host"||row.Input.EventType!="external_reference_imported"||row.Input.VerificationStatus!="unverified")
                throw new UnauthorizedAccessException("Identity is not a Host reference import");
            return task;
        }
    }
}

// Explicit administrative repository observation; never a model truth or knowledge-write grant.
internal static class ExternalClaimVerification
{
    internal sealed record Request(int Version,string ReferenceEventId,int Page,string ReferenceQuote,
        string RepositoryPath,string ExpectedRepositoryHash,string RepositoryQuote);
    internal sealed record Result(string EventId,string RequestHash,string RepositoryHash,string Head,
        bool RepositoryQuoteFound,bool Duplicate,bool SemanticClaimsVerified=false,bool KnowledgeAdopted=false);
    internal static Request Parse(string json)
    {
        var value=DecisionProtocol.Parse(json);
        DecisionProtocol.Keys(value,"version","reference_event_id","page","reference_quote","repository_path","expected_repository_hash","repository_quote");
        if(value.GetProperty("version").GetInt32()!=1)throw new InvalidDataException("Unknown external verification request version");
        var page=value.GetProperty("page").GetInt32();if(page is <1 or >200)throw new InvalidDataException("Invalid reference page");
        return new(1,DecisionProtocol.Text(value,"reference_event_id",100),page,
            DecisionProtocol.Text(value,"reference_quote",4000),DecisionProtocol.Text(value,"repository_path",260),
            DecisionProtocol.HashText(DecisionProtocol.Text(value,"expected_repository_hash",64)),DecisionProtocol.Text(value,"repository_quote",4000));
    }
    internal static async Task<Result> CheckAsync(CanonicalStore store,string taskId,string root,Request request)
    {
        // Validate typed callers through the same exact request contract as the administrative CLI.
        request=Parse(JsonSerializer.Serialize(new{version=request.Version,reference_event_id=request.ReferenceEventId,page=request.Page,
            reference_quote=request.ReferenceQuote,repository_path=request.RepositoryPath,expected_repository_hash=request.ExpectedRepositoryHash,repository_quote=request.RepositoryQuote}));
        root=Path.GetFullPath(root);CanonicalStore.GuardPath(root);
        var task=store.GetTask(taskId)??throw new InvalidDataException("Unknown reference owner");
        var workspace=store.GetWorkspace(task.WorkspaceId);
        if(!string.Equals(Path.GetFullPath(workspace.WorkspaceRoot),root,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Verification workspace differs from canonical owner");
        var reference=store.ReadTaskEvent(taskId,request.ReferenceEventId);
        if(reference.Input.Producer!="windows_host"||reference.Input.EventType!="external_reference_imported"||reference.Input.VerificationStatus!="unverified")
            throw new InvalidDataException("Verification needs this task's Host reference import");
        var metadata=reference.Input.Metadata;
        var textHash=metadata.GetProperty("text_hash").GetString()!;var manifestHash=metadata.GetProperty("manifest_hash").GetString()!;
        var originalHash=metadata.GetProperty("original_hash").GetString()!;
        foreach(var hash in new[]{textHash,manifestHash,originalHash})if(!reference.Input.EvidenceRefs.Contains(hash))throw new InvalidDataException("Reference evidence binding missing");
        _=store.ReadEvidence(originalHash);var text=store.ReadEvidence(textHash);
        using var manifest=JsonDocument.Parse(store.ReadEvidence(manifestHash));
        var pages=manifest.RootElement.GetProperty("page_map");if(request.Page>pages.GetArrayLength())throw new InvalidDataException("Reference page absent");
        var page=pages[request.Page-1];var start=page.GetProperty("start_byte").GetInt32();var end=page.GetProperty("end_byte").GetInt32();
        var utf8=new UTF8Encoding(false,true);
        if(!utf8.GetString(text.AsSpan(start,end-start)).Contains(request.ReferenceQuote,StringComparison.Ordinal))throw new InvalidDataException("Exact quotation absent on selected PDF page");
        var relative=HostEgress.NormalizePath(root,request.RepositoryPath);var path=Path.Combine(root,relative);
        byte[] Read(){CanonicalStore.GuardPath(path);using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(file.Length is 0 or >ExternalReferenceCorpus.MaxBytes)throw new InvalidDataException("Repository observation exceeds budget");
            var bytes=new byte[(int)file.Length];file.ReadExactly(bytes);return bytes;}
        async Task<string> Head()=>Encoding.UTF8.GetString(await AttemptStartingState.GitAsync(root,["rev-parse","HEAD"])).Trim();
        async Task<byte[]> Diff()=>await AttemptStartingState.GitAsync(root,["diff","HEAD","--binary","--no-ext-diff","--no-textconv","--",relative]);
        var head=await Head();var diff=await Diff();var bytes=Read();var repositoryHash=CanonicalStore.Hash(bytes);
        if(repositoryHash!=request.ExpectedRepositoryHash)throw new InvalidDataException("Current repository hash differs; refresh the explicit request");
        var found=utf8.GetString(bytes).Contains(request.RepositoryQuote,StringComparison.Ordinal);
        var finalHead=await Head();var finalDiff=await Diff();
        if(finalHead!=head||!Read().SequenceEqual(bytes)||!diff.SequenceEqual(finalDiff))throw new InvalidDataException("Repository changed during observation");
        var requestBytes=JsonSerializer.SerializeToUtf8Bytes(request);var requestHash=CanonicalStore.Hash(requestBytes);
        var diffHash=CanonicalStore.Hash(diff);
        var observationHash=CanonicalStore.Hash(JsonSerializer.SerializeToUtf8Bytes(new{request_hash=requestHash,head,diff_hash=diffHash}));
        var old=store.EventIds(taskId).Select(id=>store.ReadTaskEvent(taskId,id)).SingleOrDefault(e=>e.Input.EventType=="external_claim_repository_observed"&&e.Input.IdempotencyKey=="reference_check_"+observationHash);
        if(old is not null)return new(old.EventId,requestHash,repositoryHash,head,found,true);
        var requestRef=store.PutEvidence(requestBytes);var repositoryRef=store.PutEvidence(bytes);var diffRef=store.PutEvidence(diff);
        var receipt=store.Append(new(taskId,task.WorkspaceId,"windows_host","reference_check_"+observationHash,
            "external_claim_repository_observed","external_claim_repository_observation","host_observed",
            [requestRef,repositoryRef,diffRef,originalHash,textHash,manifestHash],JsonSerializer.SerializeToElement(new{
                reference_event_id=reference.EventId,request_hash=requestHash,observation_hash=observationHash,original_source_id=metadata.GetProperty("source_id").GetString(),
                page=request.Page,repository_path=relative,repository_hash=repositoryHash,head,diff_hash=diffRef,repository_quote_found=found,
                verification_scope="Exact literal repository quotation at observed HEAD/file hash only; no semantic claim or executable behavior verification",
                semantic_claims_verified=false,knowledge_adopted=false,review_required=true,learning_status="PENDING_REVIEW_NOT_ADOPTED"}),CausedByEventId:reference.EventId));
        return new(receipt.EventId,requestHash,repositoryHash,head,found,receipt.Duplicate);
    }
}
