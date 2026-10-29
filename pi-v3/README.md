# Pi v3 development components

These components are under development. They are not yet connected to the
Windows Host task lifecycle and are not a deployment procedure. Do not use
them against a production workspace or Canonical store.

The package manifest and lock file pin `@earendil-works/pi-coding-agent` to
1.4.0 and require Node.js 22.19.0 or newer. The WSL launcher expects a private
Host-controlled runtime and task directory, a task-specific model socket, a
dedicated bounded workspace filesystem, and cgroup v2 CPU, memory and process
limits. It refuses to start when these boundaries are absent. MCP is currently
disabled in this launcher pending Host-scoped authorization.

The model provider uses only the task socket. The socket bridge forwards only
model requests to the existing Windows Host relay; it is not a new inference
queue. The Host must own the relay capability, credentials, task state,
workspace grant, process cancellation and result authorization. These
connections have not yet been completed for PC73.

`localbrain-loop.mjs` is an isolated Pi event extension that stops a turn after
three consecutive identical bash calls and results. It does not export task
contents or call an external model. It is not loaded by the sandbox launcher
or connected to Host handoff yet, and does not detect other forms of loops.

The Host archive inspector validates data without applying it; validation alone
grants no authority to update a production workspace.
The workspace-seeding helper is likewise experimental: it requires an exact
archive hash and a manifest already inspected and authorized by the Host. On
failure, the Host must discard the entire isolated mount before starting Pi.
The mount must be owned by the helper and not group- or world-writable; for
tmpfs, provision it with `mode=0700` before seeding.
The Host scope-stop helper is also experimental. Its scope name includes both
the task and generation UUID. A disposable PC70 WSL fixture stopped one of two
generation-specific scopes while the other stayed active. Scope names and a
successful stop command do not establish ownership, cgroup emptiness or relay
termination. A Windows Host invocation and separate relay termination remain
unverified; do not use the helper to claim that a workspace is safe to discard.

`tests/outcome-host` exercises the experimental `PiRunOutcome.cs` evidence
reducer. It is not connected to the Host and still needs independent review.
Settlement and model response status alone never authorize a task result or
production update.

PC70 development fixtures have exercised Pi RPC, Codemode and Tool Search
with a test MCP server, isolated WSL-to-Gateway Qwen requests, RPC framing,
and archive guards. These fixtures do not establish PC73 deployment,
live service integration, migration correctness, or full resource isolation on
another machine. Preserve the existing Canonical and approval boundaries until
the replacement is integrated and verified.
