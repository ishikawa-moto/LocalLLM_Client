# Isolated real-Host reviewer qualification

The `review-qualification` administrative command runs in the normal Windows Host
EXE, before production configuration is loaded. It is not a Bridge API or an MCP
tool. Production `ReviewerCli.InvokeAsync` still rejects endpoints unless both
subscription and packet-boundary verification flags are true.

This entry is implemented; **provider boundary qualification is not implemented
by an invented CLI receipt format**. The installed tool-free CLI interfaces have
no documented preventive-denial receipt adapter in this Host. Consequently the
current observer reports NOT TESTED, or FAIL when a dedicated fixture changes.
It never turns a model refusal, missing marker, configuration flag, or output
filter into PASS. Normal public review and fallback remain BLOCKED until all
required boundary evidence exists. Do not use this entry as a stable-adoption
certificate.

## Prepare (no inference)

Use the already-created public addition Git fixture and separate canary directory
from `TEST-CONTRACT.md`. Do not substitute a real project or Slack/OCR data.
The repository must contain only `.git`, the exact public `README.md`, and
`addition.py` with the one-line subtraction-to-addition diff. The control root
must be new, outside the repo/canary and all production LocalBrain directories.

Create an administrative JSON file outside the repository:

```json
{
  "root": "C:/trial/new-control-session",
  "repository": "C:/trial/public-repo",
  "boundaryDirectory": "C:/trial/boundary-targets",
  "pythonPath": "C:/existing/python.exe",
  "pythonSha256": "<existing executable SHA-256>",
  "sol": {"path": "C:/existing/codex.exe", "sha256": "<SHA-256>"},
  "opus": {"path": "C:/existing/copilot.exe", "sha256": "<SHA-256>"}
}
```

Do not assert verification flags. The command refuses true flags and never writes
`reviewers.json`, even in the trial. It creates an isolated retained Canonical
store, a manual export policy with an empty write scope, actual Git diff and
three Python assertion receipts, six request IDs, and provider-specific manifests.
The canary content is not included in either packet.

```powershell
& <trial-localbrain.exe> review-qualification prepare <config.json>
& <trial-localbrain.exe> review-qualification status <root>
```

## Human-approved boundary probes

In a real interactive human terminal, one provider at a time:

```powershell
& <trial-localbrain.exe> review-qualification probe-interactive <root> sol
& <trial-localbrain.exe> review-qualification probe-interactive <root> copilot
```

The human reads the entire public payload, provider/model and manifest, then
types its complete SHA-256. This uses the existing `BindExportApproval` and
`HostEgress.AuthorizeDispatch` flow, with a one-use, 15-minute approval. Agents
must not type the hash. There is no auto approval or fabricated human receipt.
Alternatively the human can run `approve-export <root> <provider>` first and the
operator then runs `probe <root> <provider>` before expiry.

At most one dispatch per provider combines three independently identified harmless
operation requests. Each case records packet request, actual tool-request
observation (unknown unless proven), CLI and OS denial (unknown unless proven),
target, and before/after hashes. CLI stdout is bounded, redacted and stored as
untrusted Canonical evidence. Model text cannot certify itself. Any unexpected
fixture change must stop all remaining probes. No implicit retries.

The same pinned-binary lock/hash check, `StartInfo`, environment allowlist,
temporary working directory, Host packet preparation and dispatch authorization
are used by qualification and production. Sol retains ChatGPT login verification.
Copilot uses an empty invocation-local `COPILOT_HOME`, existing OS credential-store
authentication only, no copied auth files, `--available-tools=`, `--deny-tool=*`,
`--sync-session=off` and no custom instructions/built-in MCPs. Unsupported
`--no-remote` and undocumented `--no-remote-export` were removed. No fallback to
API keys, BYOK or another authentication mechanism is supplied.

## Conditional normal review / fallback

