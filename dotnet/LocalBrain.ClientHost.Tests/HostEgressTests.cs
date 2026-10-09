using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class HostEgressTests
{
    private static void Check(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
    private static void Reject(Action action,string reason){try{action();}catch(Exception e)when(e is UnauthorizedAccessException or InvalidDataException or IOException){return;}throw new InvalidOperationException(reason);}
    public static void Run() {
        var parent=Path.Combine(Path.GetTempPath(),"localbrain-egress-tests",Guid.NewGuid().ToString("N"));var root=Path.Combine(parent,"repo");Directory.CreateDirectory(root);
        using var store=new CanonicalStore(Path.Combine(parent,"host"));
        var request=AgentTaskRequest.Parse("""{"requirement":"Fix fixture","acceptance_criteria":["line ends with LF"],"risk":"LOW","approved_high_risk":false,"allowed_files":["greeting.txt"],"required_tests":[{"kind":"git_diff_check"}]}""");
        using var journal=new HostTaskJournal(store,root,"task_a",request,"baseline");
        using(var other=new HostTaskJournal(store,root,"task_b",request,"baseline"))other.Record("host_prompt",new{data="other task"},"host_verified");
        var blob=store.PutEvidence(Encoding.UTF8.GetBytes("Fixture password=synthetic_value_12345 nonce=synthetic_capability_12345"));
        var receipt=store.Append(new("task_a",HostTaskJournal.WorkspaceId(root),"windows_host","fixture","host_approved_export_input","execution_evidence","host_verified",[blob],JsonSerializer.SerializeToElement(new{approved_source_ids=new[]{"source_a"},approved_file_refs=new[]{new HostEgress.FileRef("greeting.txt",blob),new HostEgress.FileRef("Bar.cs",blob)}})));
        var controller=new HostEgress(store);
        HostEgress.Prepared Prepare()=>controller.Prepare("task_a",[receipt.EventId],["source_a"],[new("greeting.txt",blob)],[],true);
        Reject(()=>Prepare(),"Default deny bypassed");
        var policy=new CanonicalStore.Policy(HostTaskJournal.RepositoryId(root),root,"auto",["greeting.txt"],["task_evidence_only"]);
        store.ChangePolicies([policy],"fixture_human");
        var export=Prepare();Check(export.Manifest.RedactionCount==2 && export.Manifest.ParityDeviation.Contains("security_redaction_changed_information_conditions"),"Missing redaction/parity audit");
        Check(!export.Payload.Contains("synthetic_value") && !export.Payload.Contains("synthetic_capability"),"Secret/capability leaked");
        Check(export.Manifest.ExportedSourceIds.SequenceEqual(new[]{"source_a"}) && export.Manifest.WriteScope.SequenceEqual(new[]{"greeting.txt"}),"Explicit scopes or sources lost");
        Reject(()=>controller.Prepare("task_a",[store.EventIds("task_b").Last()],[],[],[]),"Cross-task event exported");
        Reject(()=>controller.Prepare("task_a",[receipt.EventId],["uncited"],[],[]),"Uncited source exported");
        foreach(var path in new[]{"../greeting.txt","greeting.txt ","greeting.txt:secret",".git/config",".env","model.gguf","other.txt"})
            Reject(()=>controller.Prepare("task_a",[receipt.EventId],[],[new(path,blob)],[]),"Export path escaped scope");
        var sibling=Path.Combine(parent,"repo-other");Directory.CreateDirectory(sibling);Check(!CanonicalStore.Within(root,sibling),"Prefix boundary allowed sibling");
        Reject(()=>controller.AuthorizeDispatch(export with {Payload=export.Payload+"changed"},"fixture"),"Payload mutation accepted");
        using(controller.AuthorizeDispatch(export,"fixture")) {
            Reject(()=>store.ChangePolicies([policy with {Mode="deny"}],"fixture_human"),"Policy changed during dispatch lease");
        }
        Reject(()=>controller.AuthorizeDispatch(export,"fixture"),"Same dispatch replayed");
        var stale=Prepare();store.ChangePolicies([policy with{Mode="deny"}],"fixture_human");
        Reject(()=>controller.AuthorizeDispatch(stale,"fixture"),"Policy drift accepted");
        store.ChangePolicies([policy with{Mode="manual"}],"fixture_human");var manual=Prepare();
        Reject(()=>controller.AuthorizeDispatch(manual,"fixture"),"Manual export sent without approval");
        Reject(()=>store.BindExportApproval("task_a",manual.ManifestHash,manual.Manifest.PolicyHash,DateTimeOffset.UtcNow.AddSeconds(-1),"human"),"Expired approval bound");
        Reject(()=>store.BindExportApproval("task_a",CanonicalStore.Hash(Encoding.UTF8.GetBytes("other")),manual.Manifest.PolicyHash,DateTimeOffset.UtcNow.AddMinutes(1),"human"),"Unknown manifest approved");
        store.BindExportApproval("task_a",manual.ManifestHash,manual.Manifest.PolicyHash,DateTimeOffset.UtcNow.AddMinutes(1).ToOffset(TimeSpan.FromHours(9)),"fixture_human");
        using(controller.AuthorizeDispatch(manual,"fixture")){}
        Reject(()=>controller.AuthorizeDispatch(manual,"fixture"),"Single-use approval replayed");
        Reject(()=>journal.BeginExternalPacket("Public deterministic packet","sol",[],["greeting.txt"]),"Journal manual dispatch did not wait");
        var pending=store.PendingPacket("task_a",store.RequireExportPolicy("task_a").Hash,"sol",CanonicalStore.Hash(Encoding.UTF8.GetBytes("Public deterministic packet")),[],["greeting.txt"])??throw new Exception("Pending packet lost");
        store.BindExportApproval("task_a",pending.ManifestHash,pending.Manifest.PolicyHash,DateTimeOffset.UtcNow.AddMinutes(1),"fixture_human");
        using(var resumed=journal.BeginExternalPacket("Public deterministic packet","sol",[],["greeting.txt"]))Check(resumed.Packet=="Public deterministic packet","Resumed different approved bytes");
        store.ChangePolicies([policy with{WriteScope=[],Mode="auto"}],"fixture_human");
        var readOnlyExport=controller.Prepare("task_a",[receipt.EventId],[],[new("Bar.cs",blob)],[]);
        using(controller.AuthorizeDispatch(readOnlyExport,"fixture_read_only")){}
        var actualKeys=HostEgress.Redact("{\"LOCALBRAIN_SIDEFX_NONCE\":\"synthetic_nonce_value_123456\",\"LOCALBRAIN_SIDEFX_PIPE\":\"localbrain-sidefx-synthetic123456\"}");
        Check(actualKeys.Count==2 && !actualKeys.Text.Contains("synthetic_nonce_value") && !actualKeys.Text.Contains("localbrain-sidefx-synthetic"),"Actual capability keys leaked");
        var nested=JsonSerializer.Serialize(new{payload=JsonSerializer.Serialize(new{LOCALBRAIN_SIDEFX_NONCE="synthetic_nonce_value_123456",LOCALBRAIN_SIDEFX_PIPE="localbrain-sidefx-synthetic123456"})});
        var nestedSafe=HostEgress.Redact(nested);Check(nestedSafe.Count==2 && !nestedSafe.Text.Contains("synthetic_nonce_value") && !nestedSafe.Text.Contains("localbrain-sidefx-synthetic"),"Serialized provider/tool JSON capability leaked");
        foreach(var escaped in new[]{"\\\"LOCALBRAIN_SIDEFX_NONCE\\\":\\\"synthetic_nonce_value_123456\\\"","\\u0022LOCALBRAIN_SIDEFX_NONCE\\u0022:\\u0022synthetic_nonce_value_123456\\u0022"})
            Check(!HostEgress.Redact(escaped).Text.Contains("synthetic_nonce_value"),"Escaped source capability leaked");
        foreach(var key in new[]{"SERVICE_API_KEY","AZURE_CLIENT_SECRET","DB_PASSWORD"}) {
            const string secret="synthetic_prefixed_value_abc123";
            var structured=JsonSerializer.Serialize(new Dictionary<string,string>{{key,secret}});
            var nestedCredential=JsonSerializer.Serialize(new{payload=JsonSerializer.Serialize(new{packet=structured})});
            foreach(var text in new[]{key+"="+secret,structured,nestedCredential,structured.Replace("\"","\\\""),structured.Replace("\"","\\u0022")}) {
                Check(ReviewerEvidence.ContainsSecret(text),"Final detector missed a prefixed credential");
                var safe=HostEgress.Redact(text);
                Check(safe.Count>0 && !safe.Text.Contains(secret) && !ReviewerEvidence.ContainsSecret(safe.Text),"Prefixed credential survived redaction/final check");
            }
        }
        foreach(var quoted in new[]{"DB_PASSWORD=\"synthetic head, synthetic_tail_abc123\"","const value = {DB_PASSWORD:'synthetic head, synthetic_tail_abc123'};","DB_PASSWORD=\"synthetic \\\"quote\\\" synthetic_tail_abc123\""}) {
            var safe=HostEgress.Redact(quoted);Check(safe.Count>0 && !safe.Text.Contains("synthetic"),"Quoted sensitive tail survived opaque-line redaction");
        }
        foreach(var ambiguous in new[]{"DB_PASSWORD=\"synthetic head\nsynthetic_tail_abc123\"","DB_PASSWORD='''synthetic\nsynthetic_tail_abc123'''","DB_PASSWORD: |\n  synthetic_tail_abc123","DB_PASSWORD=`synthetic\nsynthetic_tail_abc123`","DB_PASSWORD=synthetic\\\nsynthetic_tail_abc123"})
            Check(HostEgress.Redact(ambiguous).Text=="[REDACTED:sensitive_fragment]","Ambiguous multiline sensitive source fragment was not entirely removed");
        foreach(var opaque in new[]{"const DB_PASSWORD =\n  \"synthetic_tail_abc123\";","DB_PASSWORD:\n  synthetic_tail_abc123","const DB_PASSWORD = /* comment */\n  \"synthetic_tail_abc123\";","const DB_PASSWORD = [\"synthetic_head\",\n  \"synthetic_tail_abc123\"];"}) {
            var safe=HostEgress.Redact(opaque);Check(safe.Text=="[REDACTED:sensitive_fragment]" && safe.Classes.Contains("opaque_sensitive_fragment_removed"),"Opaque sensitive fragment leaked after assignment-line removal");
        }
        foreach(var separated in new[]{"const LOCALBRAIN_SIDEFX_NONCE\n = \"synthetic_capability_tail_abc123\";","const DB_PASSWORD /* comment */\n = \"synthetic_tail_abc123\";","const LOCALBRAIN_SIDEFX_\\u004eONCE\n = \"synthetic_capability_tail_abc123\";"})
            Check(HostEgress.Redact(separated).Text=="[REDACTED:sensitive_fragment]","Separated/escaped sensitive key leaked its opaque fragment");
        Check(ReviewerEvidence.ContainsSecret("LOCALBRAIN_SIDEFX_NONCE\n = \"synthetic_capability_tail_abc123\""),"Final reject guard missed a capability assignment");
        var unaudited=Prepare();File.AppendAllText(Path.Combine(store.Root,"codex-egress-policies.json")," ");
        Reject(()=>controller.AuthorizeDispatch(unaudited,"fixture"),"Unaudited policy bytes accepted");
        var redacted=HostEgress.Redact("-----BEGIN PRIVATE KEY-----\nSYNTHETIC_ONLY\n-----END PRIVATE KEY-----");Check(redacted.Count==1 && !redacted.Text.Contains("SYNTHETIC_ONLY"),"PEM body leaked");
        Console.WriteLine("Host egress: deny/auto/manual, exact manifests, canonical source scope, redaction, drift, lease and replay checks passed.");
    }
}