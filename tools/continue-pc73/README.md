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

The builder accepts the pinned ZIP (SHA256 `D681ACA70F24E1A3B9ADEA8D9BBE4D24D954AE74F71C7B648ED589E53DC8354B`) or the exact gzip response identified in the script. It creates a new `out/stable` directory and refuses to overwrite it. Use `-OutputDirectory` with a new path for another run. The output is 74,884,338 bytes with SHA256 `7DD5AD38DAB23D62BEE4FD380CDC2A2F4A67C170F953A2B8BCA2FC0E4852A639`. The extension bundle SHA256 is `26057F88D30FF0681A73F2C65DE42774A1252BCB8506CCD0853EB3D11CCB4059`. The GUI bundle SHA256 is `2D2D2CC7575403494CC3DD224618C531293CE9F71A401E99A1F232DDC1DB29E3`.

The previous PC73 build is retained under `local-backups/continue-pc73/continue-2.0.0-pc73-automatic-location.vsix` on PC73. Its SHA256 is `EE2A2A17B9F0AC3BC8F7BF8A3DD7516786F9C6818265CFF3CCE94211A5367131`. No VSIX, download, dependency tree or extracted build is tracked.

## Deploy or restore separately

Before installing, retain the current VSIX and back up the existing Continue and MCP settings locally. If Agent v2 is in use, verify the selected LocalBrain release manifest and the SQLite managed and Windows native dependencies. Use [the MCP example](agent-v2-mcp.example.yaml) as a reference. Keep the existing client settings path and credentials on the host. The verified Agent v2 release was `9286abcc3394-toolfix` at the repository's already merged `9286abcc3394fc77804bb876f61b6d0bb4482d9a`. No source changes to the Host are needed to reproduce this extension.

Use the target VS Code profile and `code --install-extension <verified-vsix> --force`, then reload the window. Verify the installed bundle hashes. To roll back this build on PC73, install `continue-2.0.0-pc73-automatic-location.vsix` from `local-backups/continue-pc73` and reload VS Code. Its extension bundle SHA256 is `6A51A104718F7EDB857D66322CB468B4212F489A64971DD08BA960B55B3396A6`. Preserve the working Agent v2 launch path through an extension rollback.

To check Windows path approval after reloading, ask Continue to use `ls` and `read_file` with absolute paths inside and outside the opened workspace. The tool's configured policy applies in both places. Windows file permissions and the existing execution restrictions still govern the operation. Continue reports access denials with the current permissions and runs without administrative elevation. To read a known UTF-8 file in the terminal, run `Get-Content -LiteralPath <path> -Encoding UTF8` directly in the PowerShell session.

## Tool result limits

The extension limits built-in and MCP results before they reach Continue history. A result preview is capped at 8 KiB, 160 lines, and 2,048 tokens using Continue's estimator. The final tool-call group shares the remaining input budget. If a result is shortened, narrow the search or request a smaller file range. Terminal stdout and stderr are captured in separate bounded buffers. PowerShell command lines normally start with UTF-8 output configuration, and the output decoder carries incomplete UTF-8 sequences across chunks.

For tool conversations, the outgoing prompt retains the latest tool-call group and user message while trimming older history at 75% of the planning budget. For PC73's `local-qwen38`, that budget is based on the verified 32,768-token actor window. Continue's configured context length stays in effect for the regular compiler limit. The extension compacts the outgoing prompt and logs numeric counters. Continue's manual conversation summary remains available through its existing command.

The extension reuses small, unchanged workspace file reads in memory using file metadata or the editor document version. A changed version is read again. Identical historical read or list results can also be collapsed in the outgoing prompt. Narrowed tool results and history excerpts do not create an additional full-output archive.

## Local Qwen Agent response recovery

The native Continue Agent using `local-qwen38` through the PC73 loopback Bridge streams prose and checks for strongly repeated prose and raw tool markup. It holds native tool deltas until the response has valid call IDs, advertised names and JSON arguments. At a definite output fault, it cancels inference, discards the current partial response from the GUI, and retries once using the original user request, selected workspace and previous tool results. If tools with possible side effects ran during the current user turn, recovery runs without tools and returns a progress report. Transport errors and Bridge work-loop pauses retain their existing behavior.

Scope guidance supplements the user's target path. A manual summary with no later user instruction retains the last explicit user message. The existing Bridge supervisor still checks repeated tool results. Managed Agent v2 remains the route with persistent Pi task state, replanning and Completion Gate. The extension emits only the recovery reason, attempt count and retry flag in its recovery diagnostics.

The focused test suite covers policy, Windows/Unix grep output and Git/file guards. Previous PC73 checks confirmed Qwen, Agent v2 status, file reads and `Setup-Client` at README.md line 8. Automatic, Ask First and Excluded UI results were reported by the operator. Remote Git mutations and PR merges were checked with mocks. The terminal and path checks prevent direct mistakes in supported calls. Scripts, aliases, alternate tools, races and unrecognized secret formats can evade checks. GitHub enforcement requires separate branch protection and credentials.