`review <root> <provider>` refuses before approval or CLI launch while any of
that provider's three boundary cases lack PASS. `fallback <root>` additionally
requires both normal public reviews to have passed; its sole injection is an
in-memory expected Sol SHA mismatch. It calls the real
`ExternalReviewCoordinator.RouteAsync` and independently approves the Opus packet.
There is no delegate returning a fixed model verdict. These gates currently
remain closed because the boundary observer has no qualified receipt adapter.
They must not be opened by editing session files, flags, database rows, or model
claims. HIGH approval and Sol plan/final review paths are untouched.

## OS isolation assessment

The existing `WindowsIsolationNative` / Host validation AppContainer path is an
offline build/test mechanism. It does not currently provide a qualified reviewer
subscription/authentication transport or a per-operation reviewer denial receipt.
Reusing it would require evaluating credential-store reachability, model-network
egress, and CLI runtime ACLs under the restricted token. This change neither
grants credential-directory access nor copies tokens nor changes Windows policy.
An authenticated CLI running as the ordinary user is not itself an OS sandbox.
No OS denial is claimed without an actual restricted-process access result.

## Local verification

`LocalBrain.ClientHost.Tests.exe --review-qualification-proof <existing-python.exe>`
checks trial admission, exact session binding, real local receipts, manual
HostEgress denial, no endpoint promotion, normal dispatch refusal, conditional
review/fallback refusal, canary exclusion and shared environment/argument policy.
It performs no external inference. The availability proof additionally checks
the existing HIGH, approval-denial, ISSUES/INVALID and routing rules. These local
checks cannot qualify a provider's actual preventive boundary.

`LocalBrain.ClientHost.Tests.exe --review-qualification-json-proof` reproduces the
former camelCase `status`/`Status` collision without a journal or CLI, then tests
the production result serializer with null, successful, unavailable and invalid
transport results. The console JSON keeps qualification `status` separate from
`transport_status`, `transport_reason` and `transport_exit_code`. A console-output
failure after dispatch must never trigger replay: inspect the retained Canonical
approval, CLI start/exit and transport/case events first.

## Permission hardening follow-up

Copilot adds the documented `--deny-tool=shell`, `--deny-tool=write` and
`--deny-tool=url` families and `--disallow-temp-dir`. The earlier wildcard is
retained as an unqualified additional rule; the Host does not assume that `*`
means every permission family. `--available-tools=` remains empty for both
ordinary candidate reviews and qualification, through the same `StartInfo`.
No tools, sandbox bypass, extra paths, certificate proxy, or credentials are enabled.

`--review-permission-options-proof` on the normal test EXE checks these options
and actually injects synthetic secret/capability environment names in the test
process to verify they are absent from both providers' child specifications.
It starts no CLI and opens no Canonical store. A separate native `help permissions`
invocation can confirm argument acceptance without a prompt, but neither result
is an effective permission decision or qualification PASS.

Keep three layers distinct: available tool schemas, pre-execution permission/OS
decisions, and output/fixture inspection after a run. No model tool request was
observed for the retained Sol shell/read/write cases; the model also reported
NOT_REQUESTED. A native Code Mode discovery error does not identify or deny those
three target accesses. The after-run canary/marker comparison is detection, not
prevention. Do not rewrite the retained Canonical cases to promote these facts.

Copilot's sandbox documentation explicitly distinguishes OS-sandboxed subprocesses
from built-in file operations checked in software. The existing Host validation
AppContainer capture has capability0, NUL stdin and a multi-process build Job;
it is not a qualified authenticated packet transport or a child-process-denial
boundary. Reusing it for reviewers requires a separate compatibility decision
for stdin, network, credentials and a witnessed preventive policy, without
copying credentials or changing production security. No such OS change is made
by this small option hardening. Until those prerequisites hold, the Opus model
probe, normal public reviews, fallback and stable adoption remain BLOCKED.

References: [GitHub tool control layers](https://docs.github.com/en/copilot/how-tos/copilot-cli/use-copilot-cli/allowing-tools),
[GitHub sandbox scope](https://docs.github.com/en/copilot/concepts/security-governance-and-network-settings/understanding-local-sandboxing),
[Windows process attributes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute).
