# Slack integration

This feature is disabled by default in the public templates. An operator-approved installation has now passed actual public Slack mention, acknowledgement and completion-reply checks. Deployment and live qualification are separate from merging this Draft PR; the PR does not enable another installation automatically. The existing coding, OCR, TaskMemory and Laya settings are preserved. See `SLACK_VALIDATION.md` for confirmed scope and remaining gates.

## Boundaries

ClientPC owns Socket Mode intake, the restricted SQLite session/job/outbox store and authenticated HTTPS approval. ServerPC provides authenticated, local, lower-priority inference and local Reviewer operations. A Slack request has no terminal, Git, code-editing, admin, external-review or cloud-fallback capability.

Every derived record remains `origin=slack`, `data_class=slack_restricted`, `external_ai=deny`, `cloud_fallback=false`. Slack state and input/output stay outside repositories, the ordinary TaskMemory corpus and automatic SecondBrain adoption. Ordinary work retains its review routing. Slack itself is a cloud service: posting an image in Slack uploads it to Slack; local AI processing does not undo that upload.

## Data and authority flow

```mermaid
flowchart LR
  Slack[Slack cloud: allowed channel] <-->|Socket Mode and official API| Host[ClientPC Windows Host]
  Host --> Store[Restricted sessions / jobs / outboxes]
  Host --> OCR[Existing local OCR: preemptible]
  Host --> Memory[Existing Saluki 128K: lowest priority]
  Host <-->|Private LAN mTLS| Gateway[ServerPC local Gateway / Qwen 32K]
  Gateway --> Proposals[Restricted local review and proposals]
  Browser[LAN PC / phone] -->|Trusted HTTPS + password login| Approval[Host approval page]
  Approval -->|Exact human-bound receipt| Host
  Host -->|One reserved application| Proposals
  Foreground[Continue / Agent / TaskMemory / Laya] -->|Priority and preemption| Host
```

This diagram describes the implemented optional path. The public templates remain disabled; the separately approved installation has qualified a public text-only Slack round trip. Slack itself stores uploads in its cloud; OCR, local inference, summaries and private result/proposal storage do not use external AI. The approval page runs on ClientPC. The ServerPC chat sidebar links directly to its trusted HTTPS address; the HTTP chat is not an approval proxy. On the approved installation the link appears below the new-chat button after reloading an already open page.

## Slack administrator setup

1. Create an internal Slack app using `tools/slack/app-manifest.yaml`. Enable Socket Mode and create an app-level token with `connections:write`.
2. Install the app in the intended workspace. Invite the bot to the sole permitted public, non-shared channel. No DM or other channels are accepted.
3. Obtain the real team ID, channel ID and bot user ID. Authorize IDs, never display names. `auth.test` and `conversations.info` verify identity and membership before receiving events.
4. Copy the disabled settings template to a private Host-owned directory outside Git. Replace paths/IDs. Run `Set-SlackTokens.ps1` in a local Windows terminal; token entry is concealed and saved in user/SYSTEM-only files. Never paste tokens in chat, commands, screenshots or Git.
5. Keep the feature disabled until both Client and Server candidates have passed their integration checks and the runtime/rollback plan has been approved.

The bot subscribes only to `app_mention`; explicit `<@BOT_USER_ID>` is required on every request. The manifest requests `app_mentions:read`, `channels:read`, `chat:write`, `files:read` and `files:write`. Reinstall the Slack App after changing scopes. It does not request history scanning or arbitrary URL ingestion.

## Durable reception and recovery

One thread identifies one logical session. Only its initiating user may continue it. Each accepted event is committed to SQLite with a unique event ID and a separate reply outbox. The exact acceptance text is `受け付けました。`, without a user mention or broadcast. OCR/inference wait until its delivery has been recorded. Completion mentions the requester in the original thread.

No historical channel scan generates jobs. Each authenticated Socket Mode connection has a fresh online acceptance boundary. Unknown events whose Slack message timestamp predates that boundary are ignored, including shutdown/reconnect backlog. Already persisted jobs are recovered independently. This conservative boundary may also decline an event sent during a short reconnect gap or affected by clock skew; synchronize the Host clock. It cannot perfectly infer every network/shutdown timing case.

Known rate limits honor `Retry-After`. An ambiguous post response or crash during sending becomes `uncertain` instead of blindly posting a duplicate. The authenticated approval page lists ambiguous deliveries and allows an administrator to record the exact remote Slack post timestamp after verifying it. It never automatically resends an uncertain post. If delivery cannot be established, leave it held and investigate before making a new request. `client_msg_id` is stable, but is not treated as an unlimited exactly-once delivery guarantee.

## Local OCR and conversation

Authenticated file IDs from the allowed channel are resolved through `files.info`. Only bounded image/PDF files on Slack's authenticated private download origin are allowed; redirects, arbitrary URLs, external files and unsupported formats are rejected. Originals and OCR JSON are retained in the restricted store. Existing OCR checkpoint/recovery and qualified profiles are reused; handwriting/vertical text/arbitrary forms retain their existing unqualified limits.

