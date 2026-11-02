# Host integration: development boundary

The existing authorized Coordinator now has an explicitly enabled, LOW-task
development path through `PiHostRunner` and `PiCanonicalTurn`. It captures an
isolated proposal as `candidate_ready`, with completion eligibility false and
no application to the registered repository. The default remains disabled.
`candidate_ready` currently holds that workspace until an explicit Host
review/apply/discard operation is implemented; it does not automatically start
the next task. Do not clear this hold by editing task state or treating the
candidate as a passing result.
Builds and WSL component checks do not establish Windows Host execution under
Code Integrity or eligibility for PC73 deployment. The requirements below also
describe the production boundary that remains unfinished.

1. The Windows Host resolves the requested repository through its registered
   allowlist, records the immutable request and baseline revision in the
   existing Canonical store, and obtains the required workspace grant. Pi must
   not be given the production repository, Canonical database, credentials or
   Host approval mechanism as writable mounts.
2. The Host creates a private task identity and a bounded isolated workspace.
   The WSL launch must have an appropriately limited user cgroup and dedicated
   filesystem and private per-task session directory before invoking
   `run-isolated.py`. The exact runtime path and
   resource budgets are Host configuration, not model input. The current
   launcher intentionally disables MCP until the Host supplies an authorized
   configuration and capability boundary.
3. The Host starts and owns the model socket bridge and its Windows relay.
   The sandbox gets only the model socket, while the Host retains mTLS and
   any one-use capability. Do not bypass existing preflight, context or
   approval controls merely by omitting a permit. The existing v2 managed
   relay permit is tied to its own preflight and cannot be assumed to cover v3.
4. The Host sends Pi RPC commands with matching IDs. A successful `prompt`
   response means the prompt started; `agent_end` is not completion. The Host
   waits for `agent_settled`, records observations as untrusted evidence, and
   checks model/tool errors and the task's own acceptance conditions.
5. The Host checkpoints running, cancellation, capture, validation and
   application decisions in the existing Canonical task state. After an
   interrupted run it must reconcile the WSL scope and Windows relay before
   starting a new attempt. An uncertain production
   application must not be replayed automatically.
6. Use a dedicated Git worktree for each task and inspect its diff against the
   authorized baseline. Bind any required approval to the exact proposed
   result. Git commits preserve accepted results; a failed task's worktree may
   be discarded only after its owned processes have stopped. This Git-based
   Host integration and the production application boundary are not yet
   implemented.
7. Cancellation is complete only when the task scope and separate Windows
   relay have both stopped and the Host has recorded the outcome. A timeout,
   failed stop or lost identity is an unknown outcome requiring reconciliation.

The Pi provider, RPC reader, WSL launcher, archive inspector
and scope-stop helper have isolated PC70 fixtures. These do not replace a real
Windows Host integration test, PC73 deployment test, Canonical recovery test,
or representative Slack, Browser Chat and OCR checks. Scope-stop still needs Windows Host and relay
integration testing.

`PiCanonicalTurn` is used by that development path. It records an
attempt reservation, prepared prompt and bounded untrusted observations in the
existing Canonical store; it does not authenticate a caller, authorize a task,
launch or stop Pi, update task state or publish results. Its caller must bind a
fresh isolated process and exclusive streams to the authorized task. A previous
revision passed a disposable Windows Canonical fixture. On the final revision,
Windows Code Integrity blocked the generated test executable; a successful build
or a static review does not establish that the final executable can run under
the applicable policy. The exact policy-approved Windows fixture remains to be
run. Do not use this adapter to bypass Host approval or isolation requirements.

The `RunAuthorizedAsync` entry checks the ambient Host journal, the
existing task command grant and the current Git HEAD before sending a prompt.
Its Coordinator connection has been built but not executed on Windows.
These checks do not establish process ownership, eliminate a subsequent Git
race, authorize publication, or replace Host lifecycle and isolation controls.

The model bridge's `--host-stdio` mode keeps model forwarding in the Windows
Host, without spawning a Windows relay executable from WSL. The Host measures
the exact payload with its configured tokenizer, applies the existing preflight
and one-use permit, and forwards only to the local Bridge. Redirects and proxies
are disabled. The socket also exposes only `memory_search` and `recall` through
the existing task gate. Search returns at most eight original excerpts with
event identity, source, timestamp, verification status and evidence hashes;
the first Saluki selection of eight events is not a lifetime reference ceiling.
Per-task search and Recall budgets are recorded in Canonical events. No new
history database, embedding model or mutable summary memory was added. These
Windows storage/authorization checks remain runtime-unverified.

The trusted Host configuration is `v3Development` with `enabled` (default
false), `distribution`, `runtimeRoot`, `tokenizerPython`, `tokenizerPath` and
`tokenizerScriptPath`. Runtime paths are not accepted from task requests.
Only exact authorized file paths are transported; wildcard scopes are refused.
Pi sessions live in a private, bounded 16MiB tmpfs for this one proposal.
They are not preserved across driver exit or restart. Canonical evidence and
approval history remain durable through their existing Host mechanisms.
An interrupted or unknown generation is not automatically replayed and blocks
replacement work on the same workspace until its processes are reconciled.

PC70 WSL checks on 2026-11-02 passed the actual Pi 1.4.0 `get_state`, scope
termination and candidate transport (including one long Japanese filename),
Host input closure and driver SIGKILL with an empty scope and no candidate,
seven synthetic stdio bridge cases, a memory-extension frame fixture, and the
bounded-mount guards. These checks did not run a Windows Canonical store,
exercise the native human grant, or send a model request through the new
Windows broker. Windows Host and test projects built; their execution tests
remain unperformed under Code Integrity.

A PC70 disposable WSL fixture passed a narrow handoff test: the C# archive
inspector generated a manifest for a Japanese-named file, the seeding helper
checked the archive digest and manifest and populated an empty bounded mount,
and an isolated Pi RPC bash command read the file. In a second run of the same
fixture, Pi also reached Qwen through the existing PC70 Gateway via a temporary
relay and completed a prompt. The fixture did not invoke the Windows Host and
does not establish Host authorization, production application, or PC73
behavior.
