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

`frozen_snapshot.py` is experimental and requires an explicit experimental
flag. Its recovery record and scope checks have been exercised in disposable
WSL fixtures, but independent security review and Host lifecycle integration
remain outstanding. The Host archive inspector validates data without applying
it; validation alone grants no authority to update a production workspace.

PC70 development fixtures have exercised Pi RPC, Codemode and Tool Search
with a test MCP server, isolated WSL-to-Gateway Qwen requests, RPC framing,
snapshot and archive guards. These fixtures do not establish PC73 deployment,
live service integration, migration correctness, or full resource isolation on
another machine. Preserve the existing Canonical and approval boundaries until
the replacement is integrated and verified.
