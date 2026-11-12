# PC73 Runner MCP trial

The `runner-mcp <absolute-trial-directory>` command uses the existing managed Agent v2 MCP, Windows task worker, Pi 1.4.0 and the configured Bridge. Qwen inference uses the existing PC70 Gateway. Task ownership and the Git checkout live on PC73.

Use a short local trial path. Create a standalone trusted Git checkout named `repo` on its task branch, with `.localbrain/` ignored and a clean working tree. Put the exact checkout in the trial's `projects.json`. The operator supplies a trial `clientsettings.json` with those absolute projects and audit paths, the existing Gateway and certificate references, a private Pi 1.4.0 agent directory, `personalValidation` enabled for the named Git check, and AgentV3 disabled. The trial owns its `control` directory and approval records. The path for the native receipt's pending file must be shorter than 260 characters. File restrictions depend on the existing trusted-project policy and Host gate.

Use session-specific Codex config overrides to point the MCP entry at the trial Host EXE:

```toml
[mcp_servers.localbrain_runner_trial]
command = "C:/path/to/trial-release/localbrain.exe"
args = ["runner-mcp", "C:/short/trial"]
```

Submit with `localbrain_agent_v2_run`. Supply the dedicated workspace, LOW risk, up to eight explicit relative files, one `git_diff_check`, and the command contract with `max_commands=0`, `working_directories=["."]` and `output_directories=[]`. The operator confirms the actual native scope. Keep the checkout reserved to that task while the worker owns it.

Use `localbrain_agent_v2_status` for the current task. The result contains its task ID, phase, completion gate, changed files, base commit, diff hash and validation flags. Retrieve the patch separately from the dedicated Git checkout after the worker exits. Inspect the local job record for a blocked reason. An identical submission observes the prior result. Use `localbrain_agent_v2_cancel` with the exact workspace and task ID, then wait for a terminal state before changing the checkout. A dialog exit without a receipt ends the task as blocked and retains its launch record.

Before accepting a change, inspect the diff and run independent checks for the task's acceptance criteria. `git_diff_check` checks patch formatting, and the local Critic gives a model verdict. A task can be `complete` while its output fails a behavioral check. Preserve the result for diagnosis if an independent check fails.

For rollback, remove the session-specific MCP overrides after its task reaches a terminal state. Retain the checkout, task receipts and Canonical evidence. Commit only an independently accepted diff on its task branch before considering any separately approved integration.

## External rescue for managed development tasks

The Host can delegate a stalled Qwen task without a repository policy entry.
The default packet contains the task and the starting contents of its exact
allowed files. Retrieved knowledge, raw logs, and the failed worktree diff
stay local in the default path. Slack and OCR remain restricted.

Set `external_ai_disabled: true` in the task JSON for a local-only task. From
the Host administration CLI, use
`localbrain agent-v2 egress-set <registered-workspace> deny` to disable egress
for a repository, or use `default` in place of `deny` to remove the override.
The policy is stored in `codex-egress-policies.json` under the Host control root
and audited in Canonical. Existing explicit file-scoped `auto` and `manual`
entries remain supported. A `manual` entry uses exact export approval.

Set `fullRescueRouting` to enable the four-model order. Each enabled endpoint
uses `rescueEnabled`, an absolute CLI path, its SHA-256, and a model in the
endpoint's family. `difficultyConfidenceThreshold` defaults to 0.75. LOW gets
one fresh local repair before Terra and Haiku. NORMAL uses Terra, Haiku, Sol,
and Opus. HIGH uses Sol and Opus, with its existing scope authorization.

The Host can retain a candidate without validation. Promotion requires the
existing registered .NET tests or trusted personal npm validation with a
recognized non-empty test result, unchanged verification inputs, and a fresh
local Critic. Local verification and completion do not need export permission.
The same repository can retrieve
the completed repair as historical evidence for the next Qwen task. Adoption
into shared SecondBrain knowledge still follows its separate approval policy.
