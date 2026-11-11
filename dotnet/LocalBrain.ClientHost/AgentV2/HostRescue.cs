using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalBrain.ClientHost.AgentV2;

// Durable Git/task/event/result transport; no undocumented remote API or repository-capable cloud process.
internal sealed class HostRescue(HostTaskJournal journal,Action<string>? fault=null)
{
    internal sealed record Ticket(string TaskId,string AssignmentId,int Generation,string WorkerId,string WorkspaceId,
        string MainRoot,string RescueRoot,string BaseCommit,string StartingStateHash,string StartingDiffHash,
        string StartingEventId,string FailureStateHash,string ExportManifestHash,string ExportPayloadHash);
    internal sealed record Change(string Path,string? BeforeHash,string Content);
    internal sealed record Candidate(string TaskId,string AssignmentId,int Generation,string BaseCommit,
        string StartingStateHash,string StartingDiffHash,Change[] Changes,string Summary);
    internal sealed record Validation(string TicketHash,string CandidateHash,string ResultStateHash,string ResultDiffHash,
        ToolRouter.TestResult[] Tests,string CriticVerdict,bool ExternalPassed);
    internal sealed record FailureObservation(int ActorTurns,bool ActorReportedNoProgress,int TestsWithoutImprovement,
        string SupervisorAction,string[] FailedRequiredTests,string Reason);
    private CanonicalStore Store=>journal.Canonical;
    private AgentTaskRequest Request=>AgentTaskRequest.Parse(Store.TaskRequest(journal.TaskId));
    private CanonicalStore.EventReceipt Record(string type,object data,string[] refs,string verification="host_verified",string? workspace=null,string? key=null) =>
        Store.Append(new(journal.TaskId,workspace??HostTaskJournal.WorkspaceId(journal.WindowsRoot),"windows_host",key??Guid.NewGuid().ToString("N"),type,"codex_rescue",verification,refs,JsonSerializer.SerializeToElement(data)));
    private CanonicalStore.StoredEvent[] Events(string type)=>Store.EventIds(journal.TaskId).Select(id=>Store.ReadTaskEvent(journal.TaskId,id)).Where(e=>e.Input.EventType==type && e.Input.Producer=="windows_host").ToArray();
    private static bool MatchesFinal(AttemptStartingState.Snapshot current,AttemptStartingState.Snapshot target)=>current.Head==target.Head && current.IndexTree==target.IndexTree && current.Files.SequenceEqual(target.Files);
    private static bool MatchesPartial(AttemptStartingState.Snapshot current,AttemptStartingState.Snapshot initial,AttemptStartingState.Snapshot target,HashSet<string> scope) {
        if(current.Head!=initial.Head || current.IndexTree!=initial.IndexTree || target.Head!=initial.Head || target.IndexTree!=initial.IndexTree)return false;
        var old=initial.Files.ToDictionary(f=>f.Path,f=>f.Hash,StringComparer.OrdinalIgnoreCase);var desired=target.Files.ToDictionary(f=>f.Path,f=>f.Hash,StringComparer.OrdinalIgnoreCase);var actual=current.Files.ToDictionary(f=>f.Path,f=>f.Hash,StringComparer.OrdinalIgnoreCase);
        foreach(var path in old.Keys.Concat(desired.Keys).Concat(actual.Keys).Distinct(StringComparer.OrdinalIgnoreCase)) {
            old.TryGetValue(path,out var before);desired.TryGetValue(path,out var after);actual.TryGetValue(path,out var now);
            if(scope.Contains(path)){if(now!=before && now!=after)return false;}else if(now!=before || after!=before)return false;
        }
        return true;
    }
    private void RequireTicket(Ticket ticket,string hash) {
        if(ticket.TaskId!=journal.TaskId || !string.Equals(ticket.MainRoot,journal.WindowsRoot,StringComparison.OrdinalIgnoreCase) ||
            CanonicalStore.Hash(JsonSerializer.SerializeToUtf8Bytes(ticket))!=hash || !Store.TaskEvidence(journal.TaskId).Contains(hash,StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Unregistered or changed rescue ticket");
        Store.RequireCurrentAssignment(ticket.TaskId,ticket.AssignmentId,ticket.Generation,ticket.WorkspaceId);
        var workspace=Store.GetWorkspace(ticket.WorkspaceId);
        if(workspace.Role!="codex_rescue" || workspace.WorkspaceRoot!=ticket.RescueRoot || workspace.BaseCommit!=ticket.BaseCommit ||
            workspace.RepositoryId!=HostTaskJournal.RepositoryId(journal.WindowsRoot))throw new UnauthorizedAccessException("Rescue worktree identity drift");
        CanonicalStore.GuardPath(ticket.RescueRoot);
        if(CanonicalStore.Within(ticket.MainRoot,ticket.RescueRoot) || CanonicalStore.Within(ticket.RescueRoot,ticket.MainRoot))throw new UnauthorizedAccessException("Rescue is not independent");
    }
    internal Ticket Load(string hash) {
        var ticket=JsonSerializer.Deserialize<Ticket>(Store.ReadEvidence(hash))??throw new InvalidDataException("Invalid rescue ticket");RequireTicket(ticket,hash);return ticket;
    }
    internal async Task<string> ReserveAsync(AttemptStartingState.Saved starting,CancellationToken token=default,
        FailureObservation? observation=null) {
        Slack.RestrictedExecution.DenyExternal();
        var exportPolicy=Store.RequireExportPolicy(journal.TaskId).Policy;journal.RequireMutationApproval();
        var source=Store.ReadTaskEvent(journal.TaskId,starting.EventId);
        if(source.Input.EventType!="attempt_starting_state" || source.Input.Producer!="windows_host" || !source.Input.EvidenceRefs.Contains(starting.StateHash) ||
            AttemptStartingState.Hash(starting.State)!=starting.StateHash || starting.State.Head!=journal.ExpectedHead)
            throw new UnauthorizedAccessException("Rescue needs a canonical pre-attempt starting state");
        var failure=await AttemptStartingState.CaptureAsync(journal.WindowsRoot,Store,token);var failureHash=Store.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(failure));
        if(failure.Head!=starting.State.Head)throw new UnauthorizedAccessException("Failure HEAD changed");
        var snapshot=await GitEvidence.CaptureAsync(journal.WindowsRoot,Request.AllowedFiles,token);
        if(snapshot.UnexpectedFiles.Length!=0)throw new UnauthorizedAccessException("Failure changed outside write_scope");
        var startingFiles=starting.State.Files.Where(f=>Request.AllowedFiles.Contains(f.Path,StringComparer.OrdinalIgnoreCase)).ToArray();
        var sources=new List<object>();
        var sourceBytes=0;
        foreach(var file in startingFiles) {
            if(file.Hash is null || exportPolicy.Mode!="manual" ||
                !exportPolicy.ExportScope.Contains("file:"+file.Path,StringComparer.OrdinalIgnoreCase))continue;
            var bytes=Store.ReadEvidence(file.Hash);
            if(bytes.Length>24_000-sourceBytes)throw new InvalidDataException("Rescue source selection exceeds budget");
            sourceBytes+=bytes.Length;
            sources.Add(new{file.Path,file.Hash,Content=new UTF8Encoding(false,true).GetString(bytes)});
        }
        var rescueId=Guid.NewGuid().ToString("N");var root=Path.Combine(Store.Root,"rescue-worktrees",rescueId);
        await AttemptStartingState.ReconstructAsync(journal.WindowsRoot,root,starting.State,Store,token);
        var workspace="rescue_"+rescueId;var worker="codex_protocol";var assignment="rescue_"+Guid.NewGuid().ToString("N");
        Store.RegisterWorkspace(new(HostTaskJournal.RepositoryId(journal.WindowsRoot),workspace,journal.WindowsRoot,root,"codex_rescue",starting.State.Head));
        Store.RegisterWorker(worker,["candidate_patch","host_export_result_protocol"]);
        var spec=new CanonicalStore.TaskSpec(journal.TaskId,assignment,worker,workspace,HostTaskJournal.RepositoryId(journal.WindowsRoot),starting.State.Head,starting.StateHash,starting.State.StartingDiffHash,
            Request.AllowedFiles,[],Request.Requirement,Request.AcceptanceCriteria,Request.RequiredTests,[],1);
        var reserved=Store.ReserveAssignment(spec);
        // Only an explicit file export scope adds complete starting source.
        var packet=JsonSerializer.Serialize(new {protocol="localbrain-rescue-v1",requested_model=ReviewerCli.Model("sol"),
            task_id=journal.TaskId,assignment_id=assignment,generation=reserved.Generation,
            base_commit=starting.State.Head,starting_state_hash=starting.StateHash,starting_diff_hash=starting.State.StartingDiffHash,
            requirement=Request.Requirement,acceptance_criteria=Request.AcceptanceCriteria,write_scope=Request.AllowedFiles,required_tests=Request.RequiredTests,
            failed_diff=snapshot.ReviewDiff,failure_observation=observation,parity_deviation=starting.State.ParityDeviation,
            response_schema="Candidate{TaskId,AssignmentId,Generation,BaseCommit,StartingStateHash,StartingDiffHash,Changes[{Path,BeforeHash,Content}],Summary}. Return proposed full UTF8 file contents only; no shell commands. BeforeHash must equal the starting file hash, or null for a new file.",
            starting_write_files=startingFiles.Select(f=>new{f.Path,f.Hash}),starting_source=sources,
            locally_verified_tests_required=true,candidate_is_untrusted=true});
        var export=journal.PrepareExternalPacket(packet,"codex_protocol",files:Request.AllowedFiles);
        var ticket=new Ticket(journal.TaskId,assignment,reserved.Generation,worker,workspace,journal.WindowsRoot,root,starting.State.Head,starting.StateHash,starting.State.StartingDiffHash,
            starting.EventId,failureHash,export.ManifestHash,export.Manifest.ExportHash);
        var hash=Store.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(ticket));
        Record("codex_rescue_reserved",new{ticket_hash=hash,assignment,generation=reserved.Generation,starting_state_hash=starting.StateHash,starting_diff_hash=starting.State.StartingDiffHash,export_manifest_hash=export.ManifestHash,status="RESERVED_PROTOCOL_ONLY"},[hash,starting.StateHash,failureHash,export.ManifestHash,export.Manifest.ExportHash],workspace:workspace);
        if(observation is not null)
            Record("codex_rescue_qwen_stalled",new{ticket_hash=hash,observation},
                [hash,starting.StateHash,failureHash],key:assignment+"_qwen_stalled");
        var queue=Path.Combine(Store.Root,"rescue-queue");CanonicalStore.GuardPath(queue);Directory.CreateDirectory(queue);CanonicalStore.GuardPath(queue);
        var path=Path.Combine(queue,assignment+".json");using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){var bytes=JsonSerializer.SerializeToUtf8Bytes(new{ticket_hash=hash,status="RESERVED_PROTOCOL_ONLY"});file.Write(bytes);file.Flush(true);}
        return hash;
    }
    internal HostEgress.Prepared PreparedExport(Ticket ticket) {
        var manifest=JsonSerializer.Deserialize<HostEgress.Manifest>(Store.ReadEvidence(ticket.ExportManifestHash))!;
        if(manifest.TaskId!=ticket.TaskId || manifest.ExportHash!=ticket.ExportPayloadHash)throw new UnauthorizedAccessException("Rescue export binding drift");
        return new(manifest,ticket.ExportManifestHash,new UTF8Encoding(false,true).GetString(Store.ReadEvidence(ticket.ExportPayloadHash)));
    }
    internal sealed record ExternalResult(string Status,string? CandidateHash=null,string? ValidationHash=null,string? Reason=null);

    internal HostEgress.Prepared PrepareAdvice(string ticketHash) => PrepareRescueMode(ticketHash,"advice",
        "Return exactly {\"verdict\":\"GUIDANCE\",\"steps\":[\"concrete next step\"]} or {\"verdict\":\"UNRESOLVED\",\"steps\":[]}. Use only the enclosed data. Recommend at most three steps within the write scope and named tests. Do not use tools or edit files.");

    internal HostEgress.Prepared PrepareDirectEdit(string ticketHash) => PrepareRescueMode(ticketHash,"edit",
        "Use tools to make the actual source edits in your current working directory, which contains only the scoped source files. Modify only write_scope. Do not access other directories or run the project's tests here. The Host collects your edits and independently runs the required tests. Return a short plain-text summary of the changes.");

    private HostEgress.Prepared PrepareRescueMode(string ticketHash,string mode,string instructions)
    {
        Slack.RestrictedExecution.DenyExternal();
        var ticket=Load(ticketHash);
        journal.RequireMutationApproval();
        var eventType="codex_rescue_"+mode+"_prepared";
        var existing=Events(eventType).SingleOrDefault();
        if (existing is not null)
        {
            if (existing.Input.EvidenceRefs[0]!=ticketHash)
                throw new UnauthorizedAccessException("Sol packet was prepared for another rescue assignment");
            var existingHash=existing.Input.EvidenceRefs[1];
            var manifest=JsonSerializer.Deserialize<HostEgress.Manifest>(Store.ReadEvidence(existingHash))!;
            return new(manifest,existingHash,new UTF8Encoding(false,true).GetString(Store.ReadEvidence(manifest.ExportHash)));
        }
        var packet=PreparedExport(ticket);
        if (packet.Manifest.PolicyMode!="manual" || packet.Manifest.RedactionCount!=0)
            throw new UnauthorizedAccessException("Sol requires an exact manual export");
        var input=System.Text.Json.Nodes.JsonNode.Parse(packet.Payload)!.AsObject();
        input["response_schema"]=instructions;
        var prepared=journal.PrepareExternalPacket(input.ToJsonString(),"codex_protocol",files:Request.AllowedFiles);
        Record(eventType,new{ticket_hash=ticketHash,manifest_hash=prepared.ManifestHash},
            [ticketHash,prepared.ManifestHash],key:ticket.AssignmentId+"_"+mode+"_prepared");
        return prepared;
    }

    internal async Task<SolReviewer.Rescue> RunAdviceAsync(string ticketHash,string manifestHash,
        string? developmentCodex,CancellationToken token=default)
    {
        var ticket=Load(ticketHash);
        var export=PrepareAdvice(ticketHash);
        if (export.ManifestHash!=manifestHash)
            throw new UnauthorizedAccessException("Sol advice approval is for a different manifest");
        if (Events("external_dispatch_authorized").Any(e=>e.Input.EvidenceRefs.Contains(manifestHash)))
            throw new InvalidOperationException("Sol advice was already dispatched for this task");
        if (AttemptStartingState.Hash(await AttemptStartingState.CaptureAsync(ticket.MainRoot,null,token))!=ticket.FailureStateHash)
            throw new UnauthorizedAccessException("Task worktree changed before Sol advice");
        var diff=await GitEvidence.CaptureAsync(ticket.MainRoot,Request.AllowedFiles,token);
        var reply=await ReviewerCli.GenerateRescueAsync(journal,export,token,developmentCodex);
        var advice=reply.Status=="OK"?SolReviewer.ParseRescue(reply.Answer,1):new(reply.Status,[],1);
        if (reply.Status!="OK") return advice;
        var hash=Store.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(advice));
        Record("codex_rescue_advice",new{ticket_hash=ticketHash,manifest_hash=manifestHash,
            diff_hash=AgentTaskRunner.HashTextForHost(diff.ReviewDiff),status=advice.Verdict,reply.ExitCode},
            [ticketHash,manifestHash,hash],"untrusted_candidate",key:ticket.AssignmentId+"_advice");
        return advice;
    }

    internal sealed record PendingAdvice(string Hash,string[] Steps);

    internal static PendingAdvice? ReadPendingAdvice(CanonicalStore store,string taskId,string diffHash)
    {
        var events=store.EventIds(taskId).Select(id=>store.ReadTaskEvent(taskId,id))
            .Where(e=>e.Input.Producer=="windows_host").ToArray();
        if (events.Any(e=>e.Input.EventType=="codex_rescue_advice_consumed")) return null;
        var result=events.SingleOrDefault(e=>e.Input.EventType=="codex_rescue_advice");
        if (result is null || result.Input.Metadata.GetProperty("status").GetString()!="GUIDANCE" ||
            result.Input.Metadata.GetProperty("diff_hash").GetString()!=diffHash) return null;
        var hash=result.Input.EvidenceRefs[2];
        var advice=JsonSerializer.Deserialize<SolReviewer.Rescue>(store.ReadEvidence(hash))!;
        return advice.Verdict=="GUIDANCE"?new(hash,advice.Steps):null;
    }

    internal sealed record EditDirectory(string Root,Dictionary<string,string?> Baseline);

    internal EditDirectory PrepareEditDirectory(Ticket ticket,HostEgress.Prepared export)
    {
        if (export.Manifest.RedactionCount!=0)
            throw new UnauthorizedAccessException("Direct Sol editing requires intact source");
        var baseline=JsonSerializer.Deserialize<AttemptStartingState.Snapshot>(Store.ReadEvidence(ticket.StartingStateHash))!;
        var paths=Request.AllowedFiles.Select(path=>HostEgress.NormalizePath(ticket.MainRoot,path)).ToArray();
        if (paths.Any(path=>path.Contains('*')))
            throw new UnauthorizedAccessException("Direct Sol editing requires exact file paths");
        var hashes=paths.ToDictionary(path=>path,path=>baseline.Files.SingleOrDefault(
            file=>file.Path.Equals(path,StringComparison.OrdinalIgnoreCase))?.Hash,StringComparer.OrdinalIgnoreCase);
        using var document=JsonDocument.Parse(export.Payload);
        var sources=document.RootElement.GetProperty("starting_source").EnumerateArray().ToDictionary(
            source=>source.GetProperty("Path").GetString()!,source=>Encoding.UTF8.GetBytes(source.GetProperty("Content").GetString()!),
            StringComparer.OrdinalIgnoreCase);
        if (sources.Count!=hashes.Count(pair=>pair.Value is not null) ||
            hashes.Any(pair=>pair.Value is not null &&
                (!sources.TryGetValue(pair.Key,out var bytes) || CanonicalStore.Hash(bytes)!=pair.Value)))
            throw new UnauthorizedAccessException("Each existing editable file must be explicitly approved for export");
        var root=Path.Combine(Store.Root,"rescue-edits",Guid.NewGuid().ToString("N"));
        CanonicalStore.GuardPath(root);
        Directory.CreateDirectory(root);
        foreach(var (path,bytes) in sources)
        {
            var target=Path.Combine(root,HostEgress.NormalizePath(root,path));
            CanonicalStore.GuardPath(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            output.Write(bytes);
        }
        return new(root,hashes);
    }

    internal static Candidate CollectDirectCandidate(Ticket ticket,EditDirectory edit,string summary)
    {
        var changes=new List<Change>();
        var pending=new Stack<string>();
        pending.Push(edit.Root);
        var bytesRemaining=32_000;
        while (pending.TryPop(out var directory))
        {
            CanonicalStore.GuardPath(directory);
            foreach(var path in Directory.EnumerateFileSystemEntries(directory))
            {
                CanonicalStore.GuardPath(path);
                var relative=Path.GetRelativePath(edit.Root,path).Replace('\\','/');
                if (Directory.Exists(path))
                {
                    if (!edit.Baseline.Keys.Any(file=>file.StartsWith(relative+"/",StringComparison.OrdinalIgnoreCase)))
                        throw new UnauthorizedAccessException("Sol created a directory outside the approved edit scope");
                    pending.Push(path);
                    continue;
                }
                if (!edit.Baseline.TryGetValue(relative,out var before))
                    throw new UnauthorizedAccessException("Sol created a file outside the approved edit scope");
                using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
                if (input.Length>bytesRemaining)
                    throw new InvalidDataException("Direct Sol edit exceeds the candidate budget");
                var bytes=new byte[(int)input.Length];
                input.ReadExactly(bytes);
                bytesRemaining-=bytes.Length;
                if(CanonicalStore.Hash(bytes)!=before)
                    changes.Add(new(relative,before,new UTF8Encoding(false,true).GetString(bytes)));
            }
        }
        if (edit.Baseline.Any(file=>file.Value is not null && !File.Exists(Path.Combine(edit.Root,file.Key))))
            throw new UnauthorizedAccessException("Sol deleted an approved source file");
        if (changes.Count==0 || Encoding.UTF8.GetByteCount(summary)>2_000)
            throw new InvalidDataException("Direct Sol editing did not produce a bounded change and summary");
        return new(ticket.TaskId,ticket.AssignmentId,ticket.Generation,ticket.BaseCommit,ticket.StartingStateHash,
            ticket.StartingDiffHash,changes.OrderBy(change=>change.Path,StringComparer.Ordinal).ToArray(),summary);
    }

    internal async Task<ExternalResult> RunExternalAsync(string ticketHash,CancellationToken token=default,
        string? developmentCodex=null,string? editManifestHash=null)
    {
        Slack.RestrictedExecution.DenyExternal();
        var ticket=Load(ticketHash);
        journal.RequireMutationApproval();
        // The generated code is untrusted. Only the existing OS-isolated .NET test path may execute it.
        var required=Request.RequiredTests;
        if(required.Length==0 || required.Any(test=>test.Kind is not ("dotnet_build" or "dotnet_test")) ||
            !required.Any(test=>test.Kind=="dotnet_test"))
            throw new UnauthorizedAccessException("External code rescue requires isolated dotnet_test validation");
        var export=editManifestHash is null?PreparedExport(ticket):PrepareDirectEdit(ticketHash);
        if(editManifestHash is not null && (export.ManifestHash!=editManifestHash || developmentCodex is null))
            throw new UnauthorizedAccessException("Direct Sol editing requires its exact manual export and development CLI");
        var received=Events("codex_rescue_external_response").SingleOrDefault(e=>e.Input.Metadata.GetProperty("ticket_hash").GetString()==ticketHash);
        if(received is not null && received.Input.Metadata.GetProperty("manifest_hash").GetString()!=export.ManifestHash)
            throw new UnauthorizedAccessException("External candidate export binding drift");
        var validated=Events("codex_rescue_validated").SingleOrDefault(e=>e.Input.Metadata.GetProperty("ticket_hash").GetString()==ticketHash);
        if(validated is not null) {
            if(received is null)return new("BLOCKED",Reason:"TICKET_ALREADY_VALIDATED_OUTSIDE_EXTERNAL_ROUTE");
            var hash=validated.Input.Metadata.GetProperty("validation_hash").GetString()!;
            var prior=JsonSerializer.Deserialize<Validation>(Store.ReadEvidence(hash))!;
            var responseHash=received.Input.Metadata.GetProperty("response_hash").GetString()!;
            var original=ParseExternalCandidate(new UTF8Encoding(false,true).GetString(Store.ReadEvidence(responseHash)));
            var originalHash=CanonicalStore.Hash(JsonSerializer.SerializeToUtf8Bytes(original));
            if(!Events("codex_candidate_received").Any(e=>e.Input.Metadata.GetProperty("ticket_hash").GetString()==ticketHash &&
                e.Input.Metadata.GetProperty("original_candidate_hash").GetString()==originalHash &&
                e.Input.Metadata.GetProperty("candidate_hash").GetString()==prior.CandidateHash))
                return new("BLOCKED",Reason:"EXTERNAL_VALIDATION_BINDING_MISSING");
            await RequireValidationSourceAsync(ticketHash,prior.CandidateHash,ticket.RescueRoot,token);
            RecordExternalValidation(ticketHash,ticket,export.ManifestHash,responseHash,prior.CandidateHash,hash);
            return new("VALIDATED_IN_RESCUE",prior.CandidateHash,hash);
        }
        Candidate candidate;
        string candidateResponseHash;
        if(received is not null) {
            candidateResponseHash=received.Input.Metadata.GetProperty("response_hash").GetString()!;
            candidate=ParseExternalCandidate(new UTF8Encoding(false,true).GetString(
                Store.ReadEvidence(candidateResponseHash)));
        } else {
            if(Events("external_dispatch_authorized").Any(e=>e.Input.EvidenceRefs.Contains(export.ManifestHash)))
                return new("BLOCKED",Reason:"PRIOR_DISPATCH_HAS_NO_RETAINED_CANDIDATE");
            if(AttemptStartingState.Hash(await AttemptStartingState.CaptureAsync(ticket.RescueRoot,null,token))!=ticket.StartingStateHash)
                throw new UnauthorizedAccessException("Rescue worktree changed before dispatch");
            var edit=editManifestHash is null?null:PrepareEditDirectory(ticket,export);
            var response=await ReviewerCli.GenerateRescueAsync(journal,export,token,developmentCodex,edit?.Root);
            if(response.Status!="OK") {
                Record("codex_rescue_external_unavailable",new{ticket_hash=ticketHash,manifest_hash=export.ManifestHash,
                    response.Status,response.Reason,response.ExitCode},[ticketHash,export.ManifestHash],workspace:ticket.WorkspaceId);
                return new(response.Status,Reason:response.Reason);
            }
            var candidateText=edit is null?response.Answer:
                JsonSerializer.Serialize(CollectDirectCandidate(ticket,edit,response.Answer));
            candidate=ParseExternalCandidate(candidateText);
            candidateResponseHash=Store.PutEvidence(Encoding.UTF8.GetBytes(candidateText));
            Record("codex_rescue_external_response",new{ticket_hash=ticketHash,manifest_hash=export.ManifestHash,
                response_hash=candidateResponseHash,provider="sol",requested_model=ReviewerCli.Model("sol"),response.ExitCode,
                direct_edit=edit is not null},
                [ticketHash,export.ManifestHash,candidateResponseHash],"untrusted_candidate",ticket.WorkspaceId,
                key:ticket.AssignmentId+"_external_candidate");
        }
        var candidateHash=await ImportAsync(ticketHash,candidate,token);
        var validationHash=await ValidateAsync(ticketHash,candidateHash,token);
        RecordExternalValidation(ticketHash,ticket,export.ManifestHash,candidateResponseHash,candidateHash,validationHash);
        return new("VALIDATED_IN_RESCUE",candidateHash,validationHash);
    }

    private void RecordExternalValidation(string ticketHash,Ticket ticket,string manifestHash,string responseHash,
        string candidateHash,string validationHash) =>
        Record("codex_rescue_external_validation",new{ticket_hash=ticketHash,manifest_hash=manifestHash,
            response_hash=responseHash,candidate_hash=candidateHash,validation_hash=validationHash},
            [ticketHash,manifestHash,responseHash,candidateHash,validationHash],workspace:ticket.WorkspaceId,
            key:ticket.AssignmentId+"_external_validation");

    internal static Candidate ParseExternalCandidate(string answer)
    {
        if(Encoding.UTF8.GetByteCount(answer)>48_000 || ReviewerEvidence.ContainsSecret(answer))
            throw new InvalidDataException("External candidate exceeds scope");
        using var json=JsonDocument.Parse(answer);
        RequireUniqueMembers(json.RootElement);
        var candidate=json.RootElement.Deserialize<Candidate>(new JsonSerializerOptions {
            UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow
        })??throw new InvalidDataException("External candidate is missing");
        if(candidate.Changes is null || candidate.Changes.Any(change=>change is null))
            throw new InvalidDataException("External candidate changes are missing");
        return candidate;

        static void RequireUniqueMembers(JsonElement element) {
            if(element.ValueKind==JsonValueKind.Object) {
                var names=new HashSet<string>(StringComparer.Ordinal);
                foreach(var member in element.EnumerateObject()) {
                    if(!names.Add(member.Name))throw new InvalidDataException("External candidate has duplicate fields");
                    RequireUniqueMembers(member.Value);
                }
            } else if(element.ValueKind==JsonValueKind.Array)
                foreach(var item in element.EnumerateArray())RequireUniqueMembers(item);
        }
    }
    internal async Task<string> ImportAsync(string ticketHash,Candidate candidate,CancellationToken token=default) {
        var ticket=Load(ticketHash);var request=Request;var starting=JsonSerializer.Deserialize<AttemptStartingState.Snapshot>(Store.ReadEvidence(ticket.StartingStateHash))!;
        if(candidate.TaskId!=ticket.TaskId || candidate.AssignmentId!=ticket.AssignmentId || candidate.Generation!=ticket.Generation || candidate.BaseCommit!=ticket.BaseCommit ||
            candidate.StartingStateHash!=ticket.StartingStateHash || candidate.StartingDiffHash!=ticket.StartingDiffHash || candidate.Changes is null || candidate.Changes.Length is <1 or >100 || candidate.Summary is null || candidate.Summary.Length>2000)
            throw new UnauthorizedAccessException("Stale or invalid candidate identity");
        var originalCandidateHash=Store.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(candidate));
        candidate=candidate with{Changes=candidate.Changes.Select(c=>c with{Path=request.AllowedFiles.SingleOrDefault(p=>string.Equals(HostEgress.NormalizePath(ticket.RescueRoot,p),HostEgress.NormalizePath(ticket.RescueRoot,c.Path),StringComparison.OrdinalIgnoreCase))??throw new UnauthorizedAccessException("Candidate path not in assigned write_scope")}).ToArray()};
        var accepted=Store.RequireExportPolicy(ticket.TaskId);
        var scope=request.AllowedFiles.Select(p=>HostEgress.NormalizePath(ticket.RescueRoot,p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(!accepted.Policy.WriteScope.Contains("task_allowed_files") && scope.Any(p=>!accepted.Policy.WriteScope.Contains(p,StringComparer.OrdinalIgnoreCase)))throw new UnauthorizedAccessException("Host write_scope denies candidate");
        var before=await AttemptStartingState.CaptureAsync(ticket.RescueRoot,null,token);

        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var change in candidate.Changes) {
            var path=HostEgress.NormalizePath(ticket.RescueRoot,change.Path);
            var expected=starting.Files.SingleOrDefault(f=>string.Equals(f.Path,path,StringComparison.OrdinalIgnoreCase))?.Hash;
            if(!scope.Contains(path) || !paths.Add(path) || expected!=change.BeforeHash || change.Content is null || Encoding.UTF8.GetByteCount(change.Content)>1024*1024)
                throw new UnauthorizedAccessException("Candidate escaped exact starting write scope");
        }
        var candidateHash=Store.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(candidate));
        var desired=starting.Files.ToDictionary(f=>f.Path,f=>f.Hash,StringComparer.OrdinalIgnoreCase);
        foreach(var change in candidate.Changes)desired[HostEgress.NormalizePath(ticket.RescueRoot,change.Path)]=CanonicalStore.Hash(Encoding.UTF8.GetBytes(change.Content));
        var intended=starting with{Files=desired.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>new AttemptStartingState.FileState(p.Key,p.Value)).ToArray()};
        var priorIntent=Events("codex_candidate_application_intent").Any(e=>e.Input.EvidenceRefs.Contains(candidateHash) && e.Input.Metadata.GetProperty("ticket_hash").GetString()==ticketHash);
        if(AttemptStartingState.Hash(before)!=ticket.StartingStateHash && (!priorIntent || !MatchesPartial(before,starting,intended,scope)))throw new UnauthorizedAccessException("Rescue state drift is not an authorized partial import");
        Record("codex_candidate_application_intent",new{ticket_hash=ticketHash,candidate_hash=candidateHash,before_state_hash=ticket.StartingStateHash,ordered_changes=candidate.Changes.Select(c=>new{c.Path,c.BeforeHash,after_hash=CanonicalStore.Hash(Encoding.UTF8.GetBytes(c.Content))})},[ticketHash,candidateHash,ticket.StartingStateHash],workspace:ticket.WorkspaceId,key:ticket.AssignmentId+"_candidate_intent");
        Record("codex_candidate_received",new{ticket_hash=ticketHash,candidate_hash=candidateHash,original_candidate_hash=originalCandidateHash,normalization="Host canonical allowed-file spelling"},[ticketHash,candidateHash,originalCandidateHash],"untrusted_candidate",ticket.WorkspaceId,key:ticket.AssignmentId+"_candidate");
        var position=0;
        await using(var gate=new SideEffectGate(ticket.RescueRoot,request.AllowedFiles,ticket.BaseCommit,false))foreach(var change in candidate.Changes) {
            position++;fault?.Invoke("import_before_"+position);
            var path=Path.Combine(ticket.RescueRoot,HostEgress.NormalizePath(ticket.RescueRoot,change.Path));var desiredHash=CanonicalStore.Hash(Encoding.UTF8.GetBytes(change.Content));
            if(!File.Exists(path) || CanonicalStore.Hash(File.ReadAllBytes(path))!=desiredHash)
                await gate.HandleAsync(new(gate.PipeName,gate.Nonce,"write",change.Path,change.Content),token);
            fault?.Invoke("import_after_"+position);
        }
        var applied=await AttemptStartingState.CaptureAsync(ticket.RescueRoot,Store,token);
        if(!MatchesFinal(applied,intended))throw new UnauthorizedAccessException("Imported candidate filesystem differs from whole-operation intent");
        var appliedHash=Store.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(applied));
        var diff=await GitEvidence.CaptureAsync(ticket.RescueRoot,request.AllowedFiles,token);if(diff.UnexpectedFiles.Length!=0)throw new UnauthorizedAccessException("Rescue changed outside task scope");
        var diffHash=Store.PutEvidence(Encoding.UTF8.GetBytes(diff.ReviewDiff));
        Store.CollectResult(new(ticket.TaskId,ticket.AssignmentId,ticket.WorkerId,ticket.Generation,ticket.BaseCommit,ticket.StartingStateHash,ticket.StartingDiffHash,diffHash,null,
            diff.ChangedFiles,[candidateHash,diffHash,appliedHash],candidate.Summary));
        Record("codex_candidate_applied_in_rescue",new{ticket_hash=ticketHash,candidate_hash=candidateHash,codex_result_diff_hash=diffHash,result_state_hash=appliedHash},[candidateHash,diffHash,appliedHash],workspace:ticket.WorkspaceId,key:ticket.AssignmentId+"_candidate_applied");
        return candidateHash;
    }
    internal sealed record ValidationSource(Ticket Ticket,string CandidateHash,string StateHash,string DiffHash);
    internal async Task<ValidationSource> RequireValidationSourceAsync(string ticketHash,string candidateHash,string root,CancellationToken token=default){
        var ticket=Load(ticketHash);
        if(!string.Equals(Path.GetFullPath(root),Path.GetFullPath(ticket.RescueRoot),StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Rescue validation root does not match ticket");
        var reservation=Events("codex_rescue_reserved").SingleOrDefault(e=>e.Input.VerificationStatus=="host_verified"&&e.Input.WorkspaceId==ticket.WorkspaceId&&e.Input.Metadata.GetProperty("ticket_hash").GetString()==ticketHash);
        if(reservation is null||!reservation.Input.EvidenceRefs.Contains(ticketHash,StringComparer.Ordinal))throw new UnauthorizedAccessException("No canonical Host Rescue reservation");
        if(!Store.TaskEvidence(ticket.TaskId).Contains(candidateHash,StringComparer.Ordinal))throw new UnauthorizedAccessException("Candidate is not attached to task");
        var candidate=JsonSerializer.Deserialize<Candidate>(Store.ReadEvidence(candidateHash))!;
        if(candidate.TaskId!=ticket.TaskId||candidate.AssignmentId!=ticket.AssignmentId||candidate.Generation!=ticket.Generation||candidate.BaseCommit!=ticket.BaseCommit||
            candidate.StartingStateHash!=ticket.StartingStateHash||candidate.StartingDiffHash!=ticket.StartingDiffHash)throw new UnauthorizedAccessException("Candidate assignment mismatch");
        var diff=await GitEvidence.CaptureAsync(ticket.RescueRoot,Request.AllowedFiles,token);
        if(diff.Head!=ticket.BaseCommit || diff.UnexpectedFiles.Length!=0)throw new UnauthorizedAccessException("Rescue HEAD/scope drift");
        var before=await AttemptStartingState.CaptureAsync(ticket.RescueRoot,null,token);var stateHash=AttemptStartingState.Hash(before);
        var appliedReceipt=Events("codex_candidate_applied_in_rescue").SingleOrDefault(e=>e.Input.VerificationStatus=="host_verified"&&e.Input.WorkspaceId==ticket.WorkspaceId&&e.Input.Metadata.GetProperty("ticket_hash").GetString()==ticketHash && e.Input.Metadata.GetProperty("candidate_hash").GetString()==candidateHash)
            ??throw new UnauthorizedAccessException("No Host-applied candidate receipt");
        var actualDiffHash=CanonicalStore.Hash(Encoding.UTF8.GetBytes(diff.ReviewDiff));
        if(appliedReceipt.Input.Metadata.GetProperty("result_state_hash").GetString()!=stateHash || appliedReceipt.Input.Metadata.GetProperty("codex_result_diff_hash").GetString()!=actualDiffHash||
            !new[]{candidateHash,stateHash,actualDiffHash}.All(h=>appliedReceipt.Input.EvidenceRefs.Contains(h,StringComparer.Ordinal)))
            throw new UnauthorizedAccessException("Imported candidate state/diff changed before validation");
        Store.RequireValidationRescueResult(ticket,candidateHash,stateHash,actualDiffHash);
        RequireTicket(ticket,ticketHash);
        return new(ticket,candidateHash,stateHash,actualDiffHash);
    }
    internal async Task<string> ValidateAsync(string ticketHash,string candidateHash,CancellationToken token=default) {
        var source=await RequireValidationSourceAsync(ticketHash,candidateHash,Load(ticketHash).RescueRoot,token);var ticket=source.Ticket;var request=Request;
        var stateHash=source.StateHash;var diff=await GitEvidence.CaptureAsync(ticket.RescueRoot,request.AllowedFiles,token);
        var wsl=await WslWorkspace.ResolveRegisteredRescueAsync(Store,ticket.WorkspaceId,token);var tests=new List<ToolRouter.TestResult>();
        foreach(var required in request.RequiredTests)tests.Add(await HostValidationPolicy.RunRescueAsync(journal,ticketHash,candidateHash,ticket.RescueRoot,wsl,required,token));
        var afterTests=await AttemptStartingState.CaptureAsync(ticket.RescueRoot,null,token);
        if(AttemptStartingState.Hash(afterTests)!=stateHash || !tests.All(t=>t.Passed))throw new InvalidDataException("Rescue required tests failed or mutated source");
        var prompt=ReviewerEvidence.Build(request,diff.ReviewDiff,diff.ChangedFiles,tests,new("",[]))+"\nYou are a fresh independent local Critic. Assess every acceptance criterion and test evidence. Return exactly JSON {\"verdict\":\"PASS\",\"issues\":[]} or {\"verdict\":\"ISSUES\",\"issues\":[\"specific issue\"]}. Do not modify files.";
        var critic=await PiRpcRunner.RunAsync(wsl,prompt,true,windowsRoot:ticket.RescueRoot,taskId:ticket.TaskId,token:token);
        var parsed=CopilotReviewer.Parse(critic.Text);var criticHash=Store.PutEvidence(Encoding.UTF8.GetBytes(critic.Text));
        Record("codex_rescue_critic_response",new{ticket_hash=ticketHash,critic=parsed.Verdict,critic.ToolErrors},[criticHash],"model_claim",ticket.WorkspaceId);
        if(parsed.Verdict=="UNAVAILABLE" && critic.ToolErrors==0) {
            var retry=await PiRpcRunner.RunAsync(wsl,"Return exactly one JSON object and nothing else. No explanations or markdown. Verdict PASS with empty issues, or ISSUES with specific issues. Evaluate the following same evidence in a fresh context.\n"+prompt,true,windowsRoot:ticket.RescueRoot,taskId:ticket.TaskId,token:token);
            parsed=CopilotReviewer.Parse(retry.Text);criticHash=Store.PutEvidence(Encoding.UTF8.GetBytes(retry.Text));
            Record("codex_rescue_critic_format_retry",new{ticket_hash=ticketHash,critic=parsed.Verdict,retry.ToolErrors},[criticHash],"model_claim",ticket.WorkspaceId);
            if(retry.ToolErrors>0)throw new InvalidDataException("Fresh local Rescue Critic retry had tool errors");
        }
        if(parsed.Verdict!="PASS" || critic.ToolErrors>0)throw new InvalidDataException("Fresh local Rescue Critic did not pass");
        var externalPassed=request.EffectiveRisk=="LOW";
        if(!externalPassed) {
            var external=await ExternalReviewCoordinator.RunAsync(request,diff.ReviewDiff,diff.ChangedFiles,tests,new("",[]),token);
            externalPassed=external.Passed;
        }
        if(!externalPassed || AttemptStartingState.Hash(await AttemptStartingState.CaptureAsync(ticket.RescueRoot,null,token))!=stateHash)
            throw new InvalidDataException("Rescue review failed or source changed");
        var diffHash=Store.PutEvidence(Encoding.UTF8.GetBytes(diff.ReviewDiff));var validation=new Validation(ticketHash,candidateHash,stateHash,diffHash,tests.ToArray(),parsed.Verdict,externalPassed);
        var hash=Store.PutEvidence(JsonSerializer.SerializeToUtf8Bytes(validation));Record("codex_rescue_validated",new{ticket_hash=ticketHash,validation_hash=hash,result_state_hash=stateHash},[ticketHash,hash,stateHash,diffHash,criticHash],workspace:ticket.WorkspaceId);
        // Packet assembly is local and best-effort; a learning failure never replays verified file effects.
        try{new DifferentialLearning(journal).Assemble(ticketHash,hash);}catch(Exception e)when(e is InvalidDataException or UnauthorizedAccessException or IOException){Record("differential_learning_unavailable",new{ticket_hash=ticketHash,validation_hash=hash,error_type=e.GetType().Name},[ticketHash,hash],workspace:ticket.WorkspaceId);}
        return hash;
    }
    internal async Task PromoteAsync(string ticketHash,string validationHash,CancellationToken token=default) {
        var ticket=Load(ticketHash);var validation=JsonSerializer.Deserialize<Validation>(Store.ReadEvidence(validationHash))!;
        if(validation.TicketHash!=ticketHash || validation.CriticVerdict!="PASS" || !validation.ExternalPassed || validation.Tests.Length!=Request.RequiredTests.Length || validation.Tests.Any(t=>!t.Passed) ||
            !Store.EventIds(ticket.TaskId).Select(id=>Store.ReadTaskEvent(ticket.TaskId,id)).Any(e=>e.Input.EventType=="codex_rescue_validated" && e.Input.Producer=="windows_host" && e.Input.EvidenceRefs.Contains(validationHash)))
            throw new UnauthorizedAccessException("No matching Host-verified rescue validation");
        var rescue=await AttemptStartingState.CaptureAsync(ticket.RescueRoot,null,token);
        if(AttemptStartingState.Hash(rescue)!=validation.ResultStateHash)throw new UnauthorizedAccessException("Validated rescue source changed");
        var main=await AttemptStartingState.CaptureAsync(ticket.MainRoot,null,token);
        var failure=JsonSerializer.Deserialize<AttemptStartingState.Snapshot>(Store.ReadEvidence(ticket.FailureStateHash))!;
        var promotionScope=Request.AllowedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var priorPromotion=Events("codex_rescue_promotion_intent").Any(e=>e.Input.Metadata.GetProperty("ticket_hash").GetString()==ticketHash && e.Input.EvidenceRefs.Contains(validationHash));
        if(AttemptStartingState.Hash(main)!=ticket.FailureStateHash && (!priorPromotion || !MatchesPartial(main,failure,rescue,promotionScope)))throw new UnauthorizedAccessException("Main drift is not a Host-authorized partial promotion");
        journal.RequireMutationApproval();var policy=Store.RequireExportPolicy(ticket.TaskId).Policy;
        if(!policy.WriteScope.Contains("task_allowed_files") && Request.AllowedFiles.Any(p=>!policy.WriteScope.Contains(p,StringComparer.OrdinalIgnoreCase)))throw new UnauthorizedAccessException("Promotion write_scope denied");
        var changes=new List<(string Path,string Content)>();
        foreach(var relative in Request.AllowedFiles) {
            var path=HostEgress.NormalizePath(ticket.RescueRoot,relative);var target=rescue.Files.SingleOrDefault(f=>string.Equals(f.Path,path,StringComparison.OrdinalIgnoreCase));
            var original=main.Files.SingleOrDefault(f=>string.Equals(f.Path,path,StringComparison.OrdinalIgnoreCase));
            if(target?.Hash==original?.Hash)continue;
            if(target?.Hash is null)throw new InvalidDataException("Deletion promotion is not enabled");
            changes.Add((relative,new UTF8Encoding(false,true).GetString(File.ReadAllBytes(Path.Combine(ticket.RescueRoot,path)))));
        }
        Record("codex_rescue_promotion_intent",new{ticket_hash=ticketHash,validation_hash=validationHash,before_hash=ticket.FailureStateHash,target_hash=validation.ResultStateHash,ordered_changes=Request.AllowedFiles.Select(p=>new{path=p,before_hash=failure.Files.SingleOrDefault(f=>string.Equals(f.Path,p,StringComparison.OrdinalIgnoreCase))?.Hash,after_hash=rescue.Files.SingleOrDefault(f=>string.Equals(f.Path,p,StringComparison.OrdinalIgnoreCase))?.Hash})},[ticketHash,validationHash,ticket.FailureStateHash,validation.ResultStateHash],key:ticket.AssignmentId+"_promotion_intent");
        var position=0;
        await using(var gate=new SideEffectGate(ticket.MainRoot,Request.AllowedFiles,ticket.BaseCommit,false,journal))foreach(var change in changes) {
            position++;fault?.Invoke("promotion_before_"+position);
            await gate.HandleAsync(new(gate.PipeName,gate.Nonce,"write",change.Path,change.Content),token);
            fault?.Invoke("promotion_after_"+position);
        }
        var promoted=await AttemptStartingState.CaptureAsync(ticket.MainRoot,null,token);
        if(!promoted.Files.SequenceEqual(rescue.Files) || promoted.Head!=rescue.Head || promoted.IndexTree!=main.IndexTree)throw new InvalidDataException("Promotion filesystem parity or original index changed");
        Record("codex_rescue_promoted",new{ticket_hash=ticketHash,validation_hash=validationHash,codex_result_diff_hash=validation.ResultDiffHash,scope="Verified files promoted; task completion still requires normal gates"},[ticketHash,validationHash,validation.ResultDiffHash]);
    }
}
