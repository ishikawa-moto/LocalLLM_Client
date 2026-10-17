# Local context continuation

The ServerPC inference engine remains at **32,768 tokens**. The ClientPC TaskMemory
model and its native context/KV settings are read from the existing Host-owned
manifest; this feature does not change them. Continue may retain a **65,536-token
logical history**. That number is not the ServerPC inference context size.

## Agent v2

The existing Agent path measures the final provider payload before mutation,
checkpoints canonical task state before context exhaustion, rotates the Actor
session, retrieves same-task source references through TaskMemory, and resumes
with the same task identity, tests and approval gates. No KV snapshot is authority.

## Ordinary Continue conversations

When `continue-context-memory.json` in the Windows Host control directory has
`{"enabled":true}`, Bridge measures OpenAI chat payloads with the same qualified
local Qwen tokenizer/template used by Agent v2. Requests at or below 24,576 prompt
tokens pass unchanged. Above the threshold:

1. Preserve the exact incoming request in Host-local, content-addressed storage.
2. Keep system/developer messages, the original user request, the current user
   request and the newest complete assistant/tool exchange exactly.
3. Rank historical messages from **this exact request only**, using existing
   full-text source retrieval. The ClientPC native TaskMemory selects up to eight
   existing IDs from at most 16 source candidates. It cannot invent source text,
   execute commands, approve changes or certify completion.
4. Include bounded original source excerpts, retaining their unverified status.
   Re-measure the final payload including tool schemas and chat framing.
5. Forward once only after the prompt fits the admission threshold with output
   and safety reserves. The ordinary ServerPC response/stream continues to the
   same Continue conversation. Tool calls are not replayed.

This is retrieval from durable source data, not moving the ServerPC KV cache or
concatenating all 32K+64K tokens into the ServerPC model. It is deliberately lossy
selection with an immutable original archive, not a guarantee that every past
detail is present in every turn. It cannot recover history that Continue removed
before it reached Bridge, and it does not mix unrelated conversations or Agent
tasks. The 64K client history setting reduces premature client-side pruning but
does not make history unlimited. Search candidates are a bounded subset ranked
from the complete current payload; native selection verifies identity, not truth.

## Failure and operations

Each measurement has a30-second deadline including WSL mapping/startup; the
complete local admission/retrieval phase has an18-minute absolute deadline.
Native completion also retains its existing token-dependent deadline. A timeout
returns an explicit nonretryable admission failure rather than looping indefinitely.

An interrupted upstream response is an incomplete transport, not completion.
Bridge logs whether response headers arrived, aborts an incomplete body/stream,
and never synthesizes a successful SSE `[DONE]` or replays tool calls. The response
header `X-LocalBrain-Response-Contract` states this contract. If dispatch may have
occurred but no upstream response arrived, HTTP 422 `dispatch_outcome_unknown`
requires observation of the existing request rather than an automatic resend.

If measurement, memory selection, native admission or storage fails, the chat
request is not forwarded. Bridge returns HTTP 422 `context_handoff_unavailable`
with `automatic_retry:false` (not a retryable 5xx/409) and logs
only bounded metadata. It does not silently discard source data or retry a tool.
If pinned current instructions/tools alone exceed the budget, the caller must
reduce that input. Existing archives are not replaced or automatically deleted.
Storage has a 256 MiB bound; housekeeping requires explicit retention decisions.

Originals and derived receipts are under the Host control directory's
`continue-memory/` folder, outside the Git source checkout. Receipts bind source
and forwarded hashes, measured token counts, selected IDs and native diagnostics.
Identical input/profile/candidate sets may reuse previously validated source IDs;
the actor payload is re-measured and reassembled from the exact source each time.
This derived cache never reuses a tool execution or an approval. New requests or
changed native profiles do not share selections.
`X-LocalBrain-Context-Recall` and `X-LocalBrain-Actor-Prompt-Tokens` response headers
identify a real recall and its measured forwarded size without exposing content.
Existing authentication, execution confirmations, write/export scopes, SecondBrain
adoption policy and ServerPC model configuration remain unchanged.
