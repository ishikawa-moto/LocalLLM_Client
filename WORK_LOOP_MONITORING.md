> Agent v2 command-gate update: implementation and terminal work now goes through the managed Agent v2 task owner. Its native task grant, owned process deadline/receipt, canonical progress and TaskMemory take precedence over the historical direct-terminal route described below. Continue built-in mutations/terminal must be Excluded in the supported UI. Ordinary read-only chat retains the Bridge guard. See [AGENT_COMMAND_EXECUTION.md](AGENT_COMMAND_EXECUTION.md).

# Work loop monitoring

## Coverage

| Route | Monitor and intervention |
| --- | --- |
| Ordinary Continue chat/Agent using the local Bridge | Every chat history is inspected before context-memory selection. CPU Laya receives compact numeric tool milestones (first result and each fourth result, or errors). A confident replan/escalation pauses the next request; it never executes a command or calls a cloud reviewer. |
| Other clients using this Bridge | The same HTTP limits and chat-history checks apply, independent of client/model names. |
| Agent v2 / Pi | Existing live tool milestones, deterministic ProgressTracker, Laya decisions, documented Pi abort, 10-minute Actor run and task-turn limits remain. Its model requests additionally cross the Bridge guard. |
| TaskMemory selection inside a Bridge chat | The existing native/measurement budgets and the Bridge's total request budget apply. Selection is not a separate permission to execute tools. |
| Continue's direct terminal tools | Completed tool results are inspected when the next model request returns to the Bridge. A running external command is owned by Continue, not by this supervisor. The inspected Continue 2.0.0 local foreground implementation has its own two-minute timeout; background commands do not use that timeout. The supplied always-apply rule requires bounded foreground execution or an explicitly owned background job. |
| SecondBrain background Reviewer, external cloud tools, Codex, arbitrary OS programs, routes bypassing this Bridge | Their own process/lease/timeout policies apply. This change does not give Laya access to their running jobs or control over them. It is not an OS-wide watchdog. |

## Rules

- Stop before forwarding after four repetitions of an identical tool-argument/result cycle of length one, two or three, or three consecutive identical tool errors. JSON object property order does not evade matching. Tool call IDs are not part of the fingerprint.
- Different results count as observable progress; repeated commands with changing results are not blocked by the identical-cycle rule. A new explicit `user` message starts a new work segment. The check uses the original history, not the shortened TaskMemory prompt.
- Laya receives counts and allowlisted labels only. Low confidence, invalid confidence, unavailable Laya or its 45-second budget expiry falls back to deterministic checks. A Laya decision never grants approvals, attests tests, claims success, submits Sol work, or executes an inferred action.
- Every forwarded HTTP request has a 30-minute absolute deadline. Reading a request/body/response or waiting for upstream response headers has a ten-minute no-response deadline. Actual bytes reset the read deadline. The existing native selection has its own stricter budget.
- Before forwarding, a detected loop returns HTTP 422 `work_loop_paused`, `forwarded=false`, `automatic_retry=false`. After a dispatch whose outcome is unknown, the error forbids automatic retries. Before emitting any bytes, a response timeout can return HTTP 422; a partially emitted stream is aborted without a fake completion marker. Neither case replays tools or forcibly restarts an upstream service/model.
- Local `/localbrain/supervision/status` exposes coverage, limits and in-memory counters. Audit events contain numeric counters, typed decisions and request IDs, not command text, tool output or prompts. Counters reset when the Bridge restarts; this is not a cross-conversation job database.

## Verification

The console test suite includes identical and alternating cycles, distinct-result progress, new-user reset, Laya confidence/unavailability, before-compaction inspection, exact forwarding, real HTTP header/body/absolute timeouts and no replay. The separate `--work-loop-native-proof` uses the real CPU Laya worker inside a disposable Bridge with a public HTTP peer, without PC70 model requests or production knowledge writes. Runtime activation and the independently observed installed checks are recorded outside the public repository.

This monitor cannot diagnose a tool that Continue has not returned, a UI stopped awaiting human approval, or work on another machine without a supported event/control channel. Do not describe those cases as fully covered by Laya.
