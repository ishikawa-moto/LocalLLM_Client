using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV3
{
    internal sealed class PiRunOutcome
    {
        public bool Settled { get; private set; }
        private bool _lastAssistantStopped;
        private bool _invalidEventObserved;
        public bool CompletedWithoutModelError => Settled && _lastAssistantStopped && !_invalidEventObserved;
        public bool ToolErrorObserved { get; private set; }

        public void Observe(JsonElement record)
        {
            try
            {
                if (Settled) throw new InvalidOperationException("Cannot observe an event after settlement.");
                if (record.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Pi event must be an object.");

                if (!record.TryGetProperty("type", out JsonElement type) ||
                    type.ValueKind != JsonValueKind.String ||
                    string.IsNullOrEmpty(type.GetString()))
                    throw new InvalidDataException("Pi event type is missing or invalid.");

                switch (type.GetString())
                {
                    case "message_end":
                        ObserveMessageEnd(record);
                        break;
                    case "agent_settled":
                        Settled = true;
                        break;
                    case "tool_execution_end":
                        if (!record.TryGetProperty("isError", out JsonElement isError) ||
                            (isError.ValueKind != JsonValueKind.True && isError.ValueKind != JsonValueKind.False))
                            throw new InvalidDataException("Pi tool outcome is missing or invalid.");
                        if (isError.ValueKind == JsonValueKind.True)
                            ToolErrorObserved = true;
                        break;
                }
            }
            catch (InvalidDataException)
            {
                _invalidEventObserved = true;
                throw;
            }
            catch (InvalidOperationException)
            {
                _invalidEventObserved = true;
                throw;
            }
        }

        private void ObserveMessageEnd(JsonElement record)
        {
            if (!record.TryGetProperty("message", out JsonElement message) ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("role", out JsonElement role) ||
                role.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(role.GetString()))
            {
                _lastAssistantStopped = false;
                throw new InvalidDataException("Pi message is missing or invalid.");
            }

            if (role.GetString() != "assistant")
                return;

            if (!message.TryGetProperty("stopReason", out JsonElement stopReason) ||
                stopReason.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(stopReason.GetString()))
            {
                _lastAssistantStopped = false;
                throw new InvalidDataException("Pi assistant stop reason is missing or invalid.");
            }
            _lastAssistantStopped = stopReason.GetString() == "stop";
        }
    }
}
