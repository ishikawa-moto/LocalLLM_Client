# Slack validation and remaining release gates

The public implementation is ready for review against main, with disabled templates. A separately approved local installation has now completed the text-only Slack connection and reply qualification below. This does not qualify every Slack/OCR/approval/recovery operation or enable any other installation.

## Confirmed implementation checks

| Area | Result | Scope |
| --- | --- | --- |
| Client build | PASS, 0 errors | Existing platform/nullability warnings retained |
| Client normal regression | PASS, exit 0 | Existing canonical state, Agent, decision, TaskMemory and crash-recovery public fixtures |
| Slack core | 80 checks PASS | Identity, channel/mention/owner, receipt-before-work, dedup, offline boundary, restart, retention, replies, summary wake-up and external-AI denial |
| Protocol / priority / OCR registration | 27 checks PASS | Durable OCR registration, restricted origin, legacy staging recovery, unavailable compression and local admission/preemption |
| HTTP failure / retry regression | 7 checks PASS | Metadata/download Retry-After, cancellable waiting, file rejection isolation and a stalled body after response headers |
| HTTPS password approval | 39 checks PASS | Real loopback HTTPS, no browser client certificate, password matching, minimum4/no attempt lockout, one-hour expiry/logout/rotation, CSRF, exact hashes, pagination and replay |
| File result outbox | 28 checks PASS | Exact UTF8 text, original thread, official form upload ticket, no bearer on upload capability, stage recovery, pre-upload rejection-only retry, uncertain HOLD and retention |
| Adoption / retirement contract | 67 checks PASS | Canonical cross-language vectors, reservations, lost-response/restart reconciliation, older duplicate binding, unknown HOLD, cancellation and cleanup outbox |
| Server complete public regression | 144 tests PASS, 0 failures/errors/skips | Includes SSE parser, restricted resource admission and opt-in authenticated cold-start lifecycle; test-bound loopback listeners, no production model/config |
| Independent SSE review | No findings | Limited read-only review of the fixed parser and one-source deployment boundary; not a general security guarantee |

The five Client public commands total 248 checks; `--slack-process-proof` includes both protocol and HTTP retry groups. Run the Client test executable with `--slack-proof`, `--slack-process-proof`, `--slack-https-proof`, `--slack-file-proof` and `--slack-adoption-proof`; run without arguments for the normal suite. Server tests are in the paired Server repository. Test certificates do not enroll production trust.

### PR review follow-up, 2026-10-21

Metadata and private-file download rate limits now defer the accepted job and respect Slack's Retry-After interval. Owner cancellation interrupts the wait. Startup also reconciles legacy staging names using the retained job/file attachment, exact extension and content hash; unknown or modified originals are preserved. Human confirmation removes delivered queue-refusal rows in the same transaction while retaining uncertain deliveries and ordinary job receipts.

Public regressions additionally cover a summary completing before its waiter transition, atomic wake-up across reopening, duplicate Server proposal binding beyond the first 100 items with terminal state preserved, and the response-body deadline after HTTP headers arrive. These PR follow-up commits have not been deployed to the running installation. The approved installation evidence below describes its separately qualified earlier revision.

The initial restricted environment denied a fixture rename; the normal-user run passed. An earlier crash-test Windows dialog was resolved with test-process-local dialog suppression. These failures remain in the local audit history, rather than being counted as successful runs. The SSE follow-up reused unchanged qualifying tests instead of rerunning unrelated suites.

## Approved installation checks

- The App was installed, invited and configured through concealed local credential entry. Disabled identity/channel preflight passed without reading history, posting messages or opening Socket Mode. Channel/file metadata uses Slack's documented GET routes; required safety fields fail closed.
- The tested Client runtime and compatible existing Host were placed through explicit operator approval. The original coding, model, authentication and OCR configuration were retained. The compiled runtime used the tested DLL. The optional single Slack receiver is online; its independent ownership does not replace the existing Host.
- The earlier administrator-certificate gate is superseded by password authentication. Deployed HTTPS passed normal chain/name validation without a browser client certificate; the login page responds and unauthenticated proposal access is rejected. Password login, one-hour expiry, logout and rotation passed real public HTTPS fixtures. The existing limited LAN firewall rule changes only its executable target; address/subnet/port remain fixed. Physical phone use and an actual administrator login remain separately unqualified.
- The Server was placed and enabled through separately approved, fixed deployment operations. Actual source, preserved model/authentication settings, owned Gateway identity and other-service boundaries were checked. A public priority probe confirmed foreground completion and interruption of owned background work. No model context/KV change or cloud fallback was made.
- The original human-posted public message received acknowledgement but failed because the Server rejected a valid `content: null` role frame. That failed event is retained, without replay. The parser correction also handles finish metadata and empty-choices usage frames while malformed content, missing completion, oversize and cancellation remain rejected.
- After the correction, one bounded public direct completion suite passed: two model submissions, nine HTTP calls, 10.669 seconds, normal and restricted markers matched, restricted state `complete`, owned session retired and final idle confirmed. No inference retry was made. This is a fixed-fixture smoke test, not general model quality or speed qualification.
- A new human-posted public Slack message then completed end to end: one durable job, one acknowledgement, one completion reply, expected public marker matched and zero uncertain replies. One Socket connection remained online. This qualifies that text-only mention/reply path; it does not qualify attachments, reconnect or reboot recovery.
- A separate public one-page local-file OCR check completed with expected text recognized. It was not a Slack upload/file-event check and does not extend existing handwriting, vertical-text or complex-form limits.

