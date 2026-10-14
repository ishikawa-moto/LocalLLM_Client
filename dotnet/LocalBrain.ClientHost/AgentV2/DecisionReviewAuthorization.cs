using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

internal static class DecisionReviewAuthorization
{
    internal static JsonElement Validate(DecisionAdoption.Intent intent,string taskId,string policyHash,JsonElement response,string? nativeReaffirmationHash)
    {
        DecisionProtocol.HashText(policyHash);
        if(response.GetProperty("schema").GetString()!="localbrain.decision-review.v1")throw new InvalidDataException("Unknown decision review schema");
        var record=response.GetProperty("review_receipt");
        if(DecisionProtocol.Digest(record)!=DecisionProtocol.HashText(response.GetProperty("review_receipt_hash").GetString()!))throw new InvalidDataException("Review receipt hash differs");
        var proposalId=intent.payload.GetProperty("origin_proposal_id").GetString();var proposalHash=intent.payload.GetProperty("expected_proposal_hash").GetString();
        if(intent.operation!="decision"||record.GetProperty("policy").GetString()!=AutomaticDecisionPolicy.Name||record.GetProperty("host_policy_hash").GetString()!=policyHash||record.GetProperty("task_id").GetString()!=taskId||
            record.GetProperty("proposal_id").GetString()!=proposalId||record.GetProperty("proposal_hash").GetString()!=proposalHash||response.GetProperty("proposal_id").GetString()!=proposalId||response.GetProperty("proposal_hash").GetString()!=proposalHash||
            response.GetProperty("state").GetString()!=record.GetProperty("state").GetString())throw new UnauthorizedAccessException("Review content/owner/policy binding differs");
        var mode=record.GetProperty("request_mode").GetString();if(mode is not ("proposal" or "user_request"))throw new InvalidDataException("Unknown request origin");
        if(nativeReaffirmationHash is null){
            if(record.GetProperty("state").GetString()!="approved"||record.GetProperty("review_passes").GetInt32()!=2||record.GetProperty("passes").GetArrayLength()!=2||record.GetProperty("issues").GetArrayLength()!=0)throw new UnauthorizedAccessException("Reviewer has not passed twice");
        }else{
            DecisionProtocol.HashText(nativeReaffirmationHash);
            if(mode!="user_request"||record.GetProperty("state").GetString()!="returned"||record.GetProperty("return_count").GetInt32()!=1||record.GetProperty("issues").GetArrayLength()==0)throw new UnauthorizedAccessException("Only an exact once-returned explicit save request can be reaffirmed");
        }
        return record;
    }
}
