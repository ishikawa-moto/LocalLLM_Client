using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LocalBrain.ClientHost.AgentV2;

// Administrative Windows Host corpus entry. Never an Actor/MCP tool or an adoption operation.
internal static class ExternalReferenceCorpus
{
    internal const int MaxBytes=4*1024*1024;
    internal sealed record ImportResult(string EventId,string SourceId,string OriginalHash,string TextHash,bool Duplicate,bool ClaimsVerified=false,bool KnowledgeAdopted=false);
    internal static ImportResult Import(CanonicalStore store,string taskId,byte[] original,byte[] text,byte[] manifestBytes)
    {
        var task=store.GetTask(taskId)??throw new InvalidDataException("No canonical reference owner");
        if(original.Length is 0 or >MaxBytes||text.Length is 0 or >MaxBytes||manifestBytes.Length is 0 or >1_000_000)
            throw new InvalidDataException("Reference exceeds Host corpus input budget");
        if(!original.AsSpan().StartsWith("%PDF-"u8))throw new InvalidDataException("Reference original is not a PDF");
        _=new UTF8Encoding(false,true).GetString(text);
        using var document=JsonDocument.Parse(manifestBytes);var m=document.RootElement;
        var originalHash=CanonicalStore.Hash(original);var textHash=CanonicalStore.Hash(text);
        void Require(bool value,string reason){if(!value)throw new InvalidDataException(reason);}
        string S(string key)=>m.GetProperty(key).GetString()??throw new InvalidDataException("Missing reference field");
        Require(S("schema")=="localbrain.external-reference.v1"&&S("source_type")=="pdf","Unknown corpus reference schema");
        Require(S("review_state")=="unverified"&&!m.GetProperty("allow_fact_promotion").GetBoolean(),"External reference must require review");
        Require(m.GetProperty("explicit_claim_verification_required").GetBoolean()&&!m.GetProperty("claims_are_verified").GetBoolean()
            &&!m.GetProperty("knowledge_adopted").GetBoolean()&&!m.GetProperty("current_repository_authority").GetBoolean(),"Reference cannot assert verified knowledge or repository authority");
        Require(string.Equals(S("original_pdf_sha256"),originalHash,StringComparison.OrdinalIgnoreCase)
            &&string.Equals(S("source_hash"),textHash,StringComparison.OrdinalIgnoreCase)
            &&string.Equals(S("extracted_text_sha256"),textHash,StringComparison.OrdinalIgnoreCase),"Reference bytes/hash differ");
        var sourceId="source:"+originalHash.ToLowerInvariant();Require(S("source_id")==sourceId,"Original document identity differs");
        Require(m.GetProperty("independent_document_count").GetInt32()==1,"PDF pages are one source document");
        Require(S("source_uri").Length is >0 and <=2000&&!string.IsNullOrWhiteSpace(S("source_uri")),"Source URI required");
        var retrieved=S("retrieved_at");
        Require(retrieved.Contains('T')&&(retrieved.EndsWith('Z')||Regex.IsMatch(retrieved,"[+-][0-9]{2}:[0-9]{2}$"))
            &&DateTimeOffset.TryParse(retrieved,CultureInfo.InvariantCulture,DateTimeStyles.None,out _),"Retrieval metadata must include an explicit timezone");
        var pages=m.GetProperty("page_map");Require(pages.GetArrayLength() is >0 and <=200,"Reference page count differs");
        int end=0,number=0;
        foreach(var page in pages.EnumerateArray()){
            number++;var next=page.GetProperty("end_byte").GetInt32();
            Require(page.GetProperty("page").GetInt32()==number&&page.GetProperty("start_byte").GetInt32()==end
                &&next>end&&next<=text.Length,"Reference page map differs");
            Require(string.Equals(page.GetProperty("text_sha256").GetString(),CanonicalStore.Hash(text.AsSpan(end,next-end)),StringComparison.OrdinalIgnoreCase),"Reference page hash differs");end=next;
        }
        Require(end==text.Length,"Reference page map is incomplete");
        // Preserve the first provenance for the same original, rather than counting alternate URLs as corroboration.
        foreach(var id in store.EventIds(taskId)){
            var old=store.ReadTaskEvent(taskId,id);
            if(old.Input.EventType!="external_reference_imported"||old.Input.Metadata.GetProperty("source_id").GetString()!=sourceId)continue;
            Require(old.Input.Metadata.GetProperty("text_hash").GetString()==textHash,"Same original needs an explicit extraction revision");
            return new(id,sourceId,originalHash,textHash,true);
        }
        var originalRef=store.PutEvidence(original);var textRef=store.PutEvidence(text);var manifestRef=store.PutEvidence(manifestBytes);
        var metadata=JsonSerializer.SerializeToElement(new {source_id=sourceId,original_hash=originalHash,text_hash=textHash,
            manifest_hash=manifestRef,page_count=number,independent_document_count=1,claims_verified=false,knowledge_adopted=false,
            review_state="unverified",verification_scope="Original/text/page integrity and retained provenance only; document claims remain unverified"});
        var receipt=store.Append(new(taskId,task.WorkspaceId,"windows_host","external_ref_"+originalHash,
            "external_reference_imported","external_reference","unverified",[originalRef,textRef,manifestRef],metadata));
        return new(receipt.EventId,sourceId,originalHash,textHash,receipt.Duplicate);
    }
}
