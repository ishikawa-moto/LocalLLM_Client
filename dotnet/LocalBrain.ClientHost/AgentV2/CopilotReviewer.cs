using System.Text;

namespace LocalBrain.ClientHost.AgentV2;

/// <summary>Packet-only Opus fallback, enabled only after qualification on this Host.</summary>
internal static class CopilotReviewer
{
    internal sealed record Review(string Verdict, string[] Issues);

    public static async Task<Review> ReviewFinalAsync(string packet, CancellationToken token,
        Action<string>? diagnostic = null, string[]? sources = null, string[]? files = null)
    {
        Slack.RestrictedExecution.DenyExternal();
        var reply=await ReviewerCli.InvokeAsync("copilot",packet,"high",token,sources,files);
        var review=reply.Status=="OK"?Parse(reply.Answer):new Review(reply.Status=="INVALID"?"INVALID":"UNAVAILABLE",[]);
        HostTaskJournal.Current?.Record("external_review_result",new {provider="copilot",requested_model=ReviewerCli.Model("copilot"),
            actual_model_id=(string?)null,actual_model_verified=false,packet_hash=CanonicalStore.Hash(Encoding.UTF8.GetBytes(packet)),
            verdict=review.Verdict,issues=review.Issues},"external_unverified");
        diagnostic?.Invoke("parsed="+review.Verdict);
        return review;
    }

    internal static Review Parse(string answer)
    {
        var json=answer.Trim();
        if(json.StartsWith("```json\n",StringComparison.OrdinalIgnoreCase) && json.EndsWith("\n```",StringComparison.Ordinal))
            json=json[8..^4].Trim();
        var parsed=SolReviewer.Parse(json);
        return new(parsed.Verdict=="INVALID"?"UNAVAILABLE":parsed.Verdict,parsed.Issues);
    }
}
