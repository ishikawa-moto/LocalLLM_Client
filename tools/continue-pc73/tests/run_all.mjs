// Runs all test suites; prints a combined summary.
import { spawnSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const suites = [
  "test_grep_search.test.mjs",
  "test_approval_policy.test.mjs",
  "test_windows_tool_paths.test.mjs",
  "test_tool_context_budget.test.mjs",
  "test_boundary.test.mjs",
];

let totalPass = 0;
let totalFail = 0;
let failedSuites = 0;

for (const suite of suites) {
  console.log(`\n########## ${suite} ##########`);
  const result = spawnSync(process.execPath, [path.join(here, suite)], {
    encoding: "utf8",
    stdio: "pipe",
  });
  process.stdout.write(result.stdout ?? "");
  if (result.stderr) process.stderr.write(result.stderr);
  const m = /(\d+) passed, (\d+) failed/.exec(result.stdout ?? "");
  if (m) {
    totalPass += Number(m[1]);
    totalFail += Number(m[2]);
  }
  if (result.status !== 0) failedSuites++;
}

console.log("\n========== TOTAL ==========");
console.log(`${totalPass} passed, ${totalFail} failed, ${failedSuites} failed suite(s)`);
process.exit(totalFail > 0 || failedSuites > 0 ? 1 : 0);
