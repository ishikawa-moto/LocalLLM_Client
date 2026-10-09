using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost.AgentV2;

internal static class ExternalReferenceLearningTests
{
    internal static async Task RunAsync(string proofRoot,string bundle)
    {
        if(Directory.Exists(proofRoot))throw new InvalidDataException("Existing proof root: inspect instead of replay");
        Directory.CreateDirectory(proofRoot);var repo=Path.Combine(Path.GetFullPath(proofRoot),"repo");Directory.CreateDirectory(repo);
        await AttemptStartingState.GitAsync(repo,["init"]);
        await AttemptStartingState.GitAsync(repo,["-c","user.name=Public Fixture","-c","user.email=fixture@invalid.example","commit","--allow-empty","-m","Public learning fixture"]);
        var one="Public repository configuration records context 65536.\n";var two="Public repository fixture records observation value 42.\n";
        var file1=Path.Combine(repo,"configuration.txt");var file2=Path.Combine(repo,"observation.txt");
        File.WriteAllText(file1,one,new UTF8Encoding(false));File.WriteAllText(file2,two,new UTF8Encoding(false));
        var workspace=HostTaskJournal.WorkspaceId(repo);
        using var store=new CanonicalStore(Path.Combine(proofRoot,"state"));
        store.RegisterWorkspace(new("public_reference_learning",workspace,repo,repo,"main","base"));
        var requestJson="{}";store.CreateTask("public_learning_task",workspace,CanonicalStore.Hash(Encoding.UTF8.GetBytes(requestJson)),requestJson);
        store.Checkpoint(new("public_learning_task",workspace,"fixture","initial","task_checkpoint","execution_evidence","host_observed",[],
            JsonSerializer.SerializeToElement(new{phase="existing_task"})),"{\"phase\":\"existing_task\"}",3,5,1,2);
        var before=store.GetTask("public_learning_task")!;
        var reference=ExternalReferenceCorpus.Import(store,"public_learning_task",File.ReadAllBytes(Path.Combine(bundle,"original.pdf")),
            File.ReadAllBytes(Path.Combine(bundle,"extracted.md")),File.ReadAllBytes(Path.Combine(bundle,"manifest.json")));
        using var manifest=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(bundle,"manifest.json")));
        var page=manifest.RootElement.GetProperty("page_map")[0];var text=File.ReadAllBytes(Path.Combine(bundle,"extracted.md"));
        var quote=Encoding.UTF8.GetString(text.AsSpan(page.GetProperty("start_byte").GetInt32(),page.GetProperty("end_byte").GetInt32()-page.GetProperty("start_byte").GetInt32())).Trim();
        async Task<ExternalClaimVerification.Result> Observe(string path,string content,string repositoryQuote)=>
            await ExternalClaimVerification.CheckAsync(store,"public_learning_task",repo,
                new(1,reference.EventId,1,quote,path,CanonicalStore.Hash(Encoding.UTF8.GetBytes(content)),repositoryQuote));
        var first=await Observe("configuration.txt",one,one.Trim());
        var second=await Observe("observation.txt",two,two.Trim());
        var input=new ExternalReferenceLearning.Input(1,"external-learning-public-0001","Observed public repository literals","general",[first.EventId,second.EventId]);
        var prepared=await ExternalReferenceLearning.PrepareAsync(store,repo,input);
        var count=store.EventIds("public_learning_task").Length;
        var again=await ExternalReferenceLearning.PrepareAsync(store,repo,input);
        if(prepared.Duplicate||!again.Duplicate||prepared.EventId!=again.EventId||prepared.ManifestHash!=again.ManifestHash||store.EventIds("public_learning_task").Length!=count)
            throw new Exception("Exact candidate duplicate differs");
        var candidate=JsonSerializer.Deserialize<ExternalReferenceLearning.Manifest>(store.ReadEvidence(prepared.ManifestHash))!;
        if(candidate.knowledge_adopted||candidate.original_document_claims_verified||!candidate.reviewer_required
            ||candidate.adoption_status!="LOCAL_CANDIDATE_NOT_DISPATCHED"||candidate.sources.Length!=2)throw new Exception("Candidate wrongly granted truth/adoption");
        foreach(var source in candidate.sources)
            if(CanonicalStore.Hash(Encoding.UTF8.GetBytes(source.content))!=source.content_sha256||!candidate.body.Contains(source.content.TrimEnd('\n'),StringComparison.Ordinal))
                throw new Exception("Exact source/body binding differs");
        var packet=await ExternalReferenceLearning.ExportLocalAsync(store,repo,prepared.ManifestHash);
        var exportedCount=store.EventIds("public_learning_task").Length;
        var samePacket=await ExternalReferenceLearning.ExportLocalAsync(store,repo,prepared.ManifestHash);
        if(packet.Duplicate||!samePacket.Duplicate||Directory.GetFiles(packet.PacketDirectory).Length!=3
            ||store.EventIds("public_learning_task").Length!=exportedCount)throw new Exception("Local packet inventory/idempotency differs");
        var observedFile=Path.Combine(packet.PacketDirectory,candidate.sources[0].filename);
        File.AppendAllText(observedFile,"Changed packet bytes.\n");
        try{await ExternalReferenceLearning.ExportLocalAsync(store,repo,prepared.ManifestHash);throw new Exception("Changed packet accepted");}
        catch(InvalidDataException){if(store.EventIds("public_learning_task").Length!=exportedCount)throw new Exception("Changed packet wrote event");}
        File.WriteAllText(observedFile,candidate.sources[0].content,new UTF8Encoding(false));
        var checks=new List<string>{"two_current_supported_observations_prepare_local_writeback_candidate","one_original_pdf_remains_one_document",
            "historical_literal_scope_not_pdf_truth","exact_candidate_idempotent","body_and_source_bytes_hash_bound"};
        async Task Reject(ExternalReferenceLearning.Input value,string label)
        {
            var events=store.EventIds("public_learning_task").Length;
            try{await ExternalReferenceLearning.PrepareAsync(store,repo,value);}
            catch(Exception e)when(e is InvalidDataException or UnauthorizedAccessException)
            {if(store.EventIds("public_learning_task").Length!=events)throw new Exception("Rejected candidate wrote event");checks.Add(label);return;}
            throw new Exception("Invalid candidate accepted: "+label);
        }
        checks.Add("local_outbox_packet_exact_bytes_duplicate_no_dispatch");checks.Add("changed_packet_retained_and_refused");
        await Reject(input with{Title="Different request contents"},"request_id_content_conflict_rejected");
        await Reject(input with{Title="Invalid\nmultiline title"},"multiline_title_rejected");
        await Reject(input with{ObservationEventIds=[first.EventId,first.EventId]},"duplicate_observation_ids_rejected");
        await Reject(input with{ObservationEventIds=[reference.EventId]},"unverified_corpus_event_cannot_be_learning_authority");
        var unsupported=await Observe("configuration.txt",one,"No supported quote is present here");
        await Reject(input with{RequestId="external-learning-unsupported",ObservationEventIds=[unsupported.EventId]},"unsupported_repository_quote_rejected");
        var anotherSameFile=await Observe("configuration.txt",one,"configuration records context 65536");
        await Reject(input with{RequestId="external-learning-same-file",ObservationEventIds=[first.EventId,anotherSameFile.EventId]},"same_file_cannot_inflate_corroboration");
        var unicodeLarge=new string('日',3200);var unicodePath=Path.Combine(repo,"unicode-large.txt");
        File.WriteAllText(unicodePath,unicodeLarge+"\n",new UTF8Encoding(false));
        var unicodeObserved=await Observe("unicode-large.txt",unicodeLarge+"\n",unicodeLarge);
        await Reject(input with{RequestId="external-learning-source-budget",ObservationEventIds=[unicodeObserved.EventId]},"escaped_UTF8_source_budget_rejected_before_candidate_save");
        var largeIds=new List<string>();
        for(var i=0;i<4;i++){
            var content=new string('日',1900)+(char)('一'+i);var name="unicode-budget-"+i+".txt";
            File.WriteAllText(Path.Combine(repo,name),content+"\n",new UTF8Encoding(false));
            largeIds.Add((await Observe(name,content+"\n",content)).EventId);
        }
        await Reject(input with{RequestId="external-learning-manifest-budget",ObservationEventIds=largeIds.ToArray()},"actual_UTF8_manifest_budget_rejected_before_candidate_save");

        File.AppendAllText(file1,"Changed repository bytes.\n");
        await Reject(input with{RequestId="external-learning-stale-file"},"changed_current_file_rejected");File.WriteAllText(file1,one,new UTF8Encoding(false));
        await AttemptStartingState.GitAsync(repo,["-c","user.name=Public Fixture","-c","user.email=fixture@invalid.example","commit","--allow-empty","-m","HEAD drift"]);
        await Reject(input with{RequestId="external-learning-stale-head"},"changed_current_HEAD_rejected");
        await AttemptStartingState.GitAsync(repo,["reset","--soft",first.Head]);
        var single=await ExternalReferenceLearning.PrepareAsync(store,repo,input with{RequestId="external-learning-single-source",ObservationEventIds=[first.EventId]});
        if(single.SourceCount!=1||single.KnowledgeAdopted)throw new Exception("Single source local draft readiness differs");
        var singlePacket=await ExternalReferenceLearning.ExportLocalAsync(store,repo,single.ManifestHash);
        checks.Add("single_source_local_candidate_still_requires_normal_reviewer_evidence");
        if(store.GetTask("public_learning_task")!=before)throw new Exception("Learning preparation changed task/state/counters");
        checks.Add("existing_task_state_approvals_counters_preserved");
        var result=new{status="PASS_HOST_EXTERNAL_OBSERVATION_LOCAL_LEARNING_CANDIDATE_ONLY",checks,prepared,duplicate=again,single,packet,singlePacket,
            manifest_hash=prepared.ManifestHash,manifest=candidate,canonical_root=store.Root,model_calls=0,network_calls=0,production_knowledge_adoptions=0};
        File.WriteAllText(Path.Combine(proofRoot,"proof.json"),JsonSerializer.Serialize(result));
        Console.WriteLine(JsonSerializer.Serialize(new{status=result.status,checks=checks.Count,prepared.ManifestHash,source_count=2}));
    }
}



