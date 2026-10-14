using System.Net;
using System.Text.Json;
using LocalBrain.ClientHost;

internal static class DecisionProposalMcpTests
{
    public static async Task RunAsync()
    {
        var reference = new { path="wiki/fixture.md", expected_hash=new string('a',64), expected_metadata_hash=new string('b',64), relevant_location="L7" };
        var args = JsonSerializer.SerializeToElement(new {request_id="binding_fixture_001",project="demo",title="日本語 fixture",content="引用・改行\n😀",references=new[]{reference}});
        var invalid = JsonSerializer.SerializeToElement(new {request_id="binding_fixture_002",project="demo",title="fixture",content="body",references=new[]{"wiki/fixture.md"}});
        var lines = new[]{
            JsonSerializer.Serialize(new {jsonrpc="2.0",id=1,method="tools/call",@params=new{name="brain_apply_decision",arguments=args}}),
            JsonSerializer.Serialize(new {jsonrpc="2.0",id=2,method="tools/call",@params=new{name="brain_prepare_decision",arguments=args}}),
            JsonSerializer.Serialize(new {jsonrpc="2.0",id=3,method="tools/call",@params=new{name="brain_prepare_decision",arguments=invalid}}),
            JsonSerializer.Serialize(new {jsonrpc="2.0",id=4,method="tools/call",@params=new{name="brain_status",arguments=new{}}})};
        var fixture = new Handler(); using var gateway=new GatewayClient(new Uri("https://fixture.invalid/"),fixture);
        var savedIn=Console.In; var savedOut=Console.Out; using var output=new StringWriter();
        try {Console.SetIn(new StringReader(string.Join("\n",lines)+"\n"));Console.SetOut(output);await new McpServer(gateway).RunAsync();}
        finally {Console.SetIn(savedIn);Console.SetOut(savedOut);}
        var rows=output.ToString().Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(line=>JsonDocument.Parse(line)).ToArray();
        try {
            JsonElement Value(int i)=>JsonDocument.Parse(rows[i].RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
            Check(rows.Length==4,"MCP response count");
            Check(Value(0).GetProperty("reason_code").GetString()=="proposal_binding_required" && rows[0].RootElement.GetProperty("result").GetProperty("isError").GetBoolean(),"Legacy path rejected locally");
            var prepared=Value(1); Check(prepared.GetProperty("status").GetString()=="pending_host_approval"&&!prepared.GetProperty("applied").GetBoolean(),"Draft is not adoption");
            Check(prepared.GetProperty("proposal_receipt").GetProperty("payload").GetProperty("body").GetString()=="引用・改行\n😀","Exact Unicode body preserved");
            Check(rows[2].RootElement.GetProperty("id").GetInt32()==3&&rows[2].RootElement.TryGetProperty("error",out _),"Legacy string references rejected before HTTP");
            Check(Value(3).GetProperty("status").GetString()=="ok","Status continuity");
            Check(fixture.Proposals==1 && fixture.Apply==0,"Exactly one draft request; no adoption HTTP");
            var sent=JsonDocument.Parse(fixture.Request!).RootElement;Check(!sent.TryGetProperty("approval",out _)&&JsonElement.DeepEquals(sent.GetProperty("references"),args.GetProperty("references")),"No fabricated approval or changed references");
            var rejected="{\"status\":\"rejected\",\"reason_code\":\"stale_reference\"}";
            Check(DecisionProposalMcp.Prepared(fixture.Request!,rejected)==rejected,"Business rejection retained");
            var receipt=fixture.Receipt!;var changed=receipt.Replace("binding_fixture_001","binding_fixture_999");
            try {DecisionProposalMcp.Prepared(fixture.Request!,changed);throw new Exception("Receipt identity drift accepted");}catch(InvalidDataException){}
        }finally {foreach(var row in rows)row.Dispose();}
        Console.WriteLine("MCP bound decision migration: legacy no-HTTP, exact Unicode/snapshots, no fake approval/adoption, invalid references, receipt drift and rejection continuity PASS; fixture only.");
    }
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    private sealed class Handler:HttpMessageHandler
    {
        internal int Proposals,Apply;internal string? Request,Receipt;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message,CancellationToken token){
            var path=message.RequestUri!.AbsolutePath;if(path.Contains("apply")){Apply++;throw new Exception("Forbidden apply request");}
            if(path.EndsWith("status"))return new(HttpStatusCode.OK){Content=new StringContent("{\"status\":\"ok\"}")};
            if(path!="/v1/brain/propose-bound-decision")throw new Exception("Unexpected fixture route");
            Proposals++;Request=await message.Content!.ReadAsStringAsync(token);using var doc=JsonDocument.Parse(Request);var r=doc.RootElement;
            Receipt=JsonSerializer.Serialize(new {schema="localbrain.decision-proposal.v1",status="draft",proposal_id=r.GetProperty("request_id"),proposal_hash=new string('c',64),path="drafts/pending-review/binding_fixture_001.md",content=r.GetProperty("body"),payload=new{schema="localbrain.decision-proposal.v1",kind="decision",proposal_id=r.GetProperty("request_id"),project=r.GetProperty("project"),title=r.GetProperty("title"),body=r.GetProperty("body"),references=r.GetProperty("references")}});
            return new(HttpStatusCode.OK){Content=new StringContent(Receipt)};
        }
    }
}
