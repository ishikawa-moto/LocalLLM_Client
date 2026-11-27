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

The builder accepts the pinned ZIP (SHA256 `D681ACA70F24E1A3B9ADEA8D9BBE4D24D954AE74F71C7B648ED589E53DC8354B`) or the exact gzip response identified in the script. It creates a new `out/stable` directory and refuses to overwrite it. Use `-OutputDirectory` with a new path for another run. The output is 74,880,573 bytes with SHA256 `B36EBE0ABA206E299E9E908990F9100075ABCF47EB26DF8950EBA08FCA5A96A1`. The bundle SHA256 is `B1797CAFB5FD22A4F84C333CC7556550A244E4927BAD454D7AF603346385C5EF`.

The previous PC73 build (PR #42) is retained under `local-backups/continue-pc73/continue-2.0.0-pc73-pr42.vsix` on PC73. Its SHA256 is `8AA87ADF716FF70AA36153F45DE24BF66413C9E24E6E515E791B6DE2F87BAE1C`. No VSIX, download, dependency tree or extracted build is tracked.

## Deploy or restore separately

Before installing, retain the current VSIX and back up the existing Continue and MCP settings locally. If Agent v2 is in use, verify the selected LocalBrain release manifest and the SQLite managed and Windows native dependencies. Use [the MCP example](agent-v2-mcp.example.yaml) as a reference. Keep the existing client settings path and credentials on the host. The verified Agent v2 release was `9286abcc3394-toolfix` at the repository's already merged `9286abcc3394fc77804bb876f61b6d0bb4482d9a`. No source changes to the Host are needed to reproduce this extension.

Use the target VS Code profile and `code --install-extension <verified-vsix> --force`, then reload the window. Verify the installed bundle hash. To roll back this build on PC73, install `continue-2.0.0-pc73-pr42.vsix` from `local-backups/continue-pc73` and reload VS Code. Its bundle SHA256 is `8215BF49DB2B39516EAA5B32A8925DE1F1FA105E16340D69AC00E24D0D821946`. Preserve the working Agent v2 launch path through an extension rollback.

To check Windows path approval after reloading, ask Continue to use `ls` and `read_file` with absolute paths inside the opened workspace. Both should run without a prompt when set to Automatic. An absolute path outside the workspace still requires approval. To read a known UTF-8 file in the terminal, run `Get-Content -LiteralPath <path> -Encoding UTF8` directly in the PowerShell session.

## Tool result limits

The extension limits built-in and MCP results before they reach Continue history. A result preview is capped at 8 KiB, 160 lines, and 2,048 tokens using Continue's estimator. The final tool-call group shares the remaining input budget. If a result is shortened, narrow the search or request a smaller file range. Terminal stdout and stderr are captured in separate bounded buffers. PowerShell command lines normally start with UTF-8 output configuration, and the output decoder carries incomplete UTF-8 sequences across chunks.

For tool conversations, the outgoing prompt retains the latest tool-call group and user message while trimming older history at 75% of the planning budget. For PC73's `local-qwen38`, that budget is based on the verified 32,768-token actor window. Continue's configured context length stays in effect for the regular compiler limit. The extension compacts the outgoing prompt and logs numeric counters. Continue's manual conversation summary remains available through its existing command.

The extension reuses small, unchanged workspace file reads in memory using file metadata or the editor document version. A changed version is read again. Identical historical read or list results can also be collapsed in the outgoing prompt. Narrowed tool results and history excerpts do not create an additional full-output archive.

The focused test suite covers policy, Windows/Unix grep output and Git/file guards. Previous PC73 checks confirmed Qwen, Agent v2 status, file reads and `Setup-Client` at README.md line 8. Automatic, Ask First and Excluded UI results were reported by the operator. Remote Git mutations and PR merges were checked with mocks. The terminal and path checks prevent direct mistakes in supported calls. Scripts, aliases, alternate tools, races and unrecognized secret formats can evade checks. GitHub enforcement requires separate branch protection and credentials.
