// Modified for LocalBrain PC73; see NOTICE.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

function localPath(uri: string): string {
  const url = new URL(uri);
  if (url.protocol !== "file:" || url.host || url.search || url.hash) {
    throw new Error("Local file URI required");
  }
  const value = fileURLToPath(url);
  if (
    !path.isAbsolute(value) ||
    (process.platform === "win32" &&
      (!/^[a-z]:\\/i.test(value) || /[:*?<>|"]/.test(value.slice(2))))
  ) {
    throw new Error("Local file path required");
  }
  return value;
}

function statIfPresent(value: string) {
  try {
    return fs.lstatSync(value);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return undefined;
    throw error;
  }
}

function inside(root: string, target: string): boolean {
  const relative = path.relative(root, target);
  return relative !== ".." && !relative.startsWith(".." + path.sep) && !path.isAbsolute(relative);
}

function samePath(a: string, b: string): boolean {
  return path.relative(a, b) === "";
}

// Check the actual local path and all existing ancestors. Fail closed on
// unknown branches, Git metadata paths, and links. The caller checks again
// at each edit entry point; this is not an atomic filesystem/branch lock.
export function assertLocalBrainWritePathBoundary(
  uri: string,
  workspaceUris: readonly string[],
): void {
  try {
    const target = localPath(uri);
    const root = path.parse(target).root;
    const parts = target.slice(root.length).split(path.sep);
    let current = root;
    for (const [index, part] of parts.entries()) {
      if (!part || part.toLowerCase() === ".git" ||
          (process.platform === "win32" && (/[. ]$/.test(part) ||
            /^(?:con|prn|aux|nul|com[1-9¹²³]|lpt[1-9¹²³])(?:\.|$)/i.test(part)))) {
        throw new Error("Unsafe path component");
      }
      current = path.join(current, part);
      const stat = statIfPresent(current);
      if (!stat) continue;
      if (stat.isSymbolicLink() || !samePath(fs.realpathSync.native(current), current) ||
          (index === parts.length - 1 ? !stat.isFile() || stat.nlink !== 1 : !stat.isDirectory())) {
        throw new Error("Unsafe file or directory");
      }
    }

    const withinWorkspace = workspaceUris.some((workspace) => {
      try {
        return inside(localPath(workspace), target);
      } catch {
        return false;
      }
    });
    if (!withinWorkspace) throw new Error("File is outside the local workspace");

    let foundRepository = false;
    for (let dir = path.dirname(target); ; dir = path.dirname(dir)) {
      if (statIfPresent(path.join(dir, "HEAD")) && statIfPresent(path.join(dir, "objects"))?.isDirectory() && statIfPresent(path.join(dir, "refs"))?.isDirectory()) {
        throw new Error("Git metadata cannot be edited");
      }
      const marker = path.join(dir, ".git");
      const stat = statIfPresent(marker);
      if (stat) {
        if (stat.isSymbolicLink()) throw new Error("Linked Git metadata");
        const pointer = stat.isFile() ? /^gitdir: ([^\r\n]+)$/.exec(fs.readFileSync(marker, "utf8").trimEnd()) : null;
        const gitDir = stat.isDirectory() ? marker : pointer && path.resolve(dir, pointer[1]);
        if (!gitDir) throw new Error("Unknown Git metadata");
        const commonMarker = path.join(gitDir, "commondir");
        const commonDir = statIfPresent(commonMarker)
          ? path.resolve(gitDir, fs.readFileSync(commonMarker, "utf8").trimEnd())
          : gitDir;
        if ([gitDir, commonDir].some((metadata) => inside(fs.realpathSync.native(metadata), target))) {
          throw new Error("Git metadata cannot be edited");
        }
        const head = /^ref: refs\/heads\/([^\r\n]+)$/.exec(
          fs.readFileSync(path.join(gitDir, "HEAD"), "utf8").trimEnd(),
        );
        if (!head || /^main$/i.test(head[1])) {
          throw new Error("A branch other than main is required");
        }
        foundRepository = true;
      }
      if (dir === path.dirname(dir)) break;
    }
    if (!foundRepository) throw new Error("No Git checkout found");
  } catch {
    throw new Error("Blocked by LocalBrain execution boundary: editing requires a local workspace file on a branch other than main.");
  }
}
