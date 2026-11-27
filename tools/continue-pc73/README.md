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

The builder accepts the pinned ZIP (SHA256 `D681ACA70F24E1A3B9ADEA8D9BBE4D24D954AE74F71C7B648ED589E53DC8354B`) or the exact gzip response identified in the script. It creates a new `out/stable` directory and refuses to overwrite it. Use `-OutputDirectory` with a new path for another run. The output is 74,876,785 bytes with SHA256 `8AA87ADF716FF70AA36153F45DE24BF66413C9E24E6E515E791B6DE2F87BAE1C`. The bundle SHA256 is `8215BF49DB2B39516EAA5B32A8925DE1F1FA105E16340D69AC00E24D0D821946`.

The previous PC73 build is retained under `local-backups/continue-pc73/continue-2.0.0-localbrain-pc73-automatic.vsix` on PC73. Its SHA256 is `DA1F0E52F2CE51C2520BC06CF866C164FDA1EA1D4F28334C9AD60D48554B55E3`. No VSIX, download, dependency tree or extracted build is tracked.

## Deploy or restore separately

Before installing, retain the current VSIX and back up the existing Continue and MCP settings locally. If Agent v2 is in use, verify the selected LocalBrain release manifest and the SQLite managed and Windows native dependencies. Use [the MCP example](agent-v2-mcp.example.yaml) as a reference. Keep the existing client settings path and credentials on the host. The verified Agent v2 release was `9286abcc3394-toolfix` at the repository's already merged `9286abcc3394fc77804bb876f61b6d0bb4482d9a`. No source changes to the Host are needed to reproduce this extension.

Use the target VS Code profile and `code --install-extension <verified-vsix> --force`, then reload the window. Verify the installed bundle hash. To roll back this build on PC73, install the previous build from `local-backups/continue-pc73` and reload VS Code. Its bundle SHA256 is `39C56F2439DE5DCD79470479B9FBC3019170D5AFEF82BBCEE192C032A40176F5`. The older `continue-2.0.0-fixed.vsix` in the same backup directory has VSIX SHA256 `10A6D71564A5B3E3BA80BF2E2E2063B0E74B1FF1C39F26ECF4766CAC161BA120` and bundle SHA256 `8A19B6A8297A96B20A7D607BE9B56097CAA2EC24C6C7030E54D7DE3090F3DD3F`. Preserve the working Agent v2 launch path through an extension rollback.

To check Windows path approval after reloading, ask Continue to use `ls` and `read_file` with absolute paths inside the opened workspace. Both should run without a prompt when set to Automatic. An absolute path outside the workspace still requires approval. To read a known UTF-8 file in the terminal, run `Get-Content -LiteralPath <path> -Encoding UTF8` directly in the PowerShell session.

The focused test suite covers policy, Windows/Unix grep output and Git/file guards. Previous PC73 checks confirmed Qwen, Agent v2 status, file reads and `Setup-Client` at README.md line 8. Automatic, Ask First and Excluded UI results were reported by the operator. Remote Git mutations and PR merges were checked with mocks. The terminal and path checks prevent direct mistakes in supported calls. Scripts, aliases, alternate tools, races and unrecognized secret formats can evade checks. GitHub enforcement requires separate branch protection and credentials.
