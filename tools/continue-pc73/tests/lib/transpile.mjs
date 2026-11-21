// Transpile helper: TypeScript -> CommonJS via the pinned TypeScript 5.4.5

import { createRequire } from "node:module";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const here = path.dirname(fileURLToPath(import.meta.url));
const ts = require("typescript");

export const WORKTREE_ROOT = path.resolve(here, "..", "..");
export const PATCHED_ROOT = path.join(WORKTREE_ROOT, "src", "patched");
export const BUILD_ROOT = path.join(here, ".build");

/**
 * Transpiles a TypeScript file to CommonJS JavaScript at the mirrored path
 * under tests/.build.
 */
export function transpile(relPathFromPatched) {
  const srcPath = path.join(PATCHED_ROOT, relPathFromPatched);
  const outPath = path.join(BUILD_ROOT, relPathFromPatched.replace(/\.ts$/, ".js"));
  fs.mkdirSync(path.dirname(outPath), { recursive: true });
  const source = fs.readFileSync(srcPath, "utf8");
  const result = ts.transpileModule(source, {
    compilerOptions: {
      module: ts.ModuleKind.CommonJS,
      target: ts.ScriptTarget.ES2020,
      esModuleInterop: true,
    },
    fileName: relPathFromPatched,
  });
  fs.writeFileSync(outPath, result.outputText);
  return outPath;
}

/** Transpiles and requires a patched TypeScript module. */
export function loadPatchedModule(relPathFromPatched) {
  return require(transpile(relPathFromPatched));
}

/** Strips `import ...` lines from TypeScript source text. */
export function stripImports(source) {
  return source
    .split("\n")
    .filter((line) => !/^\s*import\b/.test(line))
    .join("\n");
}

/** Transpiles a synthetic TypeScript text to a JS file under tests/.build. */
export function transpileText(text, outRelPath) {
  const outPath = path.join(BUILD_ROOT, outRelPath.replace(/\.ts$/, ".js"));
  fs.mkdirSync(path.dirname(outPath), { recursive: true });
  const result = ts.transpileModule(text, {
    compilerOptions: {
      module: ts.ModuleKind.CommonJS,
      target: ts.ScriptTarget.ES2020,
      esModuleInterop: true,
    },
    fileName: outRelPath,
  });
  fs.writeFileSync(outPath, result.outputText);
  return outPath;
}
