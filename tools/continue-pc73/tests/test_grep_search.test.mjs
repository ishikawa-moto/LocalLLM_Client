// Tests for the Windows/Unix grep_search fixes:
//   core/util/grepSearch.ts              (formatGrepSearchResults)
//   core/tools/implementations/grepSearch.ts (splitGrepResultsByFile + impl flow)
//   extensions/vscode/src/VsCodeIde.ts   (getSearchResults maxResults regex)
// Uses the real patched TypeScript sources and the real VSIX-bundled rg.exe.
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";
import {
  WORKTREE_ROOT,
  PATCHED_ROOT,
  loadPatchedModule,
  stripImports,
  transpileText,
} from "./lib/transpile.mjs";
import { test, assertEqual, assertTrue, assertFalse, summary } from "./lib/tinytest.mjs";

const require = createRequire(import.meta.url);

const suite = "grep_search";

// ---------------------------------------------------------------------------
// 1. formatGrepSearchResults (real module)
// ---------------------------------------------------------------------------
const { formatGrepSearchResults } = loadPatchedModule(
  "core/util/grepSearch.ts",
);

await test(`${suite}: formatGrepSearchResults handles Windows .\\ headings`, () => {
  const input = [
    ".\\README.md",
    "8:2. `Setup-Client.cmd`...",
    "-7:1. ServerPC...",
    "",
    ".\\Setup-Client.cmd",
    "2:powershell.exe ...",
  ].join("\n");
  const { formatted, numResults, truncated } = formatGrepSearchResults(input);
  assertEqual(numResults, 2, "numResults");
  assertFalse(truncated, "not truncated");
  assertTrue(formatted.includes(".\\README.md"), "keeps README.md path");
  assertTrue(formatted.includes(".\\Setup-Client.cmd"), "keeps cmd path");
  assertTrue(formatted.includes("2. `Setup-Client.cmd`..."), "keeps match line");
});

await test(`${suite}: formatGrepSearchResults handles Unix ./ headings`, () => {
  const input = ["./README.md", "8:setup", "", "--", "", "./a.md", "3:foo"].join("\n");
  const { numResults } = formatGrepSearchResults(input);
  assertEqual(numResults, 2, "numResults counts file paths, not --");
});

await test(`${suite}: formatGrepSearchResults truncates at maxChars`, () => {
  const lines = [];
  for (let i = 0; i < 300; i++) lines.push(`.\\file${i}.txt`, `${i}:content line`);
  const { formatted, truncated, numResults } = formatGrepSearchResults(
    lines.join("\n"),
    7500,
  );
  assertTrue(truncated, "truncated");
  assertEqual(formatted.length, 7500, "length");
  assertTrue(numResults > 0, "counts results");
});

// ---------------------------------------------------------------------------
// 2. Real rg.exe (VSIX-bundled) + format + split: Setup-Client in README.md
// ---------------------------------------------------------------------------
const repo = path.resolve(WORKTREE_ROOT, "..", "..");
const RG = path.join(process.env.PC73_STAGE_DIR, "extension/out/node_modules/@vscode/ripgrep/bin/rg.exe");
assertTrue(fs.existsSync(RG), "VSIX-bundled rg.exe exists");

function runRipgrep(query) {
  // Exact args used by VsCodeIde.getSearchResults (defaultIgnoresGlob replaced
  // by an equivalent node_modules exclusion; parsing behavior is what we test).
  return execFileSync(
    RG,
    [
      "--no-config",
      "--line-number",
      "-i",
      "-C", "2", "--heading",
      "--glob", "/README.md",
      "--glob", "/Setup-Client.cmd",
      "--glob", "/SOURCE_PROVENANCE.md",
      "-m",
      "100",
      "-e",
      query,
      ".",
    ],
    { cwd: repo, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] },
  );
}

await test(`${suite}: rg.exe emits .\\ headings on Windows (ground truth)`, () => {
  const out = runRipgrep("Setup-Client");
  assertTrue(out.includes(".\\"), "contains Windows-style heading");
  assertFalse(/^\.\/.+$/m.test(out), "no Unix-style heading on Windows");
});

let realRgOutput;
await test(`${suite}: Setup-Client search -> numResults > 0 (was the 0-result bug)`, () => {
  realRgOutput = runRipgrep("Setup-Client");
  const { formatted, numResults } = formatGrepSearchResults(realRgOutput, 7500);
  assertTrue(numResults > 0, `numResults was ${numResults}`);
  assertTrue(formatted.includes(".\\README.md"), "README.md result present");
});

await test(`${suite}: README.md line 8 match agrees with git grep`, () => {
  const gitGrep = execFileSync(
    "git",
    ["grep", "-n", "Setup-Client", "--", "README.md"],
    { cwd: repo, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] },
  ).trim();
  const first = gitGrep.split("\n")[0];
  const m = /^README\.md:(\d+):(.*)$/.exec(first);
  assertTrue(m, `git grep output unexpected: ${first}`);
  assertEqual(m[1], "8", "README.md match line is 8");
  const { formatted } = formatGrepSearchResults(realRgOutput, 7500);
  assertTrue(
    formatted.includes("8:" + m[2]),
    "rg-based formatted output contains the same line content",
  );
});

