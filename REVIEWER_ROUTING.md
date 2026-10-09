# ClientPC Agent v2 Reviewer routing

This source implementation extends Agent v2 after the existing named tests and fresh local Qwen Critic. It does not change ServerPC, Bridge v2.1, Pi/SoL-Pi, Continue tool policy, or the existing Sol model and effort settings. The installed Continue MCP host is not changed by building this repository.

## Route and completion rules

- `reviewer-routing.json` sets the low-confidence threshold (currently 0.65) and the maximum number of Astra comparisons (currently 6). Invalid or absent policy fails closed.
- Laya receives a compact metadata-only state and proposes `copilot`, `sol`, or `both`. LOW tasks retain the old no-external-review rule. LOW confidence or an unavailable Laya prediction selects both. HIGH always requires Sol; a confident `both` also invokes Copilot.
- Sol and Copilot receive independent evidence. Neither receives the other's answer. A valid issue from either selected provider blocks completion. An unavailable provider can fall back to the other for NORMAL tasks; HIGH cannot pass without Sol. Provider errors are never treated as a code-quality PASS or ISSUES result.
- Existing HIGH approval, Sol plan review, named tests, fresh Critic, bounded Actor turns, and checkpoint checks remain in force. Older `awaiting_sol_review` checkpoints are still readable.

## Provider boundaries

- Sol keeps `openai/gpt-6-sol` through the current ChatGPT OAuth-backed OpenCode path. NORMAL final review uses medium; HIGH plan and final review use high. Rescue remains high then xhigh.
- Copilot CLI uses a temporary directory holding only a bounded review packet. The command uses `--available-tools=view`, denies write/shell/memory, disables built-in MCPs and remote sessions, suppresses custom instructions, and does not ask the user for tool permission. It cannot see the original Git workspace through its working directory. Its response must parse as a bounded PASS/ISSUES object. A provider failure yields UNAVAILABLE.
- A shared evidence builder rejects oversized packets and recognizable credentials before a Copilot or Astra call. No reviewer receives the Implementer conversation. Provider stderr is discarded without logging.
- Astra is a meta evaluator only. On at most six naturally occurring valid dual reviews, it compares the *existing* Sol and Copilot findings and may move a bounded local weight by 0.05. It cannot add code findings, approve a task, or open a gate. Its result is recorded at `%LOCALAPPDATA%\LocalBrain\reviewer-calibration.json` as counts and weights, without the review packet. It uses the official Codex CLI with `gpt-6-astra`, a read-only ephemeral session, and the existing ChatGPT authentication. No API key fallback exists.

## Verification and current limit

The .NET Release build and local routing/gate checks pass. A synthetic Copilot CLI review passed in noninteractive mode. A synthetic `gpt-6-astra` meta comparison completed through the existing ChatGPT-authenticated Codex CLI; an initial call returned UNAVAILABLE and a later call succeeded, so transient provider failure remains possible. A Laya routing probe returned `both` with confidence 0.0146, so the configured conservative rule will route such tasks to both providers. Astra comparisons cannot override that low-confidence rule; they only adjust borderline NORMAL choices after enough valid samples. A new isolated Git task completed in one Actor turn with named tests, fresh Critic, Sol, Copilot, and Astra comparison all passing. The old completed pilot tasks were not rerun.

The framework-dependent host was deployed to `C:\Users\USER\AppData\Local\LocalBrain\agent-v2-reviewer-routing-20260928`. All 12 published files matched the staged SHA-256 and size. Continue's MCP YAML now references the new executable, with its original contents backed up at `C:\Users\USER\.continue\mcpServers\localbrain-agent-v2.yaml.reviewer-rollback`. The new executable passed MCP `initialize` and `tools/list` (three tools). After the user's VS Code window reload, the live Agent v2 MCP process used the new executable.

To roll back, copy `localbrain-agent-v2.yaml.reviewer-rollback` over `localbrain-agent-v2.yaml` and reload the VS Code window. The previous executable remains at `C:\Users\USER\AppData\Local\LocalBrain\agent-v2-context-telemetry-20260926\localbrain.exe`. Leave the current Bridge and client settings in place. Preserve existing task checkpoints; do not run or resume a completed task again.
