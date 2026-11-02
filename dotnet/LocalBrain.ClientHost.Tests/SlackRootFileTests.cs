using System.Net;
using System.Text.Json;
using LocalBrain.ClientHost.Slack;

internal static class SlackRootFileTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage,HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(reply(request));
    }
    internal static async Task RunAsync()
    {
        var root=Directory.CreateTempSubdirectory("slack-root-fixture-").FullName;
        var options=new SlackOptions(true,root,"TTEST","CTEST","UBOT",Path.Combine(root,"app"),Path.Combine(root,"bot"));
        var ts="1700000000.000001";var calls=0;
        using var api=new SlackApi(new FakeHandler(request=>{
            calls++;
            if(request.RequestUri?.Host!="slack.com"||request.RequestUri.AbsolutePath!="/api/conversations.replies"||
               !request.RequestUri.Query.Contains("limit=1")||!request.RequestUri.Query.Contains("channel=CTEST"))throw new Exception("root query bounds");
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{ok=true,messages=new[]{new{ts,files=new[]{new{id="FROOT"}}}}}))};
        }),"fixture-app","fixture-bot");
        var ids=await api.RootFiles("CTEST",ts,options,CancellationToken.None);
        if(ids.Length!=1||ids[0]!="FROOT"||calls!=1)throw new Exception("root file selection");
        try{await api.RootFiles("COTHER",ts,options,CancellationToken.None);throw new Exception("foreign channel accepted");}catch(UnauthorizedAccessException){}
        using var wrong=new SlackApi(new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"ok\":true,\"messages\":[{\"ts\":\"1700000000.000002\",\"files\":[{\"id\":\"FOTHER\"}]}]}")}),"fixture-app","fixture-bot");
        try{await wrong.RootFiles("CTEST",ts,options,CancellationToken.None);throw new Exception("wrong root accepted");}catch(InvalidDataException){}
    }
}
