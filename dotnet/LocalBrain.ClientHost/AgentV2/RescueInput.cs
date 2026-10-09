using System.Text;

namespace LocalBrain.ClientHost.AgentV2;

internal static class RescueInput
{
    internal static async Task<string> ReadAsync(Stream stream,int maximumBytes=2*1024*1024,CancellationToken token=default,TimeSpan? deadline=null) {
        using(stream)using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)) {
            timeout.CancelAfter(deadline??TimeSpan.FromSeconds(30));using var output=new MemoryStream();var buffer=new byte[8192];
            while(true) {
                var remaining=maximumBytes-(int)output.Length;var count=await stream.ReadAsync(buffer.AsMemory(0,Math.Min(buffer.Length,remaining+1)),timeout.Token).AsTask().WaitAsync(timeout.Token);
                if(count==0)break;if(count>remaining)throw new InvalidDataException("Candidate exceeds protocol byte budget");output.Write(buffer,0,count);
            }
            return new UTF8Encoding(false,true).GetString(output.ToArray());
        }
    }
}