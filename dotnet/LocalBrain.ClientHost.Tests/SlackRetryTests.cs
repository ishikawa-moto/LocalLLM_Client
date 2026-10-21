using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LocalBrain.ClientHost.Ocr;
using LocalBrain.ClientHost.Slack;

// All HTTP responses are in-memory fixtures: no token, Slack account, OCR or model is used.
internal static class SlackRetryTests
{
    internal static async Task RunAsync()
    {
        var checks=0;void Check(bool value,string name){if(!value)throw new Exception("SLACK_RETRY_TEST: "+name);checks++;}
        var root=Directory.CreateTempSubdirectory("slack-retry-public-").FullName;
        var options=new SlackOptions(true,Path.Combine(root,"slack"),"TTEST","CTEST","UBOT",Path.Combine(root,"app.token"),Path.Combine(root,"bot.token"));
        var ocr=new OcrConfig(true,Path.Combine(root,"ocr"),Path.Combine(root,"assets"),Path.Combine(root,"assets","python.exe"),Path.Combine(root,"assets","worker.py"),Path.Combine(root,"assets","models"),[root],new(){["a"]="a",["b"]="b",["c"]="c"},["AUTO","PDF_DOCUMENT"],[],512L<<20,512L<<20,384L<<20,30,new string('a',64));
        var gaps=new List<double>();
        foreach(var mode in new[]{"metadata","download","cancel","rejected"}){
            var fixtureOptions=options with{Root=Path.Combine(root,mode)};using var store=new SlackStore(fixtureOptions);var now=DateTimeOffset.UtcNow;
            var mention=new SlackMention("EV-"+mode,"TTEST","CTEST","UALICE",now.ToUnixTimeSeconds()+".000000",now.ToUnixTimeSeconds()+".000000","PUBLIC fixture",["FPUBLIC"],now);
            var id=store.Accept(mention,now.AddMinutes(-1),now).JobId!;var receipt=store.ClaimReply(now)!;store.ReplySent(receipt.Id,"1700000000.000001");
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));var times=new List<double>();var timer=Stopwatch.StartNew();
            using var api=new SlackApi(new Handler(request=>{
                if(mode=="download"&&request.RequestUri!.Host=="slack.com")return new(HttpStatusCode.OK){Content=new StringContent("{\"ok\":true,\"file\":{\"id\":\"FPUBLIC\",\"size\":8,\"channels\":[\"CTEST\"],\"mimetype\":\"image/png\",\"url_private\":\"https://files.slack.com/files-pri/PUBLIC\"}}")};
                times.Add(timer.Elapsed.TotalSeconds);
                if(mode=="rejected"){stop.CancelAfter(150);return new(HttpStatusCode.OK){Content=new StringContent("{\"ok\":false,\"error\":\"file_not_found\"}")};}
                if(times.Count==2||mode=="cancel")stop.CancelAfter(mode=="cancel"?150:20);
                var response=new HttpResponseMessage(HttpStatusCode.TooManyRequests);response.Headers.RetryAfter=new(TimeSpan.FromSeconds(mode=="cancel"?60:2));return response;
            }),"xapp-public-fixture","xoxb-public-fixture");
            var processor=new SlackProcessor(fixtureOptions,store,api,new NoInference(),ocr,SlackPriority.ForFixture(ocr,()=>new(long.MaxValue,long.MaxValue,0)));
            try{await processor.Run(stop.Token);throw new Exception("Unexpected processor return");}catch(OperationCanceledException)when(stop.IsCancellationRequested){}
            if(mode=="rejected")Check(times.Count==1&&store.GetJob(id).Phase=="failed","file rejection fails only its job without faulting owner");
            else if(mode=="cancel")Check(times.Count==1&&timer.Elapsed<TimeSpan.FromSeconds(3),"owner cancellation interrupts Retry-After delay");
            else{Check(times.Count==2,"temporary "+mode+" rejection is retried");var gap=times[1]-times[0];gaps.Add(gap);Check(gap>=1.9&&store.GetJob(id).Phase=="queued","Retry-After respected without permanently failing "+mode+" job");}
        }
        // Headers arrive immediately, while the body never arrives. The API's own
        // body deadline must cancel before the caller's longer service/test token.
        using(var api=new SlackApi(new Handler(_=>new(HttpStatusCode.OK){Content=new StalledContent()}),"xapp-public-fixture","xoxb-public-fixture")){
            using var caller=new CancellationTokenSource(TimeSpan.FromSeconds(30));var timer=Stopwatch.StartNew();
            try{await api.Open(caller.Token);throw new Exception("Stalled response was accepted");}
            catch(OperationCanceledException){Check(!caller.IsCancellationRequested&&timer.Elapsed<TimeSpan.FromSeconds(27),"body deadline bounds reads after headers");}
        }
        Console.WriteLine(JsonSerializer.Serialize(new{slack_retry_public_checks=checks,status="PASS",retry_after_seconds=2,actual_retry_gaps_seconds=gaps,real_slack=false,real_model=false}));
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> reply):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(reply(request));}
    private sealed class StalledContent:HttpContent
    {
        protected override bool TryComputeLength(out long length){length=0;return false;}
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync()=>Task.FromResult<Stream>(new StalledStream());
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token)=>CreateContentReadStreamAsync();
    }
    private sealed class StalledStream:Stream
    {
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){await Task.Delay(Timeout.InfiniteTimeSpan,token);return 0;}
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
    private sealed class NoInference:ISlackLocalInference
    {
        public Task<string> Chat(string requestId,string session,string context,string prompt,int outputTokens,int seconds,CancellationToken token)=>throw new Exception("Unexpected model call");
        public Task<SlackReviewedCandidate?> Candidate(string session,string source,CancellationToken token)=>throw new Exception("Unexpected review");
        public Task<string> RegisterProposal(string id,string reviewRequestId,string session,CancellationToken token)=>throw new Exception("Unexpected proposal");
    }
}
