// Tests for the Automatic-approval fix in packages/terminal-security:
//   - Automatic (allowedWithoutPermission) is a pre-approval; command
//     classification must not downgrade it to Ask First.
//   - Execution restrictions are enforced separately at the terminal entry point.
//   - Ask First and Excluded behaviors are unchanged.
import { loadPatchedModule } from "./lib/transpile.mjs";
import { test, assertEqual, summary } from "./lib/tinytest.mjs";

const suite = "approval_policy";

const { evaluateTerminalCommandSecurity } = loadPatchedModule(
  "packages/terminal-security/src/evaluateTerminalCommandSecurity.ts",
);

const AUTOMATIC = "allowedWithoutPermission";
const ASK_FIRST = "allowedWithPermission";
const EXCLUDED = "disabled";

// ---------------------------------------------------------------------------
// Automatic: no classification-based downgrade (requirement of the fix)
// ---------------------------------------------------------------------------
await test(`${suite}: Automatic Get-Location -> no dialog`, () => {
  assertEqual(evaluateTerminalCommandSecurity(AUTOMATIC, "Get-Location"), AUTOMATIC);
});
await test(`${suite}: Automatic git status -> no dialog`, () => {
  assertEqual(evaluateTerminalCommandSecurity(AUTOMATIC, "git status"), AUTOMATIC);
});
await test(`${suite}: Automatic git grep -> no dialog`, () => {
  assertEqual(
    evaluateTerminalCommandSecurity(AUTOMATIC, 'git grep -n "Setup-Client"'),
    AUTOMATIC,
  );
});
await test(`${suite}: Automatic build commands -> no dialog`, () => {
  assertEqual(evaluateTerminalCommandSecurity(AUTOMATIC, "npm run build"), AUTOMATIC);
  assertEqual(evaluateTerminalCommandSecurity(AUTOMATIC, "dotnet build"), AUTOMATIC);
  assertEqual(
    evaluateTerminalCommandSecurity(AUTOMATIC, "node build/verify.js"),
    AUTOMATIC,
  );
});
await test(`${suite}: Automatic mutating command -> still pre-approved (spec)`, () => {
  // The approval policy must not reintroduce a dialog; mutation restrictions
  // are enforced by the execution boundary instead.
  assertEqual(
    evaluateTerminalCommandSecurity(AUTOMATIC, "Remove-Item tmp.txt"),
    AUTOMATIC,
  );
});
await test(`${suite}: Automatic command substitution -> no dialog (spec)`, () => {
  assertEqual(evaluateTerminalCommandSecurity(AUTOMATIC, "echo $(whoami)"), AUTOMATIC);
});
await test(`${suite}: Automatic multi-line -> no dialog`, () => {
  assertEqual(
    evaluateTerminalCommandSecurity(AUTOMATIC, "Get-Location\ngit status"),
    AUTOMATIC,
  );
});

// ---------------------------------------------------------------------------
// Automatic: classification does not decide execution
// ---------------------------------------------------------------------------
await test(`${suite}: Automatic classification does not change the policy`, () => {
  assertEqual(evaluateTerminalCommandSecurity(AUTOMATIC, "rm -rf /"), AUTOMATIC);
  assertEqual(evaluateTerminalCommandSecurity(AUTOMATIC, "sudo rm -rf /etc"), AUTOMATIC);
  assertEqual(
    evaluateTerminalCommandSecurity(AUTOMATIC, "del /s /q C:\\Windows"),
    AUTOMATIC,
  );
  assertEqual(evaluateTerminalCommandSecurity(AUTOMATIC, "format"), AUTOMATIC);
  assertEqual(evaluateTerminalCommandSecurity(AUTOMATIC, "eval 'rm -rf /'"), AUTOMATIC);
});

// ---------------------------------------------------------------------------
// Ask First: unchanged (asks for everything non-critical)
// ---------------------------------------------------------------------------
await test(`${suite}: Ask First requires approval (unchanged)`, () => {
  assertEqual(evaluateTerminalCommandSecurity(ASK_FIRST, "Get-Location"), ASK_FIRST);
  assertEqual(evaluateTerminalCommandSecurity(ASK_FIRST, "git status"), ASK_FIRST);
  assertEqual(evaluateTerminalCommandSecurity(ASK_FIRST, "git grep x"), ASK_FIRST);
  assertEqual(evaluateTerminalCommandSecurity(ASK_FIRST, "Remove-Item a.txt"), ASK_FIRST);
  assertEqual(evaluateTerminalCommandSecurity(ASK_FIRST, "echo $(whoami)"), ASK_FIRST);
});
await test(`${suite}: Ask First critical -> disabled (unchanged)`, () => {
  assertEqual(evaluateTerminalCommandSecurity(ASK_FIRST, "rm -rf /"), EXCLUDED);
});

// ---------------------------------------------------------------------------
// Excluded: rejected (unchanged)
// ---------------------------------------------------------------------------
await test(`${suite}: Excluded is rejected (unchanged)`, () => {
  assertEqual(evaluateTerminalCommandSecurity(EXCLUDED, "Get-Location"), EXCLUDED);
  assertEqual(evaluateTerminalCommandSecurity(EXCLUDED, "git status"), EXCLUDED);
  assertEqual(evaluateTerminalCommandSecurity(EXCLUDED, "rm -rf /"), EXCLUDED);
});

summary(suite);