// ---------------------------------------------------------------------------
// 3. splitGrepResultsByFile + grepSearchImpl flow (real sources, type-only
//    imports stubbed)
// ---------------------------------------------------------------------------
const implSource = stripImports(
  fs.readFileSync(
    path.join(PATCHED_ROOT, "core/tools/implementations/grepSearch.ts"),
    "utf8",
  ),
);
const utilSource = stripImports(
  fs.readFileSync(path.join(PATCHED_ROOT, "core/util/grepSearch.ts"), "utf8"),
);
const prelude = `
class ContinueError extends Error {
  constructor(reason, message) { super(message); this.reason = reason; }
}
const ContinueErrorReason = { SearchExecutionFailed: "SearchExecutionFailed" };
function prepareQueryForRipgrep(query) { return { query, warning: null }; }
function getStringArg(args, key) { return args?.[key]; }
`;
const combined = prelude + utilSource + "\n" + implSource;
const implPath = transpileText(
  combined,
  "core/tools/implementations/grepSearchCombined.ts",
);
const impl = require(implPath);

await test(`${suite}: splitGrepResultsByFile splits Windows output per file`, async () => {
  const { formatted } = formatGrepSearchResults(realRgOutput, 7500);
  // splitGrepResultsByFile is not exported; exercise it via grepSearchImpl
  // with splitByFile: true.
  const contextItems = await impl.grepSearchImpl(
    { query: "Setup-Client", splitByFile: true },
    {
      ide: {
        getSearchResults: async () => realRgOutput,
      },
    },
  );
  assertTrue(Array.isArray(contextItems), "returns context items");
  assertTrue(contextItems.length >= 3, "one item per file (plus warnings)");
  const names = contextItems.map((i) => i.name).join("\n");
  assertTrue(names.includes("Search results in README.md"), "README.md item");
  assertTrue(
    names.includes("Search results in Setup-Client.cmd"),
    "Setup-Client.cmd item",
  );
  const readmeItem = contextItems.find((i) =>
    i.name.includes("README.md"),
  );
  assertTrue(readmeItem, "README.md item exists");
  assertFalse(
    readmeItem.content.startsWith(".\\"),
    "content must not start with the file path line",
  );
});

await test(`${suite}: grepSearchImpl single output (splitByFile=false)`, async () => {
  const contextItems = await impl.grepSearchImpl(
    { query: "Setup-Client" },
    { ide: { getSearchResults: async () => realRgOutput } },
  );
  assertEqual(contextItems.length, 1, "single context item");
  assertEqual(contextItems[0].name, "Search results", "item name");
  assertTrue(contextItems[0].content.includes(".\\README.md"), "content");
});

await test(`${suite}: grepSearchImpl zero results message`, async () => {
  const contextItems = await impl.grepSearchImpl(
    { query: "Setup-Client" },
    { ide: { getSearchResults: async () => "No matches found." } },
  );
  assertEqual(
    contextItems[0].content,
    "The search returned no results.",
    "zero-result message",
  );
});

await test(`${suite}: Unix-style output works through the impl`, async () => {
  const unixOut = ["./README.md", "8:Setup-Client here", "", "./b.md", "12:other Setup-Client"].join("\n");
  const contextItems = await impl.grepSearchImpl(
    { query: "Setup-Client", splitByFile: true },
    { ide: { getSearchResults: async () => unixOut } },
  );
  const names = contextItems.map((i) => i.name).join("\n");
  assertTrue(names.includes("Search results in README.md"), "README.md item");
  assertTrue(names.includes("Search results in b.md"), "b.md item");
});

// ---------------------------------------------------------------------------
// 4. getSearchResults maxResults truncation regex (source-level + behavior)
// ---------------------------------------------------------------------------
const vsCodeIdeSource = fs.readFileSync(
  path.join(PATCHED_ROOT, "extensions/vscode/src/VsCodeIde.ts"),
  "utf8",
);


const { limitGrepSearchResults } = loadPatchedModule("core/util/grepSearch.ts");
const start = vsCodeIdeSource.indexOf("  async getSearchResults(");
const end = vsCodeIdeSource.indexOf("  async getProblems(", start);
const fakeIde = require(transpileText(
  'const vscode = { env: {} }; const defaultIgnoresGlob = "!node_modules";\n' +
  'const limitGrepSearchResults = require("./core/util/grepSearch.js").limitGrepSearchResults;\n' +
  "export class MockIde {\n" + vsCodeIdeSource.slice(start, end) + "\n}",
  "ide-grep-fixture.ts",
)).MockIde;

await test(suite + ": VsCodeIde.getSearchResults limits file headings across workspace roots", async () => {
  for (const sep of ["/", "\\"]) {
    const ide = new fakeIde();
    const first = ["." + sep + "a.md", "1:hit", "--", "8:hit", "", "." + sep + "b.md", "2:hit"].join("\r\n");
    const second = ["." + sep + "c.md", "3:hit"].join("\r\n");
    ide.getWorkspaceDirs = async () => [first, second];
    ide.runRipgrepQuery = async (stdout, argv) => {
      assertTrue(argv.includes("--line-number"));
      assertEqual(argv[argv.indexOf("-m") + 1], "2");
      assertEqual(argv[argv.indexOf("-e") + 1], "hit");
      return stdout;
    };
    const results = await ide.getSearchResults("hit", 2);
    const { formatted, numResults } = formatGrepSearchResults(results);
    assertEqual(numResults, 2);
    assertTrue(formatted.includes("8:hit"), "same-file context group is not a file");
    assertFalse(formatted.includes("c.md"), "third file is outside the global limit");
    const items = await impl.grepSearchImpl({ query: "hit", splitByFile: true }, {
      ide: { getSearchResults: async () => results },
    });
    assertEqual(items.length, 2);
    assertEqual(items[0].uri.value, "a.md");
    assertTrue(items[0].content.includes("8:hit"));
  }
});

await test(suite + ": a limit of one keeps only the first complete file", () => {
  const raw = [".\\a.md", "1:hit", "--", "8:hit", "./b.md", "2:hit"].join("\n");
  assertEqual(limitGrepSearchResults(raw, 1), raw.slice(0, raw.indexOf("\n./b.md")));
});

summary(suite);
