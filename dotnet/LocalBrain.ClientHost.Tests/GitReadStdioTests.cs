using System.Diagnostics;
using System.Text.Json;
using LocalBrain.ClientHost;

internal static class GitReadStdioTests
{
    internal static async Task ChildAsync(string root,string projects)
    {
        var config=new ClientConfig("https://fixture.invalid",1,"unused",projects,Path.Combine(root,"unused-audit"));
        if(await Console.In.ReadLineAsync()!="resolve")throw new Exception("Unexpected fixture request");
        Console.WriteLine(new ProjectAllowlist(config).Resolve(root));
        if(await Console.In.ReadLineAsync()!="head")throw new Exception("Unexpected fixture request");
        Console.WriteLine(ProjectAllowlist.CurrentHead(root));
    }
    internal static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"git-stdio-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        Git(root,"init");Git(root,"-c","user.name=Fixture","-c","user.email=fixture@example.invalid","commit","--allow-empty","-m","Public fixture");
        var projects=Path.Combine(root,"projects.json");File.WriteAllText(projects,JsonSerializer.Serialize(new[]{root}));
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(typeof(GitReadStdioTests).Assembly.Location);
        start.ArgumentList.Add("--git-open-stdin-fixture");start.ArgumentList.Add(root);start.ArgumentList.Add(projects);
        using var child=Process.Start(start)!;var stderr=child.StandardError.ReadToEndAsync();
        try{
            // Keep the parent command stream open across both Git reads, as MCP does.
            await child.StandardInput.WriteLineAsync("resolve");await child.StandardInput.FlushAsync();
            var resolved=await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
            if(!string.Equals(Path.GetFullPath(resolved!),root,StringComparison.OrdinalIgnoreCase))throw new Exception("Git root differs");
            await child.StandardInput.WriteLineAsync("head");await child.StandardInput.FlushAsync();
            var head=await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
            if(head?.Length!=40)throw new Exception("Git HEAD response missing");
            child.StandardInput.Close();await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if(child.ExitCode!=0)throw new Exception(await stderr);
        }finally{if(!child.HasExited){child.Kill(entireProcessTree:true);child.WaitForExit(5000);}}
        Console.WriteLine("PASS_GIT_READS_WITH_PARENT_STDIN_OPEN: registered root and HEAD finish with two parent requests; no production data/API/model.");
    }
    private static void Git(string root,params string[] args)
    {
        var start=new ProcessStartInfo("git"){WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in args)start.ArgumentList.Add(arg);using var child=Process.Start(start)!;child.StandardInput.Close();
        var output=child.StandardOutput.ReadToEndAsync();var error=child.StandardError.ReadToEndAsync();
        if(!child.WaitForExit(10000)){child.Kill(entireProcessTree:true);throw new TimeoutException("Fixture Git initialization timed out");}
        Task.WhenAll(output,error).GetAwaiter().GetResult();if(child.ExitCode!=0)throw new Exception(error.Result);
    }
}
