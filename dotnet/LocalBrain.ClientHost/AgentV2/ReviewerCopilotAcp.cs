using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

internal sealed class ReviewerCopilotAcp : IDisposable
{
    private readonly WindowsChildJob job;
    private readonly CancellationTokenSource deadline;
    private readonly StringBuilder answer = new();
    private readonly char[] character = new char[1];
    private string? sessionId;
    private int outputChars;
    private bool toolNoticeRead;
    internal int Pid => job.Process.Id;

    private ReviewerCopilotAcp(ProcessStartInfo start, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        start.ArgumentList.Insert(0, "--acp");
        job = WindowsChildJob.StartReviewerBootstrap(start);
        deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        // Diagnostics can contain provider details. Drain without retaining them.
        _ = DrainErrorAsync();
    }

    private async Task DrainErrorAsync()
    {
        try { await job.Error.BaseStream.CopyToAsync(Stream.Null, deadline.Token); }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    internal static Task<ReviewerCopilotAcp> OpenAsync(ProcessStartInfo start, CancellationToken token) =>
        OpenAsync(start, token, localFixture: false);

    internal static Task<ReviewerCopilotAcp> OpenLoopbackFixtureAsync(ProcessStartInfo start, CancellationToken token)
    {
        if (!start.Environment.TryGetValue("COPILOT_OFFLINE", out var offline) || offline != "true" ||
            !start.Environment.TryGetValue("COPILOT_PROVIDER_BASE_URL", out var endpoint) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1")
            throw new UnauthorizedAccessException("ACP fixture requires the offline loopback provider");
        return OpenAsync(start, token, localFixture: true);
    }

    private static async Task<ReviewerCopilotAcp> OpenAsync(ProcessStartInfo start, CancellationToken token, bool localFixture)
    {
        var acp = new ReviewerCopilotAcp(start, token);
        try {
            var init = await acp.CallAsync(1, "initialize", new { protocolVersion = 1, clientCapabilities = new { } });
            if (!init.TryGetProperty("protocolVersion", out var version) || version.GetInt32() != 1)
                throw new InvalidDataException("Unsupported reviewer ACP protocol");
            var parameters = new { cwd = start.WorkingDirectory, mcpServers = Array.Empty<object>() };
            var bootstrap = RequireModel(await acp.CallAsync(2, "session/new", parameters), localFixture);
            await acp.job.SealReviewerBootstrapAsync(acp.deadline.Token);
            acp.sessionId = RequireModel(await acp.CallAsync(3, "session/new", parameters), localFixture);
            if (acp.sessionId == bootstrap) throw new InvalidDataException("ACP reused the authentication session");
            acp.job.RequireSealedReviewer();
            return acp;
        } catch {
            acp.Dispose();
            throw;
        }
    }

    private static string RequireModel(JsonElement result, bool localFixture)
    {
        var expected = ReviewerCli.Model("copilot");
        var session = result.GetProperty("sessionId").GetString();
        string? model = null;
        if (result.TryGetProperty("models", out var models) && models.TryGetProperty("currentModelId", out var current))
            model = current.GetString();
        if (result.TryGetProperty("configOptions", out var options) && options.ValueKind == JsonValueKind.Array) {
            foreach (var option in options.EnumerateArray()) {
                if (!option.TryGetProperty("category", out var category) || category.GetString() != "model") continue;
                var selected = option.GetProperty("currentValue").GetString();
                if (model is not null && model != selected) throw new InvalidDataException("Conflicting ACP reviewer model");
                model = selected;
            }
        }
        // Offline ACP does not publish the subscription model catalogue. The fixture observes
        // the model ID in the loopback request itself. Real dispatch always requires a selection.
        if (string.IsNullOrWhiteSpace(session) || (model != expected && !(localFixture && model is null)))
            throw new InvalidDataException("ACP did not select the expected reviewer model");
        return session;
    }

    internal async Task<ReviewerCli.Reply> ReviewAsync(string input)
    {
        if (sessionId is null) throw new InvalidOperationException("Reviewer ACP session has not been sealed");
        job.RequireSealedReviewer();
        var result = await CallAsync(4, "session/prompt", new {
            sessionId, prompt = new[] { new { type = "text", text = input } }
        });
        job.Input!.Dispose();
        await job.Process.WaitForExitAsync(deadline.Token);
        return result.GetProperty("stopReason").GetString() == "end_turn" && answer.Length > 0 &&
            Encoding.UTF8.GetByteCount(answer.ToString()) <= 8_000 && job.Process.ExitCode == 0
            ? new("OK", answer.ToString(), ExitCode: job.Process.ExitCode)
            : new("INVALID", Reason: "INCOMPLETE_ACP_REVIEW");
    }

    private async Task<JsonElement> CallAsync(int id, string method, object parameters)
    {
        var frame = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }) + "\n";
        await job.Input!.WriteAsync(Encoding.UTF8.GetBytes(frame), deadline.Token).AsTask().WaitAsync(deadline.Token);
        while (true) {
            using var document = JsonDocument.Parse(await ReadFrameAsync());
            var message = document.RootElement;
            if (!message.TryGetProperty("jsonrpc", out var protocol) || protocol.GetString() != "2.0")
                throw new InvalidDataException("Invalid reviewer ACP frame");
            if (message.TryGetProperty("method", out var notification)) {
                if (message.TryGetProperty("id", out _) || notification.GetString() != "session/update")
                    throw new InvalidDataException("Reviewer requested a client capability");
                var payload = message.GetProperty("params");
                var update = payload.GetProperty("update");
                var type = update.GetProperty("sessionUpdate").GetString();
                if (type is "tool_call" or "tool_call_update" or "user_message_chunk")
                    throw new InvalidDataException("Reviewer emitted an unauthorized ACP update");
                if (type == "agent_message_chunk") {
                    if (id != 4 || payload.GetProperty("sessionId").GetString() != sessionId)
                        throw new InvalidDataException("Reviewer emitted a message outside the sealed turn");
                    var content = update.GetProperty("content");
                    if (content.GetProperty("type").GetString() != "text")
                        throw new InvalidDataException("Unexpected reviewer content type");
                    var text = content.GetProperty("text").GetString() ?? "";
                    // This pinned CLI sends its initial disabled-tools notice as a separate
                    // agent message chunk even in silent mode. Preserve all other answer bytes.
                    const string notice = "Info: Disabled tools: ";
                    if (!toolNoticeRead && answer.Length == 0 && text.StartsWith(notice, StringComparison.Ordinal) &&
                        text.Length is > 22 and < 2_000 &&
                        text[notice.Length..].All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or ',' or ' ' or '-')) {
                        toolNoticeRead = true;
                        continue;
                    }
                    answer.Append(text);
                    if (answer.Length > 8_000) throw new InvalidDataException("Reviewer answer exceeds budget");
                }
                continue;
            }
            if (!message.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id)
                throw new InvalidDataException("Unexpected reviewer ACP response ID");
            if (message.TryGetProperty("error", out _)) throw new IOException("Reviewer ACP request failed: " + method);
            return message.GetProperty("result").Clone();
        }
    }

    private async Task<string> ReadFrameAsync()
    {
        var line = new StringBuilder();
        while (await job.Output.ReadAsync(character.AsMemory(), deadline.Token).AsTask().WaitAsync(deadline.Token) != 0) {
            if (++outputChars > 96_000) throw new InvalidDataException("Reviewer ACP output exceeds budget");
            if (character[0] == '\n') return line.ToString();
            line.Append(character[0]);
        }
        throw new IOException("Reviewer ACP output ended before the reply");
    }

    public void Dispose()
    {
        deadline.Cancel();
        job.Dispose();
        deadline.Dispose();
    }
}
