# LocalBrain Agent v2 ClientPC pilot

> **Selected configuration (2026-09-25): GSQ-RCO + Pi/SoL-Pi.** The user chose this route without further benchmark runs and will judge future alternatives from actual usage. See `GSQ_PI_SPEC.md` for the fixed architecture, known evidence, and remaining rollout limits. Hermes/Bonsai and the unused `duration` fixture were removed from the active scope.

This source is uncommitted work built on the restored distribution baseline. The installed LocalBrain host and existing Continue Qwen model remain in use. The Sol-policy build is staged separately at `C:\Users\USER\AppData\Local\LocalBrain\agent-v2-sol-policy-20260925`, including the three Pi/Laya support scripts, and a global Continue MCP component points to it with the existing production client settings. The earlier `agent-v2` folder lacked those scripts and must not be used for a task. The active Continue rules direct scoped changes to Agent v2 MCP, and the Qwen model is limited to the `chat` role so direct Edit/Apply cannot bypass the host gate; the original config is backed up. The installed host executable and Bridge v2.1 were not replaced. The live IDE needs a reload to pick up the new versioned MCP path.

## Process path

`agent-v2 run` checks an individually registered Git root, starts Pi RPC in WSL, and selects `localbrain/local-qwen38`. The provider calls a Windows `model-relay` process over stdio. That process calls the existing `__BRIDGE_HOST__:__BRIDGE_PORT__` Bridge, which retains the mTLS path to ServerPC. No new listener is created.

The Actor has `read`, `grep`, `find`, `ls`, `edit`, and `write`. The policy extension blocks shell tools, paths outside the Git root, protected files, and writes outside the task's exact `allowed_files` list. The host runs only named validation commands. A fresh Critic uses the same Qwen provider with no Pi session or tools. Model text cannot set validation booleans.

At task start, the host may read one cited SecondBrain context packet through the existing mTLS Gateway. The query is capped at 240 characters, the requested budget is 1,024 bytes, and stale or oversized packets are discarded. The bounded packet is reference data for the Actor and fresh Critic; retrieval failure does not grant permission or mark validation complete. `agent-v2 knowledge-health` reads a query on stdin and reports counts only.

## Pilot command

The standard input JSON for `localbrain.exe agent-v2 run <registered-git-root>` has these fields:

```json
{
  "requirement": "Create a scoped change",
  "acceptance_criteria": ["Describe the observable result"],
  "risk": "LOW",
  "approved_high_risk": false,
  "allowed_files": ["relative/path.txt"],
  "required_tests": [{"kind": "git_diff_check"}]
}
```

Supported test kinds are `git_diff_check`, `dotnet_build`, `dotnet_test`, and `npm_test`. A .NET operation needs a relative `.csproj` target inside the selected Git root and existing offline restore metadata for it and its literal project references. The bounded graph permits up to 20 projects with distinct names; dynamic or external references and solutions remain unsupported. .NET artifacts and temporary files go under `.localbrain/artifacts/`, leaving the source projects' `bin` and `obj` untouched. `npm_test` runs at the root. Test stdout/stderr is counted and hashed without storing its text. `dotnet_test` additionally requires a nonzero executed-test count in the console summary; an exit code of zero with no tests is a failure. A new `run` requires a clean working tree and allows at most three Actor turns in the same Pi session. It rechecks tests and the Git tree after each turn, stops on unexpected files or test-created source changes, and uses a fresh Critic before marking a LOW-risk task complete. A nonzero process exit or `eligibleForCompletion: false` means the gate is still closed.

`agent-v2 resume <registered-git-root>` takes the identical request JSON on standard input. It resumes a `needs_work` or recoverable `failed` checkpoint with the same request hash, Git HEAD, changed-file list, and review-diff hash. It rejects unexpected files, test-created source changes, no-progress checkpoints, and completed tasks. It reuses the saved Pi session ID, allows at most three more Actor turns per invocation and six total, and re-runs tests and the fresh Critic. Older checkpoints without the hashes cannot be resumed automatically. A failure before any scoped file change still requires a fresh clean-tree run.

If the selected external reviewer is unavailable, the task enters `awaiting_external_review` (`awaiting_sol_review` remains readable for older checkpoints). `resume` verifies the same request, Git HEAD, changed files, and diff hash, then reruns the named tests, fresh Critic, and external review without another Actor turn. A negative review returns `needs_work`; the completion gate remains closed. Authentication failures and provider diagnostics are not written to task state.

After six Actor turns, ordinary `resume` stops. An explicit `agent-v2 recover <registered-git-root>` with the identical request can open one fresh Pi session using the bounded current diff as prior-work context. It requires an unchanged request, Git HEAD, file list, and diff hash, rejects no-progress or test-mutated checkpoints, permits at most three further Actor turns (nine total), and reruns tests and independent reviews. Recovery count is stored in the checkpoint so another fresh-session recovery is refused. It never commits changes or bypasses validation.

Pi RPC tool events are checked for deterministic repeated-work thresholds. When triggered, the host sends Pi's documented `abort` command, keeps the session ID, and leaves validation incomplete. Laya also sees compact progress snapshots every four tool calls within a turn. Its decision can stop the Actor for validation or replan; hard rules still take precedence.

SoL-Pi ObservationPack remains enabled in its conservative profile. The Pi tool allowlist permits only its read-only `obs_recall` extension tool in addition to the scoped file tools. The LocalBrain policy validates the `obs_` ID format and nonnegative offset; SoL-Pi limits each recalled page. Host-run validation output is kept only in bounded memory; task results contain byte counts, hashes, and a short list of fixed diagnostic codes. Repair prompts can use those codes and exit status. Raw test output is never sent to the Actor or persisted.

