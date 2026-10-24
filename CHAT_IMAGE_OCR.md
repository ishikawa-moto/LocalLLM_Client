# Browser Chat image OCR relay (candidate)

The browser-facing Server UI and authenticated Gateway mailbox must be implemented and qualified together with this Client relay. This Client candidate alone does not add an upload button to the running Chat. Production is unchanged and the feature is disabled when its configuration file is absent.

## Reused processing and default output

Images/PDFs use the existing OCR job store/scheduler, Japanese model, CPU2/704MiB, AppContainer, no-network worker and priority/checkpoint behavior. No model, new OCR engine, inbound port, certificate or Cloud AI is added. Default output is Markdown; table results also include canonical JSON. HTML/CSV are explicit formats. The same deterministic renderer is used as Slack. OCR-only work does not invoke Qwen; requested interpretation belongs to the Server's local-only Chat path.

A separate, optional `control-plane/chat-ocr/config.json` has `enabled` and an absolute private non-Git `root`. No production file is created by this change. While absent or disabled the background task makes zero Gateway/OCR calls.

## Proposed wire contract

PC73's owning Bridge uses only authenticated `POST /v1/chat-ocr/next` and `/v1/chat-ocr/update`, the configured existing Host certificate/owner header, normal private-LAN TLS, no proxy and no redirects. Ordinary Bridge forwarding rejects these paths, including escaped/case variants. The Gateway must enforce the pinned Host/IP/owner and a separate authenticated loopback Chat role for intake/status/cancel/artifacts.

`next` accepts `{"schema":"localbrain.chat-ocr.poll.v1"}` and returns `{"job":null}` or `{"job":{...}}`. Job schema `localbrain.chat-ocr.job.v1` has exactly `schema`, lowercase32hex `job_id`, UUID-D `conversation_id`, lowercase SHA256 `input_sha256`, `extension`, `formats`, optional base64 `bytes`, and `cancel_requested`. Supported extensions are `.png/.jpg/.webp/.pdf`, with magic/hash validation and at most4MiB decoded bytes. `formats` includes `md` and may include `json/html/csv`. No request chooses a URL, input path, output path, executable or tool. Unknown fields are rejected.

`update` schema `localbrain.chat-ocr.update.v1` binds `job_id`/`input_sha256` and a known OCR state. Progress has page counts/wait reason. Terminal completion includes the canonical OCR `result`, deterministic `artifacts` (format/filename/MIME/SHA256/base64bytes), warnings, external_ai=deny and automatic_knowledge_adoption=false. Each artifact is at most1MiB and the report is bounded to16MiB. Failed/cancelled/expired states contain no result files. The Server must revalidate source/conversation/Host binding, hashes, terminal idempotency, cancellation/expiry and allowed output formats before publishing. It must not assume that a cancellation ACK proves an OCR child has quiesced.

## Restart and privacy

The logical job deterministically selects a single existing OCR request with origin=chat. A private receipt binds conversation/input/formats before polling results. Duplicate polls and a lost terminal response return the frozen report; they do not rerun OCR. Cancel requests act on the owned OCR job and wait for cancellation state. Cancellation after local completion does not overwrite frozen result evidence. Unknown/corrupt receipts are kept for inspection.

Source staging is deleted after the OCR-owned snapshot is registered. The existing scheduler handles checkpoint/resume, resource limits and expiry. The relay's terminal cache has a24h cleanup and256MiB quota; the Server mailbox must have its own bounded retention/tombstone rules so expired IDs cannot be re-admitted. Metadata logs contain fixed codes only. Image text is never a Host command or a knowledge-adoption approval.

## Qualification

`dotnet run --project dotnet/LocalBrain.ClientHost.Tests -c Release -- --chat-ocr-proof` checks source formats/hash/magic, unknown/path fields, source authority, external/agent denial, queue reuse, output defaults, complete formats, cancellation/restart/lost responses, corruption preservation, fixed route/auth binding and expiry using public fixtures and fake HTTP. Existing OCR store/artifact and Slack file-delivery proofs are also run. These are isolated contract tests; they do not qualify real Gateway interoperability, browser upload, deployment or real OCR performance.
