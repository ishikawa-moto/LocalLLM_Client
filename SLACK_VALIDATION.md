# Slack validation and remaining release gates

The public implementation remains an unmerged Draft PR with disabled templates. A separately approved local installation has now completed the text-only Slack connection and reply qualification below. This does not qualify every Slack/OCR/approval/recovery operation or enable any other installation.

## Confirmed implementation checks

| Area | Result | Scope |
| --- | --- | --- |
| Client build | PASS, 0 errors | Existing platform/nullability warnings retained |
| Client normal regression | PASS, exit 0 | Existing canonical state, Agent, decision, TaskMemory and crash-recovery public fixtures |
| Slack core | 60 checks PASS | Identity, channel/mention/owner, receipt-before-work, dedup, offline boundary, restart, retention, replies, rate limits and external-AI denial |
| Protocol / priority / OCR registration | 17 checks PASS | Durable OCR registration and restricted origin; local control-path admission/preemption |
| HTTPS password approval | 37 checks PASS | Real loopback HTTPS, no browser client certificate, password matching, minimum4/no attempt lockout, one-hour expiry/logout/rotation, CSRF, exact hashes and replay |
| File result outbox | 28 checks PASS | Exact UTF8 text, original thread, official form upload ticket, no bearer on upload capability, stage recovery, pre-upload rejection-only retry, uncertain HOLD and retention |
| Adoption / retirement contract | 63 checks PASS | Canonical cross-language vectors, reservations, lost-response/restart reconciliation, unknown HOLD, cancellation and cleanup outbox |
| Server complete public regression | 142 tests PASS, 0 failures/errors/skips | Includes SSE parser and opt-in authenticated cold-start lifecycle; test-bound loopback listeners, no production model/config |
| Independent SSE review | No findings | Limited read-only review of the fixed parser and one-source deployment boundary; not a general security guarantee |

The five Client public groups total 205 checks. Run the Client test executable with `--slack-proof`, `--slack-process-proof`, `--slack-https-proof`, `--slack-file-proof` and `--slack-adoption-proof`; run without arguments for the normal suite. Server tests are in the paired Server repository. Test certificates do not enroll production trust.

The initial restricted environment denied a fixture rename; the normal-user run passed. An earlier crash-test Windows dialog was resolved with test-process-local dialog suppression. These failures remain in the local audit history, rather than being counted as successful runs. The SSE follow-up reused unchanged qualifying tests instead of rerunning unrelated suites.

## Approved installation checks

- The App was installed, invited and configured through concealed local credential entry. Disabled identity/channel preflight passed without reading history, posting messages or opening Socket Mode. Channel/file metadata uses Slack's documented GET routes; required safety fields fail closed.
- The tested Client runtime and compatible existing Host were placed through explicit operator approval. The original coding, model, authentication and OCR configuration were retained. The compiled runtime used the tested DLL. The optional single Slack receiver is online; its independent ownership does not replace the existing Host.
- The earlier administrator-certificate gate is superseded by password authentication. Deployed HTTPS passed normal chain/name validation without a browser client certificate; the login page responds and unauthenticated proposal access is rejected. Password login, one-hour expiry, logout and rotation passed real public HTTPS fixtures. The existing limited LAN firewall rule changes only its executable target; address/subnet/port remain fixed. Physical phone use and an actual administrator login remain separately unqualified.
- The Server was placed and enabled through separately approved, fixed deployment operations. Actual source, unchanged configuration, owned Gateway identity and other-service boundaries were checked. A public priority probe confirmed foreground completion and interruption of owned background work. No model context/KV change or cloud fallback was made.
- The original human-posted public message received acknowledgement but failed because the Server rejected a valid `content: null` role frame. That failed event is retained, without replay. The parser correction also handles finish metadata and empty-choices usage frames while malformed content, missing completion, oversize and cancellation remain rejected.
- After the correction, one bounded public direct completion suite passed: two model submissions, nine HTTP calls, 10.669 seconds, normal and restricted markers matched, restricted state `complete`, owned session retired and final idle confirmed. No inference retry was made. This is a fixed-fixture smoke test, not general model quality or speed qualification.
- A new human-posted public Slack message then completed end to end: one durable job, one acknowledgement, one completion reply, expected public marker matched and zero uncertain replies. One Socket connection remained online. This qualifies that text-only mention/reply path; it does not qualify attachments, reconnect or reboot recovery.
- A separate public one-page local-file OCR check completed with expected text recognized. It was not a Slack upload/file-event check and does not extend existing handwriting, vertical-text or complex-form limits.