- A previously completed Slack OCR result was returned as a UTF8 `.txt` attachment to its original thread. Slack file metadata independently confirmed the file, byte length, bot ownership, original channel and thread. OCR and inference were not rerun. This is one saved-result return, not a broad OCR accuracy qualification.

- On 2026-10-21 the separately approved installation deployed the Chat navigation entry and enabled human-bound application. The actual Chat entry URL and its two static file hashes matched the reviewed public sources. HTTPS login returned 200 and unauthenticated proposal access returned 401. Authenticated Host capabilities reported human restricted adoption and Rule activation true; automatic/ordinary SecondBrain adoption, external AI and cloud fallback stayed false. The bounded rollout restarted only Chat and Gateway once each, with no inference or production application trial.
- The corresponding unchanged-source checks passed: 101 isolated public tests on ClientPC, 42 Server approval tests, 12 Chat tests and two focused approval-boundary scenarios. These are separately scoped runs, not a new combined full regression count. The focused fixtures confirmed candidates alone leave accepted knowledge/Rule unchanged, forged approval is rejected, and an exact Host approval applies each fixture once. Limited independent review of the fixed rollout reported no findings.

No real IDs, local addresses, tokens, certificate contents, Slack originals, OCR text, generated answers, private databases or per-site deployment receipts are part of this public status update. Slack-derived data remains restricted and denied external AI; ordinary non-Slack work retains its existing review routing.

## Performance evidence and limits

The earlier local public ACK control path measured 23.0084 ms and the Laya-lease cancellation path 64.3422 ms. These are synthetic local timings, not actual Slack/network latency.

The existing `ocr capabilities` warm-start median was 103.838 ms for baseline versus 104.049 ms for the candidate (about +0.2%); maximum sampled working sets were 32,374,784 and 32,776,192 bytes. All eight outputs matched. No inference was started by that comparison. It does not establish concurrent coding speed, model throughput, OCR peak memory or near-limit context performance.

## Remaining qualifications and deliberately inactive behavior

- Real Slack image/PDF uploads, authenticated file retrieval and OCR-result handling; shutdown/reconnect, logon/reboot and ambiguous-send recovery on the deployed system.
- Restricted-history Saluki compression quality, latency, preemption and actual RAM/commit use, plus broader foreground/OCR/model resource contention.
- Actual adoption of a real private proposal, production Rule changes, retention and paired retirement/purge remain unqualified. Human-bound application is enabled on the approved installation and its public contract is tested. Accepted restricted knowledge remains separate from ordinary SecondBrain and TaskMemory adoption.
- Other PC/iPhone/Android private-CA trust, password login, LAN reachability, responsive rendering and actual approval clicks. Never bypass certificate warnings.
- Arbitrary handwriting, vertical text, complex forms and general model quality remain unqualified. Large-document hierarchical analysis and general restricted-corpus retrieval are not implemented.

Slack model cold-start is an explicit Server opt-in, enabled only for validated Host-authenticated chat. Status/capabilities and autonomous Reviewer calls do not initiate startup. One failed background start does not force-stop the model or automatically retry. A bounded live public probe passed STOPPED → STARTING → READY → complete with one inference, a matching public marker, bound terminal status, retired test Session and final idle. The initial HTTP request was explicitly deferred before inference; the same immutable request proceeded only after readiness. No model was force-stopped. The paired rendered-input budget is conservatively 16,000 UTF-8 bytes; oversize fails without silent truncation or cloud fallback. An attachment staging bound is not permission to exceed that paired analysis limit. Explicit transcription bypasses model analysis and returns a text file of at most1MiB.

## Deployment and publication boundary

New installations must use the disabled public templates and complete their own exact approval, rollback, trust and live qualification steps. PR merge does not install tasks, enroll certificates, open firewall rules or activate intake/application. Site-specific changes and receipts remain in protected local storage.

