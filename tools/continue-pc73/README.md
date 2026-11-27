# LocalBrain Continue 2.0.0 for PC73

This package records the Windows x64 Continue VSIX validated on PC73. It patches a pinned public Marketplace VSIX and does not install it or update LocalBrain services. See [NOTICE](NOTICE) for upstream attribution.

- `src/patched/` contains the TypeScript inputs for approval, Windows workspace path checks, execution and file editing checks, and Windows grep.
- `scripts/patch-bundle.cjs` compiles those modules and wires them into the extension bundle. Its anchors are specific to the pinned public VSIX.
- `packaging/baseline-bundle-edits.json` retains the deployed bundle's pre-existing changes to chat trimming and YAML tool policies. `scripts/apply-baseline.cjs` applies those exact edits separately. Their original TypeScript is unavailable in the repair checkout.
- `packaging/stable-layout.tsv` pins every packaged file's SHA256, entry order and timestamp. The packer verifies the payload before producing the ZIP.

## Reproduce

From this directory on Windows x64, use Git, Node.js 22.17.0 with npm 10.9.2, and PowerShell 7.6.9 for the reference ZIP encoding. Download the [official Continue extension](https://marketplace.visualstudio.com/items?itemName=Continue.continue) at version 2.0.0 and target `win32-x64`.

```powershell
npm ci --ignore-scripts --no-audit --no-fund
$url = 'https://marketplace.visualstudio.com/_apis/public/gallery/publishers/Continue/vsextensions/continue/2.0.0/vspackage?targetPlatform=win32-x64'
$base = Join-Path $env:TEMP 'continue-2.0.0-win32-x64.vsix'
curl.exe --fail --location --compressed -o $base $url
pwsh -File ./scripts/build-vsix.ps1 -BaseVsix $base -RunTests
```

The builder accepts the pinned ZIP (SHA256 `D681ACA70F24E1A3B9ADEA8D9BBE4D24D954AE74F71C7B648ED589E53DC8354B`) or the exact gzip response identified in the script. It creates a new `out/stable` directory and refuses to overwrite it. Use `-OutputDirectory` with a new path for another run. The output is 74,885,548 bytes with SHA256 `18780DEF9A775B14EE4981DACD1448B9DF9C7F62975D3304DB4790593963B54F`. The extension bundle SHA256 is `B0255BAAA77D77AC3C8E6357E49AD5AB0901086DBAF968816FB01A025CECB77F`. The GUI bundle SHA256 is `AA30BF306A67567D3FFBFD44B252ED10DE3BDBFDEDDAD612EE409012F9681D35`.

The previous PC73 build is retained under `local-backups/continue-pc73/continue-2.0.0-pc73-automatic-location.vsix` on PC73. Its SHA256 is `EE2A2A17B9F0AC3BC8F7BF8A3DD7516786F9C6818265CFF3CCE94211A5367131`. No VSIX, download, dependency tree or extracted build is tracked.

## Deploy or restore separately

Before installing, retain the current VSIX and back up the existing Continue and MCP settings locally. The managed handoff requires a LocalBrain release from this source for the `agent-v2-mcp` command. Verify the release manifest, SQLite managed and Windows native dependencies, and the native confirmation script binding. Update the Agent v2 MCP executable path using [the MCP example](agent-v2-mcp.example.yaml) as a reference. Keep the existing client settings path and credentials on the host. The previous `9286abcc3394-toolfix` release can still serve existing Agent v2 tools.

Use the target VS Code profile and `code --install-extension <verified-vsix> --force`, then reload the window. Verify the installed bundle hashes. For a rollback on PC73, reinstall the retained response-recovery VSIX with SHA256 `7DD5AD38DAB23D62BEE4FD380CDC2A2F4A67C170F953A2B8BCA2FC0E4852A639`, restore the original Agent v2 executable path if necessary, and reload VS Code. The previous extension bundle SHA256 is `26057F88D30FF0681A73F2C65DE42774A1252BCB8506CCD0853EB3D11CCB4059`.

To check Windows path approval after reloading, ask Continue to use `ls` and `read_file` with absolute paths inside and outside the opened workspace. The tool's configured policy applies in both places. Windows file permissions and the existing execution restrictions still govern the operation. Continue reports access denials with the current permissions and runs without administrative elevation. To read a known UTF-8 file in the terminal, run `Get-Content -LiteralPath <path> -Encoding UTF8` directly in the PowerShell session.

## Tool result limits

The extension limits built-in and MCP results before they reach Continue history. A result preview is capped at 8 KiB, 160 lines, and 2,048 tokens using Continue's estimator. The final tool-call group shares the remaining input budget. If a result is shortened, narrow the search or request a smaller file range. Terminal stdout and stderr are captured in separate bounded buffers. PowerShell command lines normally start with UTF-8 output configuration, and the output decoder carries incomplete UTF-8 sequences across chunks.

For tool conversations, the outgoing prompt retains the latest tool-call group and user message while trimming older history at 75% of the planning budget. For PC73's `local-qwen38`, that budget is based on the verified 32,768-token actor window. Continue's configured context length stays in effect for the regular compiler limit. The extension compacts the outgoing prompt and logs numeric counters. Continue's manual conversation summary remains available through its existing command.

The extension reuses small, unchanged workspace file reads in memory using file metadata or the editor document version. A changed version is read again. Identical historical read or list results can also be collapsed in the outgoing prompt. Narrowed tool results and history excerpts do not create an additional full-output archive.

## Local Qwen Agent response recovery

The native Continue Agent using `local-qwen38` through the PC73 loopback Bridge streams prose and checks for strongly repeated prose and raw tool markup. It holds native tool deltas until the response has valid call IDs, advertised names and JSON arguments. At a definite output fault, it cancels inference, discards the current partial response from the GUI, and retries once using the original user request, selected workspace and previous tool results. If tools with possible side effects ran during the current user turn, recovery runs without tools and returns a progress report. Transport errors and Bridge work-loop pauses retain their existing behavior.

Scope guidance supplements the user's target path. A manual summary with no later user instruction retains the last explicit user message. The extension emits only the recovery reason, attempt count and retry flag in its recovery diagnostics.

### Handoff to managed recovery

When Qwen repeats its response again after one retry, or the loopback Bridge reports that it blocked repeated tool results before forwarding, Continue can hand off one managed task. This requires the newer Agent v2 MCP server, an enabled `localbrain_agent_v2_recover_continue` tool and a single registered Git root. Ask First and Excluded still apply. The same turn keeps one recovery ID, so a duplicate observes the existing job. No tools or model requests from the failed turn are replayed by the handoff. An earlier tool call with possible side effects stops the handoff.

Place one `localbrain-recovery` fenced JSON block in the user request to authorize a particular recovery scope. Use a clean development checkout. The Host refuses `main`, `master`, detached HEAD and dirty trees at admission. This example authorizes one file and the Host-owned npm test in a synthetic project.

````text
```localbrain-recovery
{
  "task": {
    "requirement": "Repair the example formatter",
    "acceptance_criteria": ["The example formatter tests pass"],
    "risk": "NORMAL",
    "approved_high_risk": false,
    "allowed_files": ["src/formatter.js"],
    "required_tests": [{"kind": "npm_test"}, {"kind": "git_diff_check"}],
    "command_execution": {
      "environments": ["windows"], "working_directories": ["."], "output_directories": [],
      "max_commands": 0, "max_task_minutes": 12, "max_actor_turns": 8
    }
  }
}
```
````

Use `dotnet_test` with an exact repository-relative `.csproj` to test a .NET repair. The Windows grant binds the exact request. If the bounded local Pi run stalls, the existing Host export policy and verified rescue worktree decide whether any enabled provider can receive a repair packet. Only a Host-verified candidate is eligible for promotion. A queued task is pending. Use `localbrain_agent_v2_status` with its `task_id` to observe that exact task.

To request SecondBrain review, add a top-level `knowledge` object with a `project` and an ordered `references` array. Each reference has `path`, `expected_hash`, `expected_metadata_hash` and `relevant_location` such as `L1-L2`. Use two to four current immutable sources from that project. The Host requires both a verified external promotion and completion gates before extracting a provisional lesson with local Qwen.

Deploy the matching PC70 decision reviewer before activating this Host. The installed policy protects the Host-to-PC70 review and adoption route for every project. Two successful reviews can automatically adopt the exact draft. A definitive failure on either pass preserves its issues and returns the draft for human review. `brain_list_human_reviews` lists pending requests and `brain_decision_status` shows the issues for an exact ID. Call `brain_reaffirm_decision` only when the user explicitly requests adoption after seeing the issues. The Windows dialog binds confirmation to that exact draft and receipt. An uncertain execution remains available for human investigation. Previous `review_only` jobs retain their original authorization.

The focused test suite covers policy, Windows/Unix grep output and Git/file guards. Previous PC73 checks confirmed Qwen, Agent v2 status, file reads and `Setup-Client` at README.md line 8. Automatic, Ask First and Excluded UI results were reported by the operator. Remote Git mutations and PR merges were checked with mocks. The terminal and path checks prevent direct mistakes in supported calls. Scripts, aliases, alternate tools, races and unrecognized secret formats can evade checks. GitHub enforcement requires separate branch protection and credentials.
