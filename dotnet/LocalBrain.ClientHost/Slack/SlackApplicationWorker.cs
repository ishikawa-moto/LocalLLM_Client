using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

namespace LocalBrain.ClientHost.Slack;

// Dispatches only an immutable reservation created by the authenticated administrator's exact approval.
// Network failure, process death, expiry or unknown status never authorize a second apply.
internal sealed class SlackApplicationWorker(SlackStore store,GatewayClient gateway)
{
    internal async Task Run(CancellationToken token)
    {
        while(!token.IsCancellationRequested){await Step(token);await RetireStep(token);await Task.Delay(TimeSpan.FromSeconds(10),token);}
    }
    internal async Task RetireStep(CancellationToken token)
    {
        using var restricted=RestrictedExecution.EnterSlack();
        foreach(var (session,request) in store.PendingRetirements()){
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try{
                var raw=await gateway.SlackJsonAsync("/v1/slack/retire",JsonSerializer.Serialize(new{request_id=request,session_id=session,restriction=SlackRestriction.Instance},SlackOptions.Json),deadline.Token);
                var value=DecisionProtocol.Parse(raw);
                if(value.GetProperty("status").GetString()=="retired"&&value.GetProperty("session_id").GetString()==session&&value.GetProperty("request_id").GetString()==request&&value.GetProperty("adopted_knowledge_preserved").ValueKind==JsonValueKind.True)store.RetirementComplete(session,request);
                // An idempotent cleanup may be observed again. It never starts a model or applies knowledge.
            }catch(Exception e)when(e is HttpRequestException or IOException or JsonException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException){if(token.IsCancellationRequested)throw;}
        }
    }
    internal async Task Step(CancellationToken token)
    {
        using var restricted=RestrictedExecution.EnterSlack();
        foreach(var previous in store.UncertainApplications()){
            try{
                var raw=await gateway.SlackJsonAsync("/v1/slack/status",JsonSerializer.Serialize(new{operation="apply",request_id=previous.RequestId,session_id=previous.SessionId,request_hash=previous.RequestHash,restriction=SlackRestriction.Instance},SlackOptions.Json),token);
                var status=DecisionProtocol.Parse(raw);var state=status.GetProperty("state").GetString();
                if(state=="applied"){
                    if(status.GetProperty("request_hash").GetString()!=previous.RequestHash)throw new InvalidDataException("SLACK_STATUS_HASH_MISMATCH");
                    store.ApplicationComplete(previous,status.GetProperty("result").GetRawText());
                }else if(state is "unknown" or "unresolved" or "interrupted")store.ApplicationHold(previous);
                // Running is observed again later; never relaunch it.
            }catch(Exception e)when(e is HttpRequestException or IOException or JsonException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException){if(token.IsCancellationRequested)throw;}
        }
        if(store.ClaimApplication(DateTimeOffset.UtcNow) is not{} application)return;
        try{
            var result=await gateway.SlackJsonAsync("/v1/slack/apply",application.Json,token);
            store.ApplicationComplete(application,result);
        }catch(Exception e)when(e is HttpRequestException or IOException or JsonException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException){
            store.ApplicationUncertain(application);if(token.IsCancellationRequested)throw;
        }
    }
}
