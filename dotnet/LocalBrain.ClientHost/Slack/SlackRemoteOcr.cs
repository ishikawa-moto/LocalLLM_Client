using System.Text.Json;

namespace LocalBrain.ClientHost.Slack;

// Only the restricted Host owns this adapter; IDs bind retries to one attachment.
internal sealed class SlackRemoteOcr(GatewayClient gateway)
{
    private async Task<string> Call(string action,string payload,CancellationToken token) {
        try{return await gateway.SlackOcrJsonAsync(action,payload,token);}
        catch(Exception e)when(!token.IsCancellationRequested && e is (HttpRequestException or IOException or OperationCanceledException)){throw new SlackDeferred();}
    }
    internal static string JobId(string job,string file)=>SlackHash.Of("slack-ocr-job:"+job+":"+file)[..32];
    internal static string ConversationId(string job,string file)=>Guid.ParseExact(SlackHash.Of("slack-ocr-owner:"+job+":"+file)[..32],"N").ToString("D");
    internal async Task Submit(string job,SlackStore.Attachment attachment,CancellationToken token) {
        var payload=new {job_id=JobId(job,attachment.FileId),conversation_id=ConversationId(job,attachment.FileId),input_sha256=attachment.Hash,
            extension=attachment.Extension,formats=new[]{"md"},bytes=Convert.ToBase64String(attachment.Bytes)};
        await Call("submit",JsonSerializer.Serialize(payload),token);
    }
    internal async Task<string> State(string job,string file,CancellationToken token) {
        using var d=JsonDocument.Parse(await Call("status",JsonSerializer.Serialize(new {conversation_id=ConversationId(job,file)}),token));
        var value=d.RootElement.GetProperty("jobs").EnumerateArray().SingleOrDefault(x=>x.GetProperty("job_id").GetString()==JobId(job,file));
        return value.ValueKind==JsonValueKind.Undefined?throw new InvalidDataException("SLACK_OCR_JOB_MISSING"):value.GetProperty("state").GetString()!;
    }
    internal async Task<string> Result(string job,string file,string inputHash,CancellationToken token) {
        using var d=JsonDocument.Parse(await Call("result",JsonSerializer.Serialize(new {conversation_id=ConversationId(job,file),job_id=JobId(job,file)}),token));
        var result=d.RootElement.GetProperty("result");
        var ocrId=SlackHash.Of("slack_"+JobId(job,file))[..32];
        if(result.GetProperty("origin").GetString()!="slack" || result.GetProperty("external_ai").GetString()!="deny" || result.GetProperty("automatic_knowledge_adoption").ValueKind!=JsonValueKind.False ||
            result.GetProperty("job_id").GetString()!=ocrId || result.GetProperty("input_sha256").GetString()!=inputHash || result.GetProperty("input_file_ref").GetString()!="ocr:"+ocrId+"/input" || result.GetProperty("status").GetString()!="complete")throw new InvalidDataException("SLACK_OCR_CLASSIFICATION_OR_BINDING");
        return result.GetRawText();
    }
}
