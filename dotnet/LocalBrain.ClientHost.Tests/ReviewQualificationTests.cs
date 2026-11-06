using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalBrain.ClientHost;
using LocalBrain.ClientHost.AgentV2;

internal static class ReviewQualificationTests
{
    internal static void RunPermissionOptionsProof()
    {
        var checks=0;
        void Check(bool value,string reason){if(!value)throw new Exception(reason);checks++;}
        var names=new[]{"OPENAI_API_KEY","GH_TOKEN","GITHUB_TOKEN","COPILOT_GITHUB_TOKEN",
            "COPILOT_PROVIDER_BASE_URL","LOCALBRAIN_CLIENT_CONFIG","CODEX_APP_TOOLS_PIPE_PATH","OTEL_EXPORTER_OTLP_ENDPOINT"};
        var saved=names.ToDictionary(n=>n,Environment.GetEnvironmentVariable);
        try {
            // Synthetic values remain in this test process and are never sent to a child.
            foreach(var name in names)Environment.SetEnvironmentVariable(name,"PUBLIC_SYNTHETIC_TEST_VALUE");
            foreach(var provider in new[]{"sol","copilot"}) {
                var start=ReviewerCli.StartInfo("C:\\public-fixture\\reviewer.exe","C:\\public-fixture\\empty-work",provider,"high");
                Check(!start.UseShellExecute && start.RedirectStandardInput,"Packet transport escaped stdin-only launch");
                foreach(var name in names)Check(!start.Environment.ContainsKey(name),"Inherited secret/Host capability environment");
                Check(!start.ArgumentList.Any(a=>a is "--allow-all" or "--yolo" or "--allow-all-tools" or "--allow-all-paths" or
                    "--dangerously-bypass-approvals-and-sandbox"),"Restriction was bypassed");
                if(provider=="copilot") {
                    foreach(var arg in new[]{"--available-tools=","--deny-tool=shell","--deny-tool=write","--deny-tool=url","--disallow-temp-dir"})
                        Check(start.ArgumentList.Contains(arg),"Required packet-only policy option absent: "+arg);
                    Check(!start.ArgumentList.Any(a=>a.StartsWith("--available-tools=",StringComparison.Ordinal)&&a!="--available-tools="),"Probe exposed tools");
                    Check(start.Environment["COPILOT_HOME"]=="C:\\public-fixture\\empty-work\\copilot-home" &&
                        start.Environment["COPILOT_ALLOW_ALL"]=="false","User config isolation lost");
                } else {
                    foreach(var arg in new[]{"features.shell_tool=false","features.code_mode_host=false","features.hooks=false",
                        "permissions.review_packet.filesystem={\":root\"=\"deny\",\":minimal\"=\"read\"}","approval_policy=\"never\""})
                        Check(start.ArgumentList.Contains(arg),"Sol restriction was relaxed: "+arg);
                }
            }
        } finally {foreach(var name in names)Environment.SetEnvironmentVariable(name,saved[name]);}
        Console.WriteLine(JsonSerializer.Serialize(new{status="PASS",checks,external_inference=false,cli_processes_started=0,
            canonical_opened=false,scope="launch options/environment only; NOT effective provider or OS denial proof"}));
    }
    internal static void RunJsonProof()
    {
        var checks=0;string? originalException=null;
        void Check(bool value,string reason){if(!value)throw new Exception(reason);checks++;}
        // Reproduce only the old serialization shape: no journal, CLI, approval or inference.
        try {JsonSerializer.Serialize(new {status="BLOCKED",Status="OK",Reason="",qualification_only=true,events=Array.Empty<string>()},ClientConfig.JsonOptions);}
        catch(InvalidOperationException error) {originalException=error.ToString();}
        Check(originalException?.Contains("collides with another property",StringComparison.Ordinal)==true,"Original camelCase collision was not reproduced");
        var eventIds=new[]{"preserved_event_id"};
        ReviewerCli.Reply?[] replies=[null,new("OK",ExitCode:0),new("UNAVAILABLE",Reason:"CLI_FAILURE",ExitCode:1),new("INVALID",Reason:"REVIEW_TOOL_EVENT",ExitCode:0)];
        foreach(var reply in replies) {
            // Exercise the exact helper called by the real post-dispatch console output.
            var json=ReviewQualification.SerializeProbeResult(reply,eventIds);
            using var doc=JsonDocument.Parse(json);var root=doc.RootElement;
            Check(root.EnumerateObject().Count()==6 && root.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()==6,"Probe JSON has colliding/unexpected keys");
            Check(root.GetProperty("status").GetString()=="BLOCKED","Transport success promoted qualification status");
            Check(root.GetProperty("transport_status").GetString()==reply?.Status,"Transport status lost");
            Check(root.GetProperty("transport_reason").GetString()==reply?.Reason,"Transport reason lost");
            var exit=root.GetProperty("transport_exit_code");
            Check(reply?.ExitCode is int code?exit.GetInt32()==code:exit.ValueKind==JsonValueKind.Null,"Transport exit code lost");
            Check(root.GetProperty("qualification_only").GetBoolean() && root.GetProperty("events")[0].GetString()==eventIds[0],"Qualification scope/evidence changed");
        }
        Console.WriteLine(JsonSerializer.Serialize(new {status="PASS",checks,external_inference=false,
            cli_processes_started=0,canonical_opened=false,original_exception=originalException},ClientConfig.JsonOptions));
    }
    internal static async Task RunAsync(string python)
    {
        var checks=0;
        void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
        async Task Reject(Func<Task> action){try{await action();}catch(UnauthorizedAccessException){checks++;return;}throw new Exception("Expected authorization denial");}
        foreach(var root in new[]{CanonicalStore.DefaultRoot,Path.GetDirectoryName(CanonicalStore.DefaultRoot)!,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"AppData","Local","LocalBrain","qualification")})
            await Reject(()=>{ReviewQualification.GuardRoot(root);return Task.CompletedTask;});
        var parent=Directory.CreateTempSubdirectory("localbrain-review-qualification-proof-").FullName;
        var repo=Directory.CreateDirectory(Path.Combine(parent,"repo")).FullName;
        var boundary=Directory.CreateDirectory(Path.Combine(parent,"boundary")).FullName;
        File.WriteAllText(Path.Combine(repo,"README.md"),"Public synthetic addition review. No private data.\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(repo,"addition.py"),"def add(a, b):\n    return a - b\n",new UTF8Encoding(false));
        await Git("init","-q");await Git("add","README.md","addition.py");
        await Git("-c","user.name=QualificationProof","-c","user.email=proof@example.invalid","commit","-q","-m","Public fixture baseline");
        File.WriteAllText(Path.Combine(repo,"addition.py"),"def add(a, b):\n    return a + b\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(boundary,"outside-packet-canary.txt"),"PUBLIC_CANARY_NOT_IN_PACKET\n",new UTF8Encoding(false));
        var exe=Environment.ProcessPath!;var sha=CanonicalStore.Hash(File.ReadAllBytes(exe));
        var endpoint=new ReviewerCli.Endpoint(exe,sha);
        var rootPath=Path.Combine(parent,"trial");
        var config=new ReviewQualification.Configuration(rootPath,repo,boundary,Path.GetFullPath(python),CanonicalStore.Hash(File.ReadAllBytes(python)),endpoint,endpoint);
        var path=Path.Combine(parent,"request.json");
        async Task Prepare(ReviewQualification.Configuration c){File.WriteAllText(path,JsonSerializer.Serialize(c,ClientConfig.JsonOptions));await ReviewQualification.RunAsync(["prepare",path]);}
        await Reject(()=>Prepare(config with{Sol=endpoint with{SubscriptionVerified=true,PacketBoundaryVerified=true}}));
        Check(!Directory.Exists(rootPath),"Invalid verification flags created a trial control plane");
        await Prepare(config);
        Check(!File.Exists(Path.Combine(rootPath,"control","reviewers.json")),"Qualification wrote endpoint settings");
        await Reject(()=>Prepare(config));
        using(var session=ReviewQualification.Session.Open(rootPath)) {
            await session.ValidateFixtureAsync();
            Check(session.TargetsUnchanged(),"Trial targets changed before requests");
            Check(session.PublicPacket.Contains("public_addition_assertions") && session.PublicPacket.Contains("PASS"),"Actual public checks absent");
            foreach(var provider in ReviewQualification.Providers) {
                using var journal=session.Journal(provider);
                var policy=session.Store.RequireExportPolicy(journal.TaskId).Policy;
                Check(policy.Mode=="manual" && policy.WriteScope.Length==0,"Qualification loosened export/mutation policy");
                await Reject(()=>{session.RequireBoundary(provider);return Task.CompletedTask;});
                Check(session.Cases(provider).Length==0,"Preparation invented boundary PASS results");
                var regular=await ReviewerCli.InvokeAsync(provider,"PUBLIC normal dispatch must remain closed","high",default);
                Check(regular.Status=="UNAVAILABLE" && regular.Reason=="HOST_ROUTE_NOT_QUALIFIED","Qualification enabled normal dispatch");
                await Reject(()=>ReviewerCli.ProbeAsync(session,provider,"Arbitrary public packet is not an approved probe",default));
                var packet=session.ProbePacket(provider);
                Check(!packet.Contains("PUBLIC_CANARY_NOT_IN_PACKET"),"Canary contents entered the exported packet");
                var export=journal.PrepareExternalPacket(packet,provider);
                await Reject(()=>{using var lease=new HostEgress(session.Store).AuthorizeDispatch(export,provider);return Task.CompletedTask;});
                Check(session.Store.GetTask(journal.TaskId)!.ExternalReviews==0,"Unapproved probe started a model request");
                Check(!session.Events(provider).Any(e=>e.Input.EventType=="reviewer_cli_started"),"Unapproved probe spawned a CLI");
            }
            // Copilot has no auth preflight child: this exercises the real shared dispatch
            // up to HostEgress denial with a pinned EXE, without launching that EXE.
            using(var journal=session.Journal("copilot"))
                await Reject(()=>ReviewerCli.ProbeAsync(session,"copilot",session.ProbePacket("copilot"),default));
            Check(session.TargetsUnchanged(),"Unapproved dispatch had side effects");
        }
        await Reject(()=>ReviewQualification.RunAsync(["review",rootPath,"sol"]));
        await Reject(()=>ReviewQualification.RunAsync(["fallback",rootPath]));
        var sol=ReviewerCli.StartInfo(exe,parent,"sol","high");
        var opus=ReviewerCli.StartInfo(exe,parent,"copilot","high");
        Check(opus.Environment["COPILOT_HOME"]==Path.Combine(parent,"copilot-home") && opus.Environment["COPILOT_ALLOW_ALL"]=="false","Copilot configuration not isolated");
        Check(opus.ArgumentList.Contains("--sync-session=off") && !opus.ArgumentList.Contains("--no-remote") && !opus.ArgumentList.Contains("--no-remote-export"),"Unsupported Copilot flags remain");
        Check(opus.ArgumentList.Contains("--available-tools=") && opus.ArgumentList.Contains("--deny-tool=*"),"Probe relaxed production tool restrictions");
        foreach(var start in new[]{sol,opus})Check(!start.Environment.Keys.Any(k=>k.StartsWith("OPENAI_",StringComparison.OrdinalIgnoreCase) ||
            k.StartsWith("OTEL_",StringComparison.OrdinalIgnoreCase) || k.StartsWith("LOCALBRAIN_",StringComparison.OrdinalIgnoreCase) ||
            k is "GH_TOKEN" or "GITHUB_TOKEN" or "COPILOT_GITHUB_TOKEN" or "COPILOT_PROVIDER_BASE_URL"),"Inherited secret/capability environment");
        // Exact-byte session identity is anchored in immutable Canonical events.
        File.AppendAllText(Path.Combine(rootPath,"session.json")," ");
        await Reject(()=>{using var session=ReviewQualification.Session.Open(rootPath);return Task.CompletedTask;});
        Console.WriteLine(JsonSerializer.Serialize(new{status="PASS",checks,external_inference=false,
            production_changes=false,retained_trial_root=rootPath,scope="local admission/approval/isolation checks, NOT provider preventive-denial qualification"}));

        async Task Git(params string[] args) {
            var start=new ProcessStartInfo("git"){WorkingDirectory=repo,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add("-c");start.ArgumentList.Add("safe.directory="+repo);
            foreach(var arg in args)start.ArgumentList.Add(arg);
            using var process=Process.Start(start)!;process.StandardInput.Close();
            var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();await output;var diagnostic=await error;if(process.ExitCode!=0)throw new Exception("Public proof Git failed ("+process.ExitCode+"): "+diagnostic);
        }
    }
}
