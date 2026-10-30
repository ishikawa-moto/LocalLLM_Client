namespace LocalBrain.ClientHost.AgentV2;

internal enum ReviewerRoute { None, Sol }

internal sealed record ReviewerRoutingDecision(ReviewerRoute Route, string Source)
{
    public string RouteName => Route.ToString().ToLowerInvariant();
}