Runtime state is written to `.localbrain/local-validation.json` and `.localbrain/task-state.json` in the selected project. The v2 policy does not let Pi edit that directory. For NORMAL tasks, Laya selects Copilot, Sol, or both; confidence below the policy threshold selects both. HIGH tasks always require Sol and may also use Copilot. HIGH still requires an explicit approval flag, a matching one-use approval from an interactive user terminal, one Sol plan review before Actor execution, and one Sol final review. The validation gate remains closed unless each required review passes. Provider failure is tracked separately from code-review findings. `agent-v2 sol-health` checks the OAuth-backed reviewer with a fixed, read-only prompt.

To approve an exact HIGH task, the user runs `localbrain.exe agent-v2 approve-high <registered-git-root> <request-json-file>` in an interactive terminal, inspects the request JSON and displayed scope, then types the complete request SHA-256. The approval binds the request to the current Git HEAD, expires after 30 minutes, and is consumed before the Sol plan review. Redirected input/output cannot issue approval. An unattended MCP call cannot set this approval merely by supplying `approved_high_risk: true`.

The Sol policy fixes the model to `openai/gpt-6-sol`: NORMAL final review uses `medium`; HIGH plan and final review use `high`. On a genuine no-progress stop, Sol rescue is a read-only consultation at `high`, with one `xhigh` retry if the first answer is invalid, unresolved, or unavailable. It returns bounded guidance to the caller but does not edit files, resume the Actor, or open the validation gate. Automatic `max` selection is absent; attempts beyond the initial call and one retry are rejected. The reviewer uses the user-completed ChatGPT OAuth login, runs from a dedicated WSL directory, denies all OpenCode tools, and sends a bounded packet on standard input rather than the process command line. Obvious credential patterns block transmission. Raw provider diagnostics and prompts are not logged; authentication or review failure cannot mark a task complete.

Completed and failed runs append compact counts and decision metadata to `.localbrain/telemetry.jsonl`. Bounded SecondBrain chunk IDs record which cited evidence was used; the retrieved text, prompts, source paths, and raw tool output are excluded. Telemetry write failures do not override a validation result.

The selected project must ignore `.localbrain/` in Git. `agent-v2 run` holds an exclusive `.localbrain/active.lock` file handle for its lifetime, so another run in the same project fails before starting a second Actor.

`agent-v2 ask` is read-only; `agent-v2 critic` uses a fresh no-tool session; `agent-v2 decide` and `decide-batch` are Laya diagnostics; `agent-v2 path`, `pi-health`, and `laya-health` are health checks. All commands require the existing `clientsettings.json` (or a `LOCALBRAIN_CLIENT_CONFIG` path). Do not put credentials in prompts, task JSON, or logs.

## Separate Continue entry point

`localbrain.exe agent-v2-mcp` exposes `localbrain_agent_v2_run`, `localbrain_agent_v2_resume`, and read-only `localbrain_agent_v2_status` over stdio. The write tools accept a structured task and registered Git root, then call the same host-side policy, Pi session, tests, Critic, and validation gate as the CLI. Responses return counts and gate status, not raw model or test output; a stalled task may also return up to three bounded Sol rescue suggestions. The separate `continue-agent-v2-template.yaml` uses the same loopback Qwen model and this MCP server. The active user Continue config has been updated with a backed-up Pi rule and chat-only model role.

The template has not replaced the live Continue model. The isolated workspace pilot used a staged executable and a single registered sample Git root. Continue stores built-in tool policies per user, outside this YAML. The user reported setting the built-in create/edit/replace/terminal tools to Excluded; they should remain Excluded while using Pi so those tools cannot bypass the host policy. In the actual IDE pilot, a NORMAL task completed through `localbrain_agent_v2_run` in about 53 seconds, and both its run result and read-only status result were visible with the validation gate true. The newly added global MCP component uses the ten existing production-registered Git roots; no root was added or changed.

An isolated pilot workspace at `work/continue-pilot` contains a workspace-scoped MCP component, staged v2 executable path, and a project allowlist limited to one sample Git repository. Its MCP initialize/list/status probes passed. The actual Continue IDE run on `sample-ide-repo-4` returned completion eligible with npm/Git 2/2, fresh Critic PASS, one Sol final PASS, and a visible read-only status result. An identical completed request can now return `already_complete=true` without another Actor run after verifying its request hash, Git evidence, and persisted gate. This protects against duplicate delivery while a different request is still rejected.

## Current limits

- The Continue MCP route worked in an isolated IDE workspace. The global Agent v2 MCP component and separate executable are configured, but VS Code reload and connection confirmation remain. The one completed greeting comparison produced the same correct file and passed tests in the existing and Pi routes; it does not establish real-work usability or speed. No further benchmark run is planned by user choice. Startup/reboot recovery and production rollback validation remain unverified. OAuth-backed Sol review works in isolated NORMAL and synthetic HIGH-risk runs. Six-turn recovery is implemented and synthetically tested, but not tested on a genuinely exhausted task. The interactive HIGH approval command has not been exercised by a real user.
- No ServerPC Gateway or model-server changes.
- Laya uses CPU with 2 threads. Its current checkpoint confidence is uncalibrated/low; deterministic rules take precedence and low-confidence decisions fall back.
- SoL-Pi remains on its conservative profile: ActionFusion and ObservationPack on; OCC and EPR off.
