using LocalBrain.ClientHost;
using LocalBrain.ClientHost.AgentV2;

try
{
    if(args.FirstOrDefault()=="ocr"){
        await LocalBrain.ClientHost.Ocr.OcrCommands.RunAsync(args.Skip(1).ToArray());return;
    }
    if (args.FirstOrDefault() == "host-action")
    {
        await SideEffectGate.RunRelayAsync();
        return;
    }
    var config = ClientConfig.Load();
    var audit = new Audit(config);
    var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "serve";
    if (command == "register-project")
    {
        Console.WriteLine(new ProjectAllowlist(config).Register(args.ElementAtOrDefault(1) ?? Environment.CurrentDirectory));
    }
    else if (command == "mcp")
    {
        McpStdio.Configure();
        using var gateway = new GatewayClient(config);
        await new McpServer(gateway,config).RunAsync();
    }
    else if (command == "review")
    {
        await new ReviewRunner(config, new ProjectAllowlist(config), audit).RunAsync(args.Skip(1).ToArray());
    }
    else if (command == "agent-v2")
    {
        await AgentV2Commands.RunAsync(args.Skip(1).ToArray(), new ProjectAllowlist(config), config);
    }
    else if (command == "agent-v2-mcp")
    {
        await new AgentV2McpServer(new ProjectAllowlist(config), config).RunAsync();
    }
    else if (command == "agent-task-worker")
    {
        await TaskAgentCoordinator.RunWorkerAsync(args.ElementAtOrDefault(1)??throw new ArgumentException("Managed task directory is required"),config);
    }
    else if (command == "model-relay")
    {
        await ModelRelay.RunAsync(config.BridgePort);
    }
    else if (command == "serve")
    {
        using var gateway = new GatewayClient(config); using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
        await new Bridge(config, gateway, audit).RunAsync(stop.Token);
    }
    else throw new ArgumentException("Commands: serve, mcp, review, agent-v2, agent-v2-mcp, model-relay, register-project");
}
catch (Exception error)
{
    Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { error = error.Message, cause = error.GetType().Name,
        timestamp = DateTimeOffset.UtcNow }));
    Environment.ExitCode = 1;
}
