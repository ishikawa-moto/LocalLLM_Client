# Slack candidate validation and release gates

This branch is for review. It is not deployed, enabled, or qualified for real Slack traffic. The user requested implementation and PRs first, with Slack App installation and real connectivity later. No production services, model settings, certificates or firewall rules were changed.

## Public checks

| Area | Result | Actual scope |
| --- | --- | --- |
| Client build | PASS, 0 errors / 9 existing platform-nullability warnings | .NET 10 candidate build |
| Client normal regression | PASS, process exit 0 | Existing canonical state, policy, Agent, decision, TaskMemory and crash recovery tests; synthetic fixtures |
| Slack core | 53 checks PASS | ID mention/owner/channel, receipt before work, dedup, offline boundary, restart, retention, safe replies, rate-limit handling, external-AI denial |
| Protocol / priority / OCR integration | 17 checks PASS | Actual local ACK 23.0084 ms and Laya-lease cancellation 64.3422 ms; durable OCR registration and restricted origin, without model inference |
| HTTPS approval | 18 checks PASS | Actual loopback TLS with public temporary certificates, administrator pinning, untrusted/absent certificates, CSRF, exact hash/replay, safe JSON rendering |
| Adoption / retirement contract | 63 checks PASS | Exact Server Python canonical proposal/review/apply vectors, administrator-bound reservation, expired/replayed approval denial, lost response/restart status reconciliation, unknown HOLD, immutable inference requests, targeted cancellation and durable cleanup outbox |
| Server public regression | Prior 109 PASS + additional 16 retirement checks PASS | Public fixtures; original 109 were not rerun after the retirement addition. Server HEAD `9733d1c30a5f4f0bf65afd8e8fd1d662f6746b2b`; no real model, Slack or deployment |
| Whitespace / patch check | PASS | Changed source and public fixtures only |

Run the Client test executable with `--slack-proof`, `--slack-process-proof`, `--slack-https-proof`, and `--slack-adoption-proof`. Run it without arguments for the existing normal suite. Test inputs are public synthetic data; the HTTPS fixture does not enroll certificates in the system trust store. Process-local Windows error-dialog suppression is limited to the test executable and its children. No OS-wide policy was changed.

The initial restricted execution environment denied a fixture file rename; the normal-user rerun passed. A Windows crash dialog delayed an earlier crash test; the process-local test policy corrected this and both focused and complete regression passed. These earlier failures are retained in the private audit record, not treated as successful runs.

## Bounded performance comparison

Baseline: Client default `main` at `c7e8505434a2772a37cb7198024dbdcd276dbf36`.

The existing `ocr capabilities` command was run four times for each fixed binary. Warm median was **103.838 ms baseline / 104.049 ms candidate** (about +0.2%, a startup smoke measurement). Maximum sampled peak working set was **32,374,784 / 32,776,192 bytes**. All eight output SHA256 values were identical. No OCR or language model was started.

This does not qualify coding speed, model throughput, model cold-start/unload, full OCR performance, RAM/commit peaks during inference, Saluki summary quality/latency, or simultaneous build/test workloads. Public lease tests show foreground admission/preemption behavior only. Those real resource measurements remain release gates; the feature stays disabled.

## Local boundary review

The source was manually reviewed for the following paths: authenticated Socket Mode IDs and online epochs; durable receipt/application/retirement outboxes; exact proposal/review hashes; existing Host mTLS identity; no redirects/proxy/public-IP local inference; derived AsyncLocal restriction; Host Egress and immediate Sol/Copilot/Astra/Rescue/tool-entry denial; separate restricted SQLite and OCR provenance; expired-session cleanup; foreground leases and owned-process disposal; bounded input/output and cancellation; HTTPS administrator pinning, CSRF and safe rendering.

This is a self-review, not an independent reviewer sign-off or a general attack-resistance guarantee. Runtime restrictions cannot stop a machine administrator deliberately copying private data outside the system.

## Unqualified / deliberately inactive

- Slack App is not created; real workspace/team/channel/bot identities and local tokens are not installed. The named channel is not connected. Live `app_mention` attachment payloads, upload/private-file behavior, retries, and exact receipt latency remain unverified.
- No live Slack shutdown/reconnect test, Qwen inference, Saluki compression, native OCR rerun, or production knowledge adoption occurred in this task. Existing model/profile qualifications remain unchanged; handwriting, vertical text and complex forms keep their existing limits.
- The paired Server's stopped-model auto-start is deliberately disabled. Real model admission/preemption and throughput must be qualified before activation.
- Physical PC/phone LAN reachability, private-CA trust/name validation, administrator certificate installation/selection, mobile rendering and click behavior remain unverified. No certificate warning bypass is allowed.
- Restricted human acceptance stores a restricted knowledge item / enumerated Slack response Rule. It does not promote data into ordinary SecondBrain or ordinary TaskMemory. General restricted-corpus retrieval is not implemented.
- Inputs must fit the paired Server's conservative 16,000 UTF-8 byte budget. Large-document hierarchical analysis is not implemented; oversized content fails without silent truncation or cloud fallback.
- Independent review, paired end-to-end retention and production activation/rollback verification remain required before enabling the candidates.

## Later operator steps

Create the Slack App from the checked-in manifest, invite it to the allowed public non-shared channel, and enter real IDs/tokens only in the local private settings. Provision trusted LAN HTTPS server and administrator client certificates through the approved device process. Review both Draft PRs and the fixed deployment/rollback plan, then qualify the real traffic/phone/resource cases above before explicitly enabling the feature. Keep both Client `enabled` and Server `enabled` / `human_apply_enabled` false until then.

See `SLACK_CONNECT.md` and the paired Server `SLACK-SERVER.md` for exact launch, configuration, privacy and rollback instructions. PRs are not automatically merged or deployed.
