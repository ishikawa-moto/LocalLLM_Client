# Host integration: remaining implementation contract

This document records boundaries for the unfinished v3 Host integration. It
does not authorize deployment or claim that the steps below are implemented.

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

`PiCanonicalTurn` is an unintegrated Host-side building block. It records an
attempt reservation, prepared prompt and bounded untrusted observations in the
existing Canonical store; it does not authenticate a caller, authorize a task,
launch or stop Pi, update task state or publish results. Its caller must bind a
fresh isolated process and exclusive streams to the authorized task. A previous
revision passed a disposable Windows Canonical fixture. On the final revision,
Windows Code Integrity blocked the generated test executable; a successful build
or a static review does not establish that the final executable can run under
the applicable policy. The exact policy-approved Windows fixture remains to be
run. Do not use this adapter to bypass Host approval or isolation requirements.

The candidate `RunAuthorizedAsync` entry checks the ambient Host journal, the
existing task command grant and the current Git HEAD before sending a prompt.
It is not wired to the Host coordinator and has only been built, not executed.
These checks do not establish process ownership, eliminate a subsequent Git
race, authorize publication, or replace Host lifecycle and isolation controls.

A PC70 disposable WSL fixture passed a narrow handoff test: the C# archive
inspector generated a manifest for a Japanese-named file, the seeding helper
checked the archive digest and manifest and populated an empty bounded mount,
and an isolated Pi RPC bash command read the file. In a second run of the same
fixture, Pi also reached Qwen through the existing PC70 Gateway via a temporary
relay and completed a prompt. The fixture did not invoke the Windows Host and
does not establish Host authorization, production application, or PC73
behavior.
