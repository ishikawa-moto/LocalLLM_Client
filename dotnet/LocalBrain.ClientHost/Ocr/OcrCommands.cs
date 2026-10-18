using System.Text.Json;

namespace LocalBrain.ClientHost.Ocr;

// Fixed local interface. No shell, model prompts, network URLs, Slack or knowledge adoption.
internal static class OcrCommands
{
    internal static Task RunAsync(string[] args){
        McpStdio.Configure();
        try{
            var command=args.FirstOrDefault()??"capabilities";
            var config=OcrConfig.Read(requireEnabled:command!="capabilities");
            if(command=="capabilities"){
                Output(new{enabled=config is not null,profiles=config?.QualifiedProfiles??[],experimental_profiles=config?.ExperimentalProfiles??[],external_ai="deny",automatic_knowledge_adoption=false,interface_version=1});return Task.CompletedTask;
            }
            var store=new OcrJobStore(config!);
            string Option(string name){var index=Array.IndexOf(args,name);if(index<0||index+1>=args.Length)throw new OcrException("ARGUMENT_REQUIRED");return args[index+1];}
            if(command=="submit"){
                var state=store.Submit(Option("--input"),Option("--sha256"),Option("--profile"),Option("--request-id"),args.Contains("--experimental"),args.Contains("--retain-original"));
                Output(View(store,state));
            }else{
                var id=Option("--job-id");
                switch(command){
                    case "status":Output(View(store,store.GetState(id)));break;
                    case "cancel":Output(View(store,store.Cancel(id)));break;
                    case "resume":Output(View(store,store.Resume(id)));break;
                    case "result":
                        var result=store.Result(id);
                        if(args.Contains("--text"))Console.WriteLine(result.GetProperty("text").GetString());else Output(result);
                        break;
                    default:throw new OcrException("OPERATION_INVALID");
                }
            }
        }catch(Exception error)when(error is OcrException or IOException or JsonException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException or InvalidOperationException){
            Output(new{status="rejected",error_code=error is OcrException o?o.Code:error is IOException?"STATE_BUSY_OR_UNAVAILABLE":"OCR_REQUEST_INVALID",external_ai="deny"});Environment.ExitCode=2;
        }
        return Task.CompletedTask;
    }
    private static object View(OcrJobStore store,OcrJobStore.State state){
        var m=store.GetManifest(state.JobId);
        var scheduler="not_running";
        var path=Path.Combine(store.Root,"scheduler.lock");
        if(File.Exists(path)){
            try{using var lease=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException e)when((e.HResult&65535)==32){scheduler="running";}
        }
        return new{job_id=state.JobId,input_file_ref=m.InputFileRef,input_sha256=m.InputSha256,profile=m.Profile,status=state.Status,page_count=state.PageCount,completed_pages=state.CompletedPages,created_at=state.CreatedAt,updated_at=state.UpdatedAt,result_ref=state.ResultRef,error_code=state.ErrorCode,scheduler_status=scheduler,wait_reason=scheduler=="not_running"&&(state.Status is "queued" or "paused" or "running")?"OCR_HOST_NOT_RUNNING":state.ErrorCode,input_snapshot_available=File.Exists(store.Input(m)),input_cleanup_pending=File.Exists(Path.Combine(store.DirectoryFor(state.JobId),"input-cleanup-pending.json")),retain_original=m.RetainOriginal,experimental=m.Experimental,external_ai=m.ExternalAi,automatic_knowledge_adoption=false};
    }
    private static void Output(object value)=>Console.WriteLine(JsonSerializer.Serialize(value,OcrJobStore.Json));
}
