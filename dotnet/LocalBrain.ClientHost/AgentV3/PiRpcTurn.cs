using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LocalBrain.ClientHost.AgentV3
{
    internal sealed class PiRpcTurn
    {
        private const int MaxPromptUtf8Bytes = 100000;

        private readonly Stream _output;
        private readonly PiJsonlReader _reader;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private volatile bool _unusable;

        public PiRpcTurn(Stream input, Stream output, int maxRecordBytes)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));
            if (maxRecordBytes <= 0 || maxRecordBytes > 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(maxRecordBytes));
            _output = output ?? throw new ArgumentNullException(nameof(output));
            _reader = new PiJsonlReader(input, maxRecordBytes);
        }

        // Single use: the caller supplies a fresh task-bound process and exclusive
        // streams. Separate wrappers cannot enforce stream ownership. Observers must
        // honor cancellation; an asynchronous observer may outlive a cancelled wait,
        // and its owner must observe eventual faults. Events are untrusted observations.
        public async Task RunPromptAsync(string id, string message, Func<JsonElement, CancellationToken, Task> observe, CancellationToken token)
        {
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("id must not be null or empty.", nameof(id));
            if (string.IsNullOrEmpty(message))
                throw new ArgumentException("message must not be null or empty.", nameof(message));
            if (Encoding.UTF8.GetByteCount(message) > MaxPromptUtf8Bytes)
                throw new ArgumentException("message exceeds maximum length.", nameof(message));
            if (observe == null)
                throw new ArgumentNullException(nameof(observe));
            if (_unusable)
                throw new InvalidOperationException("This PiRpcTurn instance is unusable after a failed or interrupted turn.");

            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_unusable)
                    throw new InvalidOperationException("This PiRpcTurn instance is single-use.");

                _unusable = true;
                try
                {
                    token.ThrowIfCancellationRequested();
                    await SendPromptAsync(id, message, token).ConfigureAwait(false);
                    bool responseAccepted = false;

                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        JsonElement record = await _reader.ReadAsync(token).ConfigureAwait(false)
                            ?? throw new EndOfStreamException("Pi exited before the turn settled.");
                        if (record.ValueKind != JsonValueKind.Object)
                            throw new InvalidDataException("Record is not an object.");

                        string type = GetRequiredString(record, "type");
                        if (type == "response")
                        {
                            if (responseAccepted)
                                throw new InvalidDataException("Duplicate response received.");
                            if (GetRequiredString(record, "id") != id)
                                throw new InvalidDataException("Response id mismatch.");
                            if (GetRequiredString(record, "command") != "prompt")
                                throw new InvalidDataException("Response command mismatch.");
                            if (!GetRequiredBool(record, "success"))
                                throw new InvalidDataException("Response success is false.");
                            if (!record.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                                GetRequiredString(data, "disposition") != "started")
                                throw new InvalidDataException("Prompt did not start a new agent run.");
                            responseAccepted = true;
                        }
                        else
                        {
                            if (!responseAccepted)
                                throw new InvalidDataException("Event received before response acceptance.");
                            await observe(record, token).WaitAsync(token).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            if (type == "agent_settled")
                                return;
                        }
                    }
                }
                catch
                {
                    _unusable = true;
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task SendPromptAsync(string id, string message, CancellationToken token)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("id", id);
                writer.WriteString("type", "prompt");
                writer.WriteString("message", message);
                writer.WriteEndObject();
                writer.Flush();
            }
            buffer.WriteByte((byte)'\n');
            byte[] payload = buffer.ToArray();
            await _output.WriteAsync(payload, 0, payload.Length, token).ConfigureAwait(false);
            await _output.FlushAsync(token).ConfigureAwait(false);
        }

        private static string GetRequiredString(JsonElement record, string key)
        {
            if (!record.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"Property '{key}' is missing or is not a string.");
            string result = value.GetString()!;
            if (key == "type" && result.Length == 0)
                throw new InvalidDataException("Property 'type' is empty.");
            return result;
        }

        private static bool GetRequiredBool(JsonElement record, string key)
        {
            if (!record.TryGetProperty(key, out var value) ||
                (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False))
                throw new InvalidDataException($"Property '{key}' is missing or is not a boolean.");
            return value.ValueKind == JsonValueKind.True;
        }
    }
}
