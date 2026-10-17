# Agent v2: task-scoped command execution

Continue submits implementation and terminal work to the Windows Host. The Host returns a task ID promptly and owns the background execution independently of the MCP connection. `queued` and `running` are not completion.

## One permission for one task

`localbrain_agent_v2_run` accepts the exact requirement, acceptance criteria, editing scope, required tests and `command_execution`. The Windows confirmation shows these together. A genuine native confirmation grants commands in this exact task and its verified resumptions; a model-supplied boolean or repository text cannot grant it. Changing the requirement, command scope or baseline requires a new task/grant. Cancellation and completion end the grant. Recorded time/turn/command/output budgets still apply. UAC and reauthentication remain OS operations.

Use trusted personal project code. A cwd restriction is not a filesystem/network sandbox for arbitrary programs. The Host enforces job ownership, admission, deadlines and output limits; it does not claim isolation against hostile scripts or daemon escape.

Example `task` for a registered Git root (initial worktree must be clean):

```json
{
  "requirement": "Run the requested Node CLI batch and retain one final CSV per ID. Preserve existing results.",
  "acceptance_criteria": ["Every requested ID has a verified final CSV; no existing result was overwritten."],
  "risk": "LOW",
  "approved_high_risk": false,
  "allowed_files": ["**"],
  "required_tests": [{"kind":"command_exit","target":"requested_batch"},{"kind":"git_diff_check"}],
  "command_execution": {
    "environments": ["windows","wsl"],
    "working_directories": ["."],
    "output_directories": ["../out"],
    "max_command_seconds": 21600,
    "max_task_minutes": 480,
    "max_actor_turns": 60,
    "max_commands": 256,
    "max_output_bytes": 2097152
  }
}
```

A scope of `**` or `src/**` requires the command contract and is displayed in the native grant. Legacy exact-file task hashes remain compatible. File tools continue to exclude protected metadata and secret paths. The initial clean-tree and bounded review-packet requirements remain; existing uncommitted work is never automatically committed, stashed or removed to make admission pass. HEAD-changing Git operations are outside the ordinary same-HEAD task contract.

## Execution and evidence

The Actor uses `host_command_start`, `host_command_status`, `host_command_cancel`. Program and arguments are separate fields. Windows jobs close owned descendants; WSL uses a login shell (including configured Volta-like environment managers), an owned process group and a short Host lease. Long commands do not rely on Continue's terminal timeout.

Each operation has a stable ID, immutable intent, bounded raw logs, terminal receipt, exit code and declared output hashes. Keep declared outputs stable final files; do not declare transient `result.csv` if a later operation renames it. For a batch, verify all final renamed outputs in the batch operation. Expected-output verification has finite size/time bounds (128MiB per file, 512MiB total, 15 seconds; at most256 declared files). Oversized/unknown outcomes cannot qualify as successful.

Repeated submission observes the existing task. Reusing an operation ID with different arguments is refused. An intent without a terminal outcome is `outcome_unknown`, never automatically replayed under another ID. Cancellation targets the exact task and retains receipts. Missing worker identity/unknown launch is inspected rather than restarted automatically.

Real receipts feed the single canonical writer and TaskMemory. Laya counts new Host-verified progress, not repeated status calls or model completion claims. The worker continues across ordinary Actor chunks and context handoff within the original finite budget. Normal tests, a fresh local Critic and existing risk-based external review gates still decide completion. Genuine no-progress, invalid evidence, scope/baseline drift, unavailable review, cancellation and exhausted budgets remain truthful stop conditions.

Managed model calls obtain a one-use Host capability bound to the accepted provider payload. Only that capability avoids a second Bridge conversation-level Laya/TaskMemory pass. Ordinary conversations retain conversation history management and loop supervision; all routes retain network deadlines. A caller's arbitrary HTTP label cannot select the managed route.

## Continue configuration

Use Agent v2 for coding, file changes, scripts, builds and tests. Direct read-only investigation and ordinary chat remain available. Disable built-in file mutation and terminal tools in Continue's supported tool-policy UI (`Excluded`); set Agent v2 run/resume/status/cancel according to the desired UI policy. `Automatic` on run/resume removes redundant Continue confirmations; the genuine one-task native grant remains. Do not edit extension private state to fake user settings. Reload the VS Code window after activating the new MCP executable.

`localbrain_agent_v2_status` reports background ownership and canonical completion. Do not spin on status. The background Host continues even if Continue ends its current response. Use `localbrain_agent_v2_cancel` with the current exact task ID to stop it. The corresponding local CLI supports `agent-v2 managed-status <registered-root>` and `managed-cancel <registered-root> <task-id>`.

## Runtime packaging

Build and run normal regression tests first. Publish the exact tested assembly and its scripts. The installer writes `task-command-native-code.json` with SHA256 of the installed `Confirm-TaskCommands.ps1`; the Host refuses unqualified confirmation code. Keep private client settings outside Git. Never replace or delete a canonical DB to activate this feature. Preserve the configured Server model/context, Client TaskMemory and SecondBrain policies.
