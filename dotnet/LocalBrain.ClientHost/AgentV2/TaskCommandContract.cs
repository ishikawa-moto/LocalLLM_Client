using System.Text.Json.Serialization;

namespace LocalBrain.ClientHost.AgentV2;

// This is the requested scope, NOT a grant. Only the native Host confirmation grants it.
internal sealed record TaskCommandContract(
    [property:JsonPropertyName("environments")] string[] Environments,
    [property:JsonPropertyName("working_directories")] string[] WorkingDirectories,
    [property:JsonPropertyName("output_directories")] string[] OutputDirectories,
    [property:JsonPropertyName("max_command_seconds")] int MaxCommandSeconds=900,
    [property:JsonPropertyName("max_task_minutes")] int MaxTaskMinutes=240,
    [property:JsonPropertyName("max_actor_turns")] int MaxActorTurns=60,
    [property:JsonPropertyName("max_commands")] int MaxCommands=256,
    [property:JsonPropertyName("max_output_bytes")] int MaxOutputBytes=2097152)
{
    internal void Validate()
    {
        if(Environments is null or {Length:0 or >2} || Environments.Any(e=>e is not ("windows" or "wsl")) ||
            Environments.Distinct().Count()!=Environments.Length || WorkingDirectories is null or {Length:0 or >20} ||
            WorkingDirectories.Any(p=>!Relative(p)) || OutputDirectories is null or {Length:>20} ||
            OutputDirectories.Any(p=>string.IsNullOrWhiteSpace(p)||p.Length>500||p.IndexOfAny(['\0','\r','\n'])>=0) ||
            MaxCommandSeconds is <1 or >86400 || MaxTaskMinutes is <1 or >1440 || MaxActorTurns is <3 or >300 ||
            MaxCommands is <1 or >1024 || MaxOutputBytes is <4096 or >8388608)
            throw new InvalidDataException("Invalid task command scope or finite budgets");
    }
    internal static bool Relative(string p)=>!string.IsNullOrWhiteSpace(p)&&p.Length<=260&&!Path.IsPathRooted(p)&&
        p.IndexOfAny(['\0','\r','\n'])<0&&(p=="."||!p.Split('/','\\').Any(s=>s is ".." or ".git" or ".localbrain"));
    internal string[] OutputRoots(string root)=>OutputDirectories.Select(p=>Path.GetFullPath(p,root)).ToArray();
}

internal sealed record TaskCommandRequest(string OperationId,string Environment,string Program,string[] Arguments,
    string WorkingDirectory=".",string[]? ExpectedOutputs=null,int? TimeoutSeconds=null)
{
    internal void Validate(TaskCommandContract contract)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(OperationId??"",@"^[A-Za-z0-9_-]{1,80}$") ||
            !contract.Environments.Contains(Environment) || string.IsNullOrWhiteSpace(Program)||Program.Length>500||Program.IndexOfAny(['\0','\r','\n'])>=0 ||
            Arguments is null or {Length:>500} || Arguments.Sum(a=>a?.Length??0)>100000 || Arguments.Any(a=>a is null||a.Contains('\0')) ||
            !TaskCommandContract.Relative(WorkingDirectory) || ExpectedOutputs is {Length:>256} || (ExpectedOutputs?.Sum(p=>p?.Length??0)??0)>32000 ||
            ExpectedOutputs?.Any(p=>string.IsNullOrWhiteSpace(p)||p.Length>500||p.IndexOfAny(['\0','\r','\n'])>=0)==true ||
            TimeoutSeconds is {} s && (s<1||s>contract.MaxCommandSeconds))throw new InvalidDataException("Command exceeds the task's proposed contract");
    }
}
