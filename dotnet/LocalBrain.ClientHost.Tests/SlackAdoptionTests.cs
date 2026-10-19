using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.AgentV2;
using LocalBrain.ClientHost.Slack;

internal static class SlackAdoptionTests
{
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> action):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t)=>action(r);}
    internal static async Task RunAsync()
    {
        var checks=0;void Check(bool ok,string why){if(!ok)throw new Exception("SLACK_ADOPTION_TEST: "+why);checks++;}
        void Denied(Action a,string why){try{a();}catch(Exception e)when(e is UnauthorizedAccessException or InvalidDataException){checks++;return;}throw new Exception(why);}
        var fixture=DecisionProtocol.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","slack-server-contract.json")));
        var retirement=DecisionProtocol.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","slack-retire.json")));
        Check(retirement.GetProperty("schema").GetString()=="localbrain.slack-retirement-fixture.v1","fixed Server retirement contract");
        foreach(var kind in new[]{"knowledge","rule"}){
            var example=fixture.GetProperty(kind);
            Check(DecisionProtocol.Digest(example.GetProperty("proposal_response").GetProperty("payload"))==example.GetProperty("proposal_response").GetProperty("proposal_hash").GetString(),"exact Python proposal canonical vector "+kind);
            Check(DecisionProtocol.Digest(example.GetProperty("review_response").GetProperty("review"))==example.GetProperty("review_response").GetProperty("review_hash").GetString(),"exact Python review canonical vector "+kind);
            Check(DecisionProtocol.Digest(example.GetProperty("apply_request"))==example.GetProperty("status_request").GetProperty("request_hash").GetString(),"exact Python application canonical vector "+kind);
            var root=Directory.CreateTempSubdirectory("slack-adoption-public-").FullName;var now=DateTimeOffset.UtcNow;
            using var store=new SlackStore(new(true,root,"TTEST","CTEST","UBOT",Path.Combine(root,"a.token"),Path.Combine(root,"b.token")));
            var m=new SlackMention("EV-PUBLIC","TTEST","CTEST","UALICE",now.ToUnixTimeSeconds()+".000000",now.ToUnixTimeSeconds()+".000000","<@UBOT> PUBLIC",[],now);
            var job=store.GetJob(store.Accept(m,now.AddSeconds(-2),now).JobId!);var session=job.SessionId;
            using(RestrictedExecution.EnterSlack()){
                var first=JsonSerializer.Serialize(new{request_id="inference_fixture_01",session_id=session,prompt="PUBLIC original"});
                var exact=store.FreezeInference("/v1/slack/chat","inference_fixture_01",session,first);
                var later=first.Replace("original","changed");Check(store.FreezeInference("/v1/slack/chat","inference_fixture_01",session,later)==exact,"restart reuses exact inference input");
                Denied(()=>store.FreezeInference("/v1/slack/review","inference_fixture_01",session,first),"same ID route drift rejected");
            }
            var review=JsonNode.Parse(example.GetProperty("review_response").GetRawText())!.AsObject();review["review"]!["session_id"]=session;
            var rh=DecisionProtocol.Digest(JsonSerializer.SerializeToElement(review["review"]));review["review_hash"]=rh;
            var proposal=JsonNode.Parse(example.GetProperty("proposal_response").GetRawText())!.AsObject();proposal["payload"]!["session_id"]=session;proposal["payload"]!["review_hash"]=rh;
            proposal["proposal_hash"]=DecisionProtocol.Digest(JsonSerializer.SerializeToElement(proposal["payload"]));
            var p=store.BindServerProposal(session,proposal.ToJsonString(),review.ToJsonString(),now);
            Check(store.ClaimApplication(now) is null,"no apply without human");
            Denied(()=>store.Authorize(p.Id,p.Hash,"member_public",false,"approve",now,new string('a',64)),"ordinary member denied");
            var changed=JsonNode.Parse(proposal.ToJsonString())!.AsObject();changed["payload"]!["classification"]!["external_ai"]="allow";changed["proposal_hash"]=DecisionProtocol.Digest(JsonSerializer.SerializeToElement(changed["payload"]));
            Denied(()=>store.BindServerProposal(session,changed.ToJsonString(),review.ToJsonString(),now),"classification tamper");
            Denied(()=>store.Authorize(p.Id,p.Hash,"admin_public",true,"approve",now),"missing Host identity");
            store.Authorize(p.Id,p.Hash,"admin_public",true,"approve",now,new string('a',64));
            var applyCount=0;var statusCount=0;JsonElement sent=default;string? sentHash=null;
            using var gateway=new GatewayClient(new Uri("https://public-fixture.invalid"),new Handler(async r=>{
                Check(RestrictedExecution.Active&&r.Headers.GetValues("X-LocalBrain-Approval-Owner").Single()==DecisionProtocol.Owner,"restricted Host transport identity");
                var text=await r.Content!.ReadAsStringAsync();var body=DecisionProtocol.Parse(text);
                if(r.RequestUri!.AbsolutePath=="/v1/slack/apply"){
                    applyCount++;sent=body;sentHash=DecisionProtocol.Digest(sent);
                    var approval=JsonNode.Parse(body.GetProperty("approval").GetRawText())!.AsObject();var receipt=approval["receipt_hash"]!.GetValue<string>();approval.Remove("receipt_hash");
                    Check(DecisionProtocol.Digest(JsonSerializer.SerializeToElement(approval))==receipt,"human receipt canonical hash");
                    Check(body.GetProperty("approval").GetProperty("host_certificate_sha256").GetString()==new string('a',64),"actual Host certificate binding");
                    throw new HttpRequestException("PUBLIC simulated lost apply response");
                }
                statusCount++;Check(body.GetProperty("request_hash").GetString()==sentHash,"read-only status binds exact reservation");
                var result=example.GetProperty("apply_response");var resultNode=JsonNode.Parse(result.GetRawText())!.AsObject();resultNode["proposal_hash"]=p.Hash;
                return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{state="applied",request_hash=sentHash,result=resultNode,retry_safe=false}))};
            }));
            var worker=new SlackApplicationWorker(store,gateway);await worker.Step(CancellationToken.None);
            Check(applyCount==1&&store.UncertainApplications().Length==1,"ambiguous send retained");store.Recover();
            await worker.Step(CancellationToken.None);await worker.Step(CancellationToken.None);
            Check(applyCount==1&&statusCount==1&&store.Proposals().Single().State=="applied","restart reconciles without second apply");
            Check(store.UncertainApplications().Length==0,"bound terminal receipt persisted");
            Denied(()=>store.Authorize(p.Id,p.Hash,"admin_public",true,"approve",now,new string('a',64)),"no approval replay");
            using(RestrictedExecution.EnterSlack()){
                try{await gateway.JsonAsync(HttpMethod.Post,"/v1/brain/search","{}",CancellationToken.None);throw new Exception("ordinary transport used");}catch(UnauthorizedAccessException){checks++;}
            }
            Check(applyCount==1,"no generic transport bypass");
            var cancels=0;
            using(var cancelledGateway=new GatewayClient(new Uri("https://public-fixture.invalid"),new Handler(r=>{
                if(r.RequestUri!.AbsolutePath=="/v1/slack/chat")throw new OperationCanceledException("PUBLIC foreground preemption");
                Check(r.RequestUri.AbsolutePath=="/v1/slack/cancel","preemption uses authenticated cancel only");cancels++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"cancelled\":true}")});
            })))using(RestrictedExecution.EnterSlack()){
                try{await new SlackLocalInference(cancelledGateway,store).Chat("cancel_fixture_01",session,"","PUBLIC",64,5,CancellationToken.None);throw new Exception("preempted result accepted");}catch(OperationCanceledException){checks++;}
                Check(cancels==1,"one bounded remote cancel after preemption");
            }
            var unknown=JsonNode.Parse(proposal.ToJsonString())!.AsObject();unknown["payload"]!["proposal_id"]="proposal_unknown_fixture";
            unknown["proposal_hash"]=DecisionProtocol.Digest(JsonSerializer.SerializeToElement(unknown["payload"]));
            var u=store.BindServerProposal(session,unknown.ToJsonString(),review.ToJsonString(),now);
            store.Authorize(u.Id,u.Hash,"admin_public",true,"approve",now,new string('a',64));
            var countUnknown=0;
            using var unknownGateway=new GatewayClient(new Uri("https://public-fixture.invalid"),new Handler(r=>{
                if(r.RequestUri!.AbsolutePath.EndsWith("apply")){countUnknown++;throw new HttpRequestException("PUBLIC uncertain send");}
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"state\":\"unknown\",\"retry_safe\":false}")});
            }));
            var unknownWorker=new SlackApplicationWorker(store,unknownGateway);await unknownWorker.Step(CancellationToken.None);await unknownWorker.Step(CancellationToken.None);await unknownWorker.Step(CancellationToken.None);
            Check(countUnknown==1&&store.Proposals().Single(p=>p.Id==u.Id).State=="apply_unresolved","unknown is HOLD, never retry");
            Check(store.Cleanup(now.AddDays(40),false)==0,"unresolved application and live job protect history");
            Denied(()=>store.Authorize(u.Id,u.Hash,"admin_public",true,"approve",now,new string('a',64)),"unknown cannot approve again");
            var expiring=JsonNode.Parse(proposal.ToJsonString())!.AsObject();expiring["payload"]!["proposal_id"]="proposal_expiry_fixture";expiring["proposal_hash"]=DecisionProtocol.Digest(JsonSerializer.SerializeToElement(expiring["payload"]));
            var e=store.BindServerProposal(session,expiring.ToJsonString(),review.ToJsonString(),now);store.Authorize(e.Id,e.Hash,"admin_public",true,"approve",now,new string('a',64));
            Check(store.ClaimApplication(now.AddMinutes(6)) is null&&store.Proposals().Single(p=>p.Id==e.Id).State=="approval_expired","dispatch never renews expired approval");
        }
        {
            var root=Directory.CreateTempSubdirectory("slack-retirement-public-").FullName;var old=DateTimeOffset.UtcNow.AddDays(-31);
            using var store=new SlackStore(new(true,root,"TTEST","CTEST","UBOT",Path.Combine(root,"a.token"),Path.Combine(root,"b.token")));
            var m=new SlackMention("EV-OLD","TTEST","CTEST","UALICE",old.ToUnixTimeSeconds()+".000000",old.ToUnixTimeSeconds()+".000000","PUBLIC",[],old);
            var j=store.GetJob(store.Accept(m,old.AddSeconds(-2),old).JobId!);var r=store.ClaimReply(DateTimeOffset.UtcNow)!;store.ReplySent(r.Id,"1700000000.000001");store.ClaimNext();store.Finish(j.Id,"PUBLIC",true,false);
            r=store.ClaimReply(DateTimeOffset.UtcNow)!;store.ReplySent(r.Id,"1700000000.000002");
            store.Propose(j.SessionId,"knowledge","PUBLIC expired candidate","PUBLIC evidence","PUBLIC review",old);
            Check(store.Cleanup(DateTimeOffset.UtcNow,false)==1&&store.Proposals().Length==0,"expiry erases unadopted candidate content");
            Check(store.PendingRetirements().Single().Session==j.SessionId,"session erase atomically queues remote retirement");
            var requests=new List<string>();using var g=new GatewayClient(new Uri("https://public-fixture.invalid"),new Handler(async req=>{
                Check(req.RequestUri!.AbsolutePath=="/v1/slack/retire","retirement never inference or apply");var body=DecisionProtocol.Parse(await req.Content!.ReadAsStringAsync());requests.Add(body.GetProperty("request_id").GetString()!);
                if(requests.Count==1)throw new HttpRequestException("PUBLIC lost cleanup response");
                if(requests.Count==2)return new(HttpStatusCode.OK){Content=new StringContent(retirement.GetProperty("deferred_response").GetRawText())};
                var response=JsonNode.Parse(retirement.GetProperty("retired_response").GetRawText())!.AsObject();response["session_id"]=body.GetProperty("session_id").GetString();response["request_id"]=body.GetProperty("request_id").GetString();
                return new(HttpStatusCode.OK){Content=new StringContent(response.ToJsonString())};
            }));
            var worker=new SlackApplicationWorker(store,g);await worker.RetireStep(CancellationToken.None);Check(store.PendingRetirements().Length==1,"cleanup unknown remains durable");store.Recover();
            await worker.RetireStep(CancellationToken.None);Check(store.PendingRetirements().Length==1,"busy Server defers without false retirement");await worker.RetireStep(CancellationToken.None);await worker.RetireStep(CancellationToken.None);
            Check(requests.Count==3&&requests.Distinct().Count()==1&&store.PendingRetirements().Length==0,"only idempotent cleanup repeats exact identity");
        }
        Console.WriteLine(JsonSerializer.Serialize(new{slack_adoption_contract_checks=checks,status="PASS",real_slack=false,real_model=false,production_adoption=false,server_contract_commit="a9a911e6c847ff5d57c16e18441f75e11565c428",retirement_contract_commit="9733d1c30a5f4f0bf65afd8e8fd1d662f6746b2b"}));
    }
}