Review `SLACK_CONNECT.md` and the paired Server `SLACK-SERVER.md` for the contracts and setup. Do not restore the ordinary canonical DB, replay unknown sends/applies or force-stop unrelated models/services. The implemented scope is ready for review; the remaining qualifications above stay explicit limits rather than claims of completed coverage. Review and merge do not automatically deploy or enable another installation.


## 2026-10-21 long-session integration fixes

The normal Client regression completed successfully. All Slack public suites passed 343 checks (80 protocol, 27 processing/priority/OCR, 7 HTTP retry, 95 new conversation/transport, 28 file-return, 67 adoption, 39 HTTPS approval). These use public data and fake model/Slack responses, not live inference or private inputs.

Long conversation compression now uses bounded 65,536-byte batches with restricted, prefix-bound durable checkpoints. A 60-turn fixture above the old 200,000-character cap resumes after store reopen, preserves original history, completes compression and wakes the waiting job. Appended history reuses the verified prefix; stale prefixes are rejected. Retained OCR text and source hashes are part of the source binding. Invalid or oversized summaries fail explicitly rather than leaving the waiting head indefinitely.

The final UTF-8 budget is context + one LF + prompt, at most 16,000 bytes, matching Server. Summaries are bounded to the space remaining after prompt and metadata; whole recent turns are included only if they fit. Omitted raw excerpts remain in the restricted store and are represented by the bound summary, not silently truncated. Unicode byte-boundary and oversized-input fixtures are included.

Only structured deferred/busy responses with boolean retry_safe=true are retried using the identical frozen request. A resource-pressure fixture defers once then completes; ambiguous HTTP 503 and unsafe/missing retry flags remain terminal. This does not authorize replay after an uncertain inference result.

These changes are source/PR fixes only. This validation did not deploy, restart services, run live Slack, launch native models, apply production knowledge or qualify general summary accuracy. Existing installation evidence above describes earlier deployed builds.


## Latest review follow-up (2026-10-21)

The six follow-up review comments were valid. Document analysis now combines prior completed conversation or a source-bound summary with the current OCR and its references under the same 16,000-byte context + LF + prompt budget. A pending summary budget includes current document metadata/text; nothing truncates the new document to make room. A summary waiter triggers compression independently of the short job-metadata threshold.

Replies are escaped before applying their output cap, preserving trailing text and Unicode pairs. Frozen pending reply channels are checked before send admission: a changed channel configuration fails closed without redirecting or incrementing attempts. Delivery remains on hold until the original destination is reconciled.

Only queued jobs with confirmed acceptance delivery block maintenance. Running and waiting-OCR jobs remain blockers. Unacknowledged or uncertain inputs are not extracted as review evidence, while their frozen records remain available for reconciliation. Password setup now requires PowerShell 7.2 or later before password entry.

The added public regressions cover actual document-follow-up processing with prior OCR/constraints, a 14KB transcription with sub-threshold job metadata, document-aware summary budgets, escaped suffix preservation, destination changes, uncertain acceptance scheduling and password setup version admission. No real model, Slack, OCR, password entry or production service is used by these regressions. This follow-up is a source/PR change, not a deployment.

Final follow-up validation: normal Client regression exit 0; all Slack suites passed 370 checks (80 protocol, 27 processing, 7 retries, 95 conversation, 27 review regressions, 28 files, 67 adoption, 39 HTTPS). Build: 9 existing warnings, 0 errors. Password script parsed with 0 syntax errors and a 7.2 requirement; no password UI was opened.


## Structured artifact upload contract

The final public Slack image test exposed a mismatch: the store queued JSON/HTML/CSV names, but the upload-ticket validator accepted only OCR TXT/MD and analysis TXT. The legacy validator rejected these additional names before any HTTP call, while delivery conservatively recorded an uncertain ticket. The validator now accepts only each generated prefix paired with its exact extension and a 32-character lowercase hexadecimal job ID. Absolute matching rejects trailing newlines and paths.

`dotnet run --project dotnet/LocalBrain.ClientHost.Tests -c Release -- --slack-file-proof` covers all six supported names and invalid cross-format/path names. A public in-memory pipeline queues a table, creates tickets, uploads frozen bytes, and completes MD/JSON/HTML/CSV in the original thread exactly once; a repeated return and restart do not resend. Existing lost-response and rate-limit tests remain in this proof. The new test fails on the previous ticket validator before its HTTP handler is called. Local argument rejection is held with a fixed diagnostic code; network/response ambiguity remains uncertain and is never automatically retried.

Existing uncertain rows are not reset by deployment. Any one-time repair must prove the exact original binary/source validation failure occurred before dispatch, bind the specific job/input/artifact hashes and original destination, require ticket stage with no remote ID or upload URL, and preserve all other jobs and sent files. Unknown or genuinely ambiguous dispatch remains held for reconciliation. Stub tests are separate from real Slack delivery qualification.