- A previously completed Slack OCR result was returned as a UTF8 `.txt` attachment to its original thread. Slack file metadata independently confirmed the file, byte length, bot ownership, original channel and thread. OCR and inference were not rerun. This is one saved-result return, not a broad OCR accuracy qualification.

No real IDs, local addresses, tokens, certificate contents, Slack originals, OCR text, generated answers, private databases or per-site deployment receipts are part of this public status update. Slack-derived data remains restricted and denied external AI; ordinary non-Slack work retains its existing review routing.

## Performance evidence and limits

The earlier local public ACK control path measured 23.0084 ms and the Laya-lease cancellation path 64.3422 ms. These are synthetic local timings, not actual Slack/network latency.

The existing `ocr capabilities` warm-start median was 103.838 ms for baseline versus 104.049 ms for the candidate (about +0.2%); maximum sampled working sets were 32,374,784 and 32,776,192 bytes. All eight outputs matched. No inference was started by that comparison. It does not establish concurrent coding speed, model throughput, OCR peak memory or near-limit context performance.

## Remaining qualifications and deliberately inactive behavior

- Real Slack image/PDF uploads, authenticated file retrieval and OCR-result handling; shutdown/reconnect, logon/reboot and ambiguous-send recovery on the deployed system.
- Restricted-history Saluki compression quality, latency, preemption and actual RAM/commit use, plus broader foreground/OCR/model resource contention.
- Live human-bound restricted knowledge and enumerated Rule application, retention and paired retirement/purge. Human application remains disabled. Accepted restricted knowledge is separate from ordinary SecondBrain and TaskMemory adoption.
- Other PC/iPhone/Android private-CA trust, password login, LAN reachability, responsive rendering and actual approval clicks. Never bypass certificate warnings.
- Arbitrary handwriting, vertical text, complex forms and general model quality remain unqualified. Large-document hierarchical analysis and general restricted-corpus retrieval are not implemented.

Slack model cold-start is an explicit Server opt-in, enabled only for validated Host-authenticated chat. Status/capabilities and autonomous Reviewer calls do not initiate startup. One failed background start does not force-stop the model or automatically retry. A bounded live public probe passed STOPPED → STARTING → READY → complete with one inference, a matching public marker, bound terminal status, retired test Session and final idle. The initial HTTP request was explicitly deferred before inference; the same immutable request proceeded only after readiness. No model was force-stopped. The paired rendered-input budget is conservatively 16,000 UTF-8 bytes; oversize fails without silent truncation or cloud fallback. An attachment staging bound is not permission to exceed that paired analysis limit. Explicit transcription bypasses model analysis and returns a text file of at most1MiB.

## Deployment and publication boundary

New installations must use the disabled public templates and complete their own exact approval, rollback, trust and live qualification steps. PR merge does not install tasks, enroll certificates, open firewall rules or activate intake/application. Site-specific changes and receipts remain in protected local storage.

Review `SLACK_CONNECT.md` and the paired Server `SLACK-SERVER.md` for the contracts and setup. Do not restore the ordinary canonical DB, replay unknown sends/applies or force-stop unrelated models/services. Keep both PRs as drafts until the intended remaining release scope is qualified; there is no automatic merge or deployment.
