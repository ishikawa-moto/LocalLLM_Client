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
   filesystem before invoking `run-isolated.py`. The exact runtime path and
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
   interrupted run it must reconcile the WSL scope, Windows relay and any
   snapshot intent before starting a new attempt. An uncertain production
   application must not be replayed automatically.
6. An experimental snapshot can freeze only its exact task scope, retain a
   durable intent and produce a bounded archive. The Host must inspect that
   archive, compare every path and digest against its authorized scope and
   baseline, and bind any required approval to the exact result. The inspector
   performs no extraction or application. Materialization needs its own
   reparse-point, collision, integrity and atomicity checks.
7. Cancellation is complete only when the task scope and separate Windows
   relay have both stopped and the Host has recorded the outcome. A timeout,
   failed stop or lost identity is an unknown outcome requiring reconciliation.

The Pi provider, RPC reader, WSL launcher, snapshot helper, archive inspector
and scope-stop helper have isolated PC70 fixtures. These do not replace a real
Windows Host integration test, PC73 deployment test, Canonical recovery test,
or representative Slack, Browser Chat and OCR checks. The snapshot and
scope-stop components still require their deferred security reviews.