OCR plaintext and model interpretation are separate. File-derived responses, including later conversation in that session, remain local by default; Slack receives a completion notice and processing ID. The attachment staging guard allows up to 48,000 UTF-8 bytes of extracted text, but the current paired Server contract accepts at most 16,000 UTF-8 bytes of combined rendered input fields. Inputs exceeding the paired budget fail locally instead of silently truncating them. Large-document hierarchical summarization is not qualified by this implementation.

## Priority, context and retention

Foreground Continue/Agent/Fresh Critic and important TaskMemory/Laya work take precedence. Slack OCR reuses the existing preemptible scheduler. Slack Qwen concurrency is one; background work checks Host activity and resource admission. The Server enforces priority at actual inference admission; a Client-only busy check is insufficient. The approved public normal/background/foreground probe confirmed interruption of owned background work and foreground completion. This is limited public-probe evidence, not a general throughput guarantee.

Saluki uses the existing verified 128K/CPU6/parallel1/GPU0/KV-q8 engine and exclusive backend lease. Background summarization yields on important activity, releases its owned backend and preserves original history. A conservative UTF-8 upper bound triggers compression near the configurable 8K input budget; the Server enforces the paired rendered-input limit. Real restricted-history Saluki compression quality, latency and resource use are not yet qualified. Summary provenance binds to immutable completed history; summaries never replace originals.

Daily cleanup removes inactive sessions after 30 days. Storage-attributable pressure can remove oldest sessions older than seven days, stopping when pressure recovers. Active jobs/undelivered replies remain protected. CPU load alone never deletes sessions. Client cleanup also removes expired local proposal/review and inference-input payloads, retaining hash-only audit. Unresolved application receipts protect their session until reconciliation. Formally accepted restricted knowledge stays on ServerPC; the idempotent Server retirement endpoint removes unadopted run/proposal payloads while preserving formally accepted knowledge, Rule state and adoption audit. Public fixtures verify this contract; real paired retirement remains a deployment gate.

## Human approval over LAN HTTPS

ClientPC Windows Host is the authority. Use a dedicated LAN HTTPS URL ending in `/slack/approvals` directly; do not depend on an HTTP redirect/menu for trust. The existing ServerPC chat can link to the registered URL but is not an approval authority.

Use a trusted private-CA server certificate with the actual LAN DNS/IP SAN. Devices need to trust the server CA; they no longer need administrator client certificates. Never bypass certificate warnings. Each device must be on the configured private subnet; actual reachability and mobile usability need physical verification. No Internet/VPN/port-forwarding endpoint is installed. Use `tools/slack/Set-ApprovalPassword.ps1` locally to prepare a credential file outside Git and the matching web configuration. The script requires the known old configuration hash and preserves existing files.

The approval listener requires HTTPS and administrator password login. The site-selected policy permits 4–256 characters, stores only a random-salted PBKDF2-SHA256 hash (600,000 iterations), and has no failed-attempt lockout. The Secure/HttpOnly/SameSite=Strict login cookie expires after one hour; logout, password rotation or receiver restart also ends the session. Same-origin CSRF, proposal content hash, expiration and replay checks remain mandatory. Untrusted proposal text is rendered as text. Server logs omit passwords, request bodies, certificates and tokens. Host approval is distinct from formal application; an `approved` item is not an `applied` item. Approval atomically reserves an exact canonical request, bound to the existing internal Host mTLS certificate, administrator, proposal/review hashes, target/base/new hashes, nonce and five-minute expiration. A separate worker dispatches it once. Ambiguous responses are reconciled through read-only Server status; unknown/interrupted results remain HOLD and are never replayed. Accepted items remain in restricted storage and are not ordinary SecondBrain knowledge.

The optional user-session notification watcher displays only a generic pending notice. Run it in the logged-in Windows session, not session 0. The approval list remains authoritative when a notification is missed.

## Rollback

Do not change model/auth settings. Disable the Slack feature and stop only its owned process using the normal service/task lifecycle. Preserve its SQLite/outbox and unknown application receipts. Return the optional Client/Server entrypoints to their prior binaries from the exact deployment receipt. Do not replay unknown sends/applies or restore the ordinary canonical database. PRs are not auto-merged.

## Official references

