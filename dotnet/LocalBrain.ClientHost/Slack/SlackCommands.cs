using System.Text.Json;
using LocalBrain.ClientHost.Ocr;

namespace LocalBrain.ClientHost.Slack;

internal static class SlackCommands
{
    internal static async Task Run(string[] args)
    {
        string Option(string name){var i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:throw new ArgumentException("SLACK_ARGUMENT_REQUIRED");}
        var operation=args.FirstOrDefault()??"capabilities";
        if(operation=="capabilities"){
            Console.WriteLine(JsonSerializer.Serialize(new{feature="slack",enabled=false,configuration_required=true,external_ai="deny",arbitrary_commands=false,automatic_adoption=false}));return;
        }
        var options=SlackOptions.Load(Option("--config"));if(!options.Enabled)throw new InvalidOperationException("SLACK_DISABLED");
        using var stop=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
        Directory.CreateDirectory(options.Root);
        using var owner=new FileStream(Path.Combine(options.Root,"host.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        using var store=new SlackStore(options);
        if(operation=="approvals"){
            var web=JsonSerializer.Deserialize<SlackApprovalOptions>(File.ReadAllBytes(Option("--web-config")),SlackOptions.Json)??throw new InvalidDataException("SLACK_WEB_CONFIG_INVALID");
            await new SlackApprovalWeb(web,store).Run(stop.Token);return;
        }
        if(operation!="serve")throw new ArgumentException("SLACK_OPERATION_INVALID");
        var ocr=OcrConfig.Read()!;using var api=new SlackApi(options);using var gateway=new GatewayClient(ClientConfig.Load());
        var priority=new SlackPriority(ocr);var processor=new SlackProcessor(options,store,api,new SlackLocalInference(gateway,store),ocr,priority);
        store.Recover();
        var tasks=new List<Task>{new SlackSocket(options,store,api).Run(stop.Token),SlackSocket.Replies(store,api,stop.Token),processor.Run(stop.Token),processor.Maintenance(stop.Token),new SlackApplicationWorker(store,gateway).Run(stop.Token)};
        if(args.Contains("--web-config")){
            var web=JsonSerializer.Deserialize<SlackApprovalOptions>(File.ReadAllBytes(Option("--web-config")),SlackOptions.Json)??throw new InvalidDataException("SLACK_WEB_CONFIG_INVALID");
            tasks.Add(new SlackApprovalWeb(web,store).Run(stop.Token));
        }
        try{var ended=await Task.WhenAny(tasks);await ended;}
        finally{stop.Cancel();try{await Task.WhenAll(tasks);}catch(OperationCanceledException){}}
    }
}
