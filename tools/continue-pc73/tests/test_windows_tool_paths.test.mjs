import { createRequire } from "node:module";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import * as url from "node:url";
import vm from "node:vm";
import { assertEqual, summary, test } from "./lib/tinytest.mjs";

const suite = "windows_tool_paths";
const stage = process.env.PC73_STAGE_DIR;
if (process.platform === "win32" && stage) {
  const bundle = fs.readFileSync(path.join(stage, "extension/out/extension.js"), "utf8");
  const start = bundle.indexOf("// ../../core/util/pathResolver.ts\n");
  const resolverEnd = bundle.indexOf("var import_node_url3, path11;", start);
  const policyStart = bundle.indexOf("function evaluateFileAccessPolicy(", resolverEnd);
  const policyEnd = bundle.indexOf("var init_fileAccess", policyStart);
  const { resolveInputPath, resolveLocalFileUri, evaluateFileAccessPolicy, isPc73FileAccessDenied } = vm.runInNewContext(
    bundle.slice(start, resolverEnd) + bundle.slice(policyStart, policyEnd) +
      "\n({ resolveInputPath, resolveLocalFileUri, evaluateFileAccessPolicy, isPc73FileAccessDenied })",
    {
      process,
      URL,
      require: createRequire(import.meta.url),
      import_node_url3: url,
      path11: path,
      untildify: (value) => value,
      resolveRelativePathInDir: async (value, ide) => {
        const target = url.pathToFileURL(path.join(url.fileURLToPath((await ide.getWorkspaceDirs())[0]), value)).href;
        return await ide.fileExists(target) ? target : undefined;
      },
    },
  );

  const fixture = fs.mkdtempSync(path.join(os.tmpdir(), "pc73-tool-paths-"));
  const workspace = path.join(fixture, "Workspace");
  const outside = path.join(fixture, "Workspace-outside");
  try {
    fs.mkdirSync(workspace);
    fs.mkdirSync(outside);
    const localFile = path.join(workspace, "試験 file.txt");
    const outsideFile = path.join(outside, "test.txt");
    fs.writeFileSync(localFile, "fixture");
    fs.writeFileSync(outsideFile, "fixture");
    const link = path.join(workspace, "link");
    fs.symlinkSync(outside, link, "junction");
    const workspaceUri = url.pathToFileURL(workspace).href.replace(
      /^file:\/\/\/([a-z]):/i, (_, letter) => `file:///${letter.toLowerCase()}%3A`,
    );
    const ide = {
      getWorkspaceDirs: async () => [workspaceUri],
      fileExists: async (uri) => fs.existsSync(url.fileURLToPath(uri)),
    };
    const upperDrive = (value) => value.replace(/^[a-z]:/i, (drive) => drive.toUpperCase());
    const automatic = "allowedWithoutPermission";
    const askFirst = "allowedWithPermission";

    await test("Windows ls/read_file keep Automatic for a file or directory in the workspace", async () => {
      for (const input of [upperDrive(workspace), upperDrive(localFile), url.pathToFileURL(localFile).href, path.basename(localFile)]) {
        const resolved = await resolveInputPath(ide, input);
        assertEqual(resolved.isWithinWorkspace, true);
        assertEqual(evaluateFileAccessPolicy(automatic, resolved.isWithinWorkspace), automatic);
        assertEqual(evaluateFileAccessPolicy(askFirst, resolved.isWithinWorkspace), askFirst);
        assertEqual(evaluateFileAccessPolicy("disabled", resolved.isWithinWorkspace), "disabled");
      }
    });

    await test("Windows paths do not change the selected approval policy", async () => {
      for (const input of [outsideFile, path.join(link, "test.txt"), "link/test.txt", path.join(workspace, "missing")]) {
        const resolved = await resolveInputPath(ide, input);
        assertEqual(resolved.isWithinWorkspace, false);
        assertEqual(evaluateFileAccessPolicy(automatic, resolved.isWithinWorkspace), automatic);
        assertEqual(evaluateFileAccessPolicy(askFirst, resolved.isWithinWorkspace), askFirst);
        assertEqual(evaluateFileAccessPolicy("disabled", resolved.isWithinWorkspace), "disabled");
      }
    });

    await test("a junction resolves to the actual target and real access denials stay distinct", async () => {
      const aliased = await resolveLocalFileUri(url.pathToFileURL(path.join(link, "test.txt")).href);
      assertEqual(aliased, await resolveLocalFileUri(url.pathToFileURL(outsideFile).href));
      for (const code of ["NoPermissions", "EACCES", "EPERM"]) {
        assertEqual(isPc73FileAccessDenied({ code }), true);
      }
      assertEqual(isPc73FileAccessDenied({ code: "FileNotFound" }), false);
    });
  } finally {
    fs.rmSync(fixture, { recursive: true });
  }
} else {
  console.log("  SKIP Windows VSIX stage required");
}

summary(suite);