- [Slack Socket Mode](https://docs.slack.dev/apis/events-api/using-socket-mode/)
- [Slack app mentions](https://docs.slack.dev/reference/events/app_mention/)
- [Slack message posting and rate limits](https://docs.slack.dev/reference/methods/chat.postMessage/)
- [Slack private file authentication](https://docs.slack.dev/reference/objects/file-object/)
- [ASP.NET certificate authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/certauth?view=aspnetcore-10.0)

## Candidate launch after qualification

`localbrain slack verify --config <private-slack-config.json>` checks the current
Bot identity and allowed channel without enabling intake, opening Socket Mode,
posting messages, starting OCR or requesting inference. It also works while
`enabled` is false and prints only a fixed status and zero activity counters.
Channel and file metadata use Slack's documented GET transport; credentials
remain in the Authorization header. Missing required channel safety flags fail
closed.

The Client executable now requires both the .NET 10 runtime and ASP.NET Core 10 shared runtime (or a matching self-contained publish); verify them before deployment. Both are present on the development PC. The public templates contain no credentials. Keep both `enabled` and the Server's `human_apply_enabled` false until the deployment gates are satisfied. In a private, non-Git configuration directory, use the existing Host executable with:

```powershell
localbrain slack serve --config <private-slack-config.json> --web-config <private-approval-config.json>
```

This single owner runs intake, reply outbox, processing, background maintenance, approval application and HTTPS. It does not replace or restart the existing Bridge/OCR service. The `host.lock` prevents a second Slack owner. The existing Bridge OCR scheduler must be running with its qualified configuration. The optional notification watcher runs separately in the interactive user's session. No Windows service, firewall opening, certificate enrollment or scheduled task is installed by this PR.

Use the actual configured Host owner and Host mTLS certificate fingerprint in the Server's private settings; public fixture identities are not production identities. The Server connection is restricted to private IPv4, existing HTTPS certificate validation, no proxy and no redirects. Ordinary Client requests do not instantiate this optional transport. Client HTTPS supports `/slack/approvals`; the original `/` also serves the same login/approval page.

### Text file results

`returnFileResults` is opt-in and defaults to false in public templates. With it enabled, a completed OCR job attaches the exact UTF-8 transcription as `ocr-<job-id>.txt` to its original configured channel/thread. Explicit transcription requests bypass Qwen interpretation. Analysis requests additionally receive `analysis-<job-id>.txt`. These files are returned only to the originating Slack thread; no Cloud AI or Cloud OCR is used. Slack itself receives the requested result attachment.

The durable file outbox freezes content and destination before obtaining a Slack upload ticket, uploading bytes, and completing the share. Known rate limits defer; ambiguous network/completion outcomes remain held without automatic resend. Pending/uncertain files protect their Session from retention cleanup. Private upload capabilities and text are never logged or published.

For an explicitly authorized, already completed job, `localbrain slack return-ocr-text --config <private-config> --job <job-id>` submits a local request to the standing single Host owner. It reuses saved OCR and does not rerun OCR or inference. Repeating the operator command observes the same request; a terminal sent file is not sent again. Local OCR results remain under the private OCR jobs directory and in the private Slack store.

## Review and release status

See `SLACK_VALIDATION.md` for exact public test coverage and unqualified gates. Actual public Slack mention reception, acknowledgement and completion reply now pass on the approved installation. Client checks and normal regression, Server full public regression, limited independent SSE review, authenticated capabilities, public priority/completion probes and the local password-authenticated HTTPS checks are recorded in `SLACK_VALIDATION.md`. Real Slack file/OCR events, reconnect and reboot recovery, restricted Saluki compression, live knowledge/Rule application and retention, physical mobile usability and broader resource performance remain unqualified. The implemented scope is ready for review against main. Local activation remains a separate operator decision; merging does not change another installation. A failed local process never falls back to Cloud AI.

## Current operator qualification

The App is installed and invited, concealed credentials were entered locally, and the approved single receiver is online. A new human-posted public mention produced one durable job, one acknowledgement and one completion reply with the expected marker. There was no uncertain delivery or duplicate job in that check. The earlier failed public event was retained and was not replayed.

The Server stream parser now accepts valid role/finish/usage metadata while preserving malformed-frame, size, completion and cancellation checks. A bounded public restricted request completed successfully after the correction. This fixes the observed text-reply failure; it does not qualify arbitrary attachments or general model quality.

The approved Server installation enables opt-in local model startup for validated Host-authenticated chat. A bounded public stopped-to-ready-to-complete probe passed with one inference. Autonomous review, status and capabilities do not start the model; uncertain requests are not blindly retried. Public templates still default to disabled.

The approved installation also enables human-bound restricted knowledge and enumerated Slack-response Rule application. Candidates can accumulate without adoption. After password login, an administrator reviews the exact content and local review, then explicitly approves it; the authenticated Host binds the application to the proposal/review/target/base/new hashes, nonce and expiry. Actual Host capabilities report human application enabled while automatic adoption, ordinary SecondBrain adoption, external AI and cloud fallback remain false. The chat entry, HTTPS login page and unauthenticated denial were checked from ClientPC. No real private proposal was adopted or production Rule changed as a test; physical other-device approval remains unqualified.

All site-specific task, firewall, certificate, credential and activation changes remain local operator records. They are not copied into Git. Public templates remain disabled and contain placeholders; private configuration, original messages, OCR results and generated answers are not publication artifacts.

An operator may use `slack retry-ocr-ticket --config <private-config> --job <job-id>` after correcting a known API rejection before upload. It only resets a held ticket with no remote file ID or upload URL and unchanged OCR bytes. It cannot reset uncertain uploads or completed/ambiguous shares.
