// Modified for LocalBrain PC73; see NOTICE.
import { realpath } from "node:fs/promises";
import { posix } from "node:path";
import { fileURLToPath } from "node:url";

export async function resolveLocalFileUri(uri: string): Promise<string | undefined> {
  try {
    const url = new URL(uri);
    if (url.protocol !== "file:" || url.search || url.hash) return undefined;
    return (await realpath(fileURLToPath(url))).replaceAll("\\", "/");
  } catch {
    return undefined;
  }
}

export async function isWindowsWorkspaceFile(
  uri: string,
  workspaceDirs: string[],
): Promise<boolean> {
  // Compare the actual on-disk paths so drive spelling and URI encoding do not
  // affect approval, and links that leave the workspace still require consent.
  const target = await resolveLocalFileUri(uri);
  if (!target) return false;

  for (const workspaceDir of workspaceDirs) {
    const root = await resolveLocalFileUri(workspaceDir);
    if (!root) continue;
    const relative = posix.relative(root, target);
    if (relative !== ".." && !relative.startsWith("../") && !posix.isAbsolute(relative)) {
      return true;
    }
  }
  return false;
}
