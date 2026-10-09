using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class DifferentialLearningTests
{
    private static void Check(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
    private static void Reject(Action action,string reason){try{action();}catch(Exception e)when(e is UnauthorizedAccessException or InvalidDataException or JsonException or IOException){return;}throw new InvalidOperationException(reason);}
    internal static async Task RunAsync() {
        var root=Path.Combine(Path.GetTempPath(),"localbrain-learning-tests",Guid.NewGuid().ToString("N"));var fixture=await HostRescueTests.PrepareAsync(root);
        using var store=new CanonicalStore(fixture.HostRoot);using var journal=new HostTaskJournal(store,fixture.MainRoot,fixture.TaskId,HostRescueTests.Request,fixture.Head);
        var rescue=new HostRescue(journal);var candidate=await rescue.ImportAsync(fixture.TicketHash,fixture.Candidate);var learning=new DifferentialLearning(journal);
        var validation=new HostRescue.Validation(fixture.TicketHash,candidate,"","",[],"PASS",true);var hash=store.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(validation));
        Reject(()=>learning.Assemble(fixture.TicketHash,hash),"Unverified validation became a learning packet");
        Reject(()=>learning.SaveClaims(hash,"{\"cause\":\"a\",\"trigger\":\"b\",\"action\":\"c\"}"),"Unregistered packet accepted");
        foreach(var text in new[]{"{\"cause\":\"a\",\"trigger\":\"b\",\"action\":\"c\",\"approved\":true}","{\"cause\":\"a\",\"cause\":\"b\",\"trigger\":\"b\",\"action\":\"c\"}","{\"cause\":true,\"trigger\":\"b\",\"action\":\"c\"}","prose {\"cause\":\"a\",\"trigger\":\"b\",\"action\":\"c\"}"})Reject(()=>DifferentialLearning.ParseClaims(text),"Invalid learning claims accepted");
        var redacted=DifferentialLearning.ParseClaims("{\"cause\":\"SERVICE_API_KEY=secretvalue\",\"trigger\":\"unknown\",\"action\":\"verify locally\"}");Check(!redacted.cause.Contains("secretvalue",StringComparison.Ordinal),"Learning claim leaked sensitive opaque text");
        Console.WriteLine("Differential learning: unverified source/unregistered packet/duplicate/extra/non-text claims rejection and sensitive text redaction PASS");
    }
    internal static async Task PreservedAsync(string fixtureRoot,string outputRoot,bool infer) {
        fixtureRoot=Path.GetFullPath(fixtureRoot);outputRoot=Path.GetFullPath(outputRoot);if(Directory.Exists(outputRoot))throw new IOException("Preserved-evidence output must be new");Directory.CreateDirectory(outputRoot);
        var fixture=JsonSerializer.Deserialize<HostRescueTests.Fixture>(File.ReadAllText(Path.Combine(fixtureRoot,"fixture.json")))!;
        Check(fixture.ValidationHash is not null,"Preserved fixture is unvalidated");
        string OriginalState()=>CanonicalStore.Hash(Encoding.UTF8.GetBytes(string.Join("\n",Directory.EnumerateFiles(fixture.HostRoot,"*",SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(Path.Combine(fixture.HostRoot,"evidence"),"*.blob")).Order(StringComparer.Ordinal).Select(p=>p+":"+CanonicalStore.Hash(File.ReadAllBytes(p))))));
        var originalState=OriginalState();
        // Work only on a new local copy of the already quiescent canonical store. Original immutable evidence stays unchanged.
        var host=Path.Combine(outputRoot,"host");Directory.CreateDirectory(host);Directory.CreateDirectory(Path.Combine(host,"evidence"));
        foreach(var name in new[]{"canonical.db","canonical.db-wal","canonical.db-shm","policy.json"}) {var path=Path.Combine(fixture.HostRoot,name);if(File.Exists(path))File.Copy(path,Path.Combine(host,name));}
        foreach(var path in Directory.EnumerateFiles(Path.Combine(fixture.HostRoot,"evidence"),"*.blob"))File.Copy(path,Path.Combine(host,"evidence",Path.GetFileName(path)));
        string packetHash;string? claimsHash=null;
        using(var store=new CanonicalStore(host))using(var journal=new HostTaskJournal(store,fixture.MainRoot,fixture.TaskId,HostRescueTests.Request,fixture.Head)) {
            var learning=new DifferentialLearning(journal);packetHash=learning.Assemble(fixture.TicketHash,fixture.ValidationHash!);var packet=learning.RequirePacket(packetHash);
            Check(learning.Assemble(fixture.TicketHash,fixture.ValidationHash!)==packetHash,"Same immutable input changed packet identity");
            var ticket=new HostRescue(journal).Load(fixture.TicketHash);
            var start=store.ReadTaskEvent(fixture.TaskId,ticket.StartingEventId);
            store.Append(start.Input with{IdempotencyKey="duplicate_start_learning_test",Metadata=JsonSerializer.SerializeToElement(new{attempt=999,base_commit=ticket.BaseCommit,starting_state_hash=ticket.StartingStateHash,starting_diff_hash=ticket.StartingDiffHash})});
            Check(learning.Assemble(fixture.TicketHash,fixture.ValidationHash!)==packetHash,"Repeated identical state selected a different starting attempt");
            Check(packet.starting_state_hash==fixture.Candidate.StartingStateHash && packet.starting_diff_hash==fixture.Candidate.StartingDiffHash && packet.failure_diff_hash!=packet.starting_diff_hash,"Starting diff was confused with failed diff");
            Check(packet.changes.Length==3 && packet.changes.All(d=>d.observed_failure_hash!=d.verified_result_hash),"Exact synthetic failed/result files were not compared");
            Check(packet.sources.Any(s=>s.verification_status=="untrusted_candidate") && packet.adoption_status=="QUARANTINED_NOT_ADOPTED" && packet.candidate_provenance.Contains("not established",StringComparison.Ordinal),"Candidate origin or truth authority was upgraded");
            var ids=packet.sources.Select(s=>"event:"+s.event_id).Concat(packet.evidence_hashes.Select(h=>"blob:"+h));Check(packet.constituent_source_ids.SequenceEqual(ids,StringComparer.Ordinal),"Manifest closure IDs differ");
            Check(packet.external_export_id.Length>0 && packet.original_candidate_hash.Length==64,"Export/original provenance absent");
            File.WriteAllBytes(Path.Combine(outputRoot,"packet.json"),store.ReadEvidence(packetHash));
            var other="unrelated_learning_"+Guid.NewGuid().ToString("N");using(var foreignJournal=new HostTaskJournal(store,fixture.MainRoot,other,HostRescueTests.Request,fixture.Head)) {
                Reject(()=>new DifferentialLearning(foreignJournal).RequirePacket(packetHash),"Foreign task recalled learning packet");
                Reject(()=>new DifferentialLearning(foreignJournal).Assemble(fixture.TicketHash,fixture.ValidationHash!),"Foreign task assembled learning packet");
            }
            var path=Path.Combine(host,"evidence",packet.failure_diff_hash+".blob");var bytes=File.ReadAllBytes(path);File.SetAttributes(path,FileAttributes.Normal);File.WriteAllText(path,"TAMPERED",new UTF8Encoding(false));
            Reject(()=>learning.RequirePacket(packetHash),"Altered constituent blob accepted");File.WriteAllBytes(path,bytes);File.SetAttributes(path,FileAttributes.ReadOnly);
            Reject(()=>learning.SaveClaims(packetHash,"Non-JSON untrusted test response"),"Non-JSON output became a learning candidate");
            var responses=store.EventIds(fixture.TaskId).Select(id=>store.ReadTaskEvent(fixture.TaskId,id)).ToArray();
            Check(responses.Any(e=>e.Input.EventType=="differential_learning_model_response" && e.Input.VerificationStatus=="model_claim") && responses.Any(e=>e.Input.EventType=="differential_learning_claims_rejected"),"Rejected model response was not retained separately from Host parser facts");
            if(infer)claimsHash=await learning.ExtractAsync(fixture.TicketHash,fixture.ValidationHash!);
            else claimsHash=learning.SaveClaims(packetHash,"{\"cause\":\"Synthetic test claim; causality unproven\",\"trigger\":\"Synthetic failed bytes differ\",\"action\":\"Check actual test and diff evidence before reuse\"}");
            var candidate=JsonSerializer.Deserialize<DifferentialLearning.Candidate>(store.ReadEvidence(claimsHash))!;
            Check(candidate.verification_status=="model_claim" && candidate.adoption_status=="QUARANTINED_NOT_ADOPTED","Learning claims promoted to truth");File.WriteAllBytes(Path.Combine(outputRoot,"candidate.json"),store.ReadEvidence(claimsHash));
        }
        using(var store=new CanonicalStore(host))using(var journal=new HostTaskJournal(store,fixture.MainRoot,fixture.TaskId,HostRescueTests.Request,fixture.Head)) {
            var learning=new DifferentialLearning(journal);Check(learning.Assemble(fixture.TicketHash,fixture.ValidationHash!)==packetHash,"Host restart changed immutable packet");_=learning.RequirePacket(packetHash);
            var ticket=new HostRescue(journal).Load(fixture.TicketHash);var request=HostRescueTests.Request;
            store.ReserveAssignment(new(fixture.TaskId,"learning_stale_"+Guid.NewGuid().ToString("N"),ticket.WorkerId,ticket.WorkspaceId,HostTaskJournal.RepositoryId(fixture.MainRoot),ticket.BaseCommit,ticket.StartingStateHash,ticket.StartingDiffHash,request.AllowedFiles,[],request.Requirement,request.AcceptanceCriteria,request.RequiredTests,[],2));
            Reject(()=>learning.Assemble(fixture.TicketHash,fixture.ValidationHash!),"Stale assignment generated a new learning packet");_=learning.RequirePacket(packetHash);
        }
        Check(OriginalState()==originalState,"Original preserved canonical store changed");
        Console.WriteLine(JsonSerializer.Serialize(new{status="PASS_PRESERVED_HOST_EVIDENCE_LEARNING_PACKET",fixture.TaskId,packet_hash=packetHash,claims_hash=claimsHash,local_model_inference=infer,original_store_unchanged=true,closure_same_task_verified=true,idempotence_restart_verified=true,tampered_blob_rejected=true,cross_task_rejected=true,stale_assignment_rejected=true,repeated_starting_state_disambiguated=true,original_store_hash=originalState,adoption_status="QUARANTINED_NOT_ADOPTED",provenance="Original Phase5 Host Git/WSL tests and Critic actual; failing state and candidate synthetic; no external Codex authorship established"}));
    }
}
