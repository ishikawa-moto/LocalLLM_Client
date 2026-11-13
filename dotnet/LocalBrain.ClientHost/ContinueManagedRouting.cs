using System.Text.Json.Nodes;

namespace LocalBrain.ClientHost;

internal static class ContinueManagedRouting
{
    internal static byte[] Prepare(byte[] source)
    {
        var request=JsonNode.Parse(source)?.AsObject()??throw new InvalidDataException("Invalid chat request");
        if(request["tools"] is not JsonArray tools || !tools.Any(t=>Name(t)?.EndsWith("localbrain_agent_v2_investigate",StringComparison.Ordinal)==true))return source;
        // Capability discovery, not client-supplied labels, chooses the Continue integration.
        // The managed model relay does not advertise this MCP tool and keeps its own policy.
        var direct=new HashSet<string>(StringComparer.Ordinal){"read_file","read_currently_open_file","ls","grep_search","file_glob_search","search_codebase","run_terminal_command","create_new_file","edit_existing_file","single_find_and_replace","multi_edit"};
        var removed=new HashSet<string>();
        for(var i=tools.Count-1;i>=0;i--)if(Name(tools[i]) is {} name && direct.Contains(name)){removed.Add(name);tools.RemoveAt(i);}
        if(request["tool_choice"] is JsonObject choice && removed.Contains(Name(choice)??""))request.Remove("tool_choice");
        var messages=request["messages"]?.AsArray()??throw new InvalidDataException("Missing messages");
        messages.Insert(0,new JsonObject{["role"]="system",["content"]=
            "Repository investigation must use localbrain_agent_v2_investigate with read_only=true and the exact original user goal and completion criteria. " +
            "Host owns the task, evidence, finite continuation and final report delivery. Use LOW risk, no command_execution, required_tests=[{kind:git_diff_check}]; allowed_files lists relevant relative paths. " +
            "For changes/commands keep the existing managed run/native approval policy. Never substitute a plan or promise for a tool execution/report. " +
            "A progress question does not cancel or replace the original task; inspect the current task status. Explicit user cancellation uses the exact task ID. " +
            "Do not reconstruct an active Host task goal with SecondBrain. Report blocked/unknown states honestly; never replay unknown operations."});
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request);
    }
    private static string? Name(JsonNode? tool)=>tool?["function"]?["name"]?.GetValue<string>();
}
