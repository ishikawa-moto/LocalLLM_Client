import fs from "node:fs";
import path from "node:path";

// Pi tool hooks are a safety backstop. The Windows orchestrator decides which
// tools to enable and must still require approval for externally impactful work.
const FILE_TOOLS = new Set(["read", "grep", "find", "ls", "edit", "write"]);
const OBSERVATION_ID = /^obs_[a-f0-9]{24}$/;
const PRIVATE_NAME = /^(?:\.env(?:\..*)?|id_(?:rsa|ed25519)|.*\.(?:pfx|p12|key|pem))$/i;
const PROTECTED_SEGMENT = /^(?:\.git|\.localbrain|node_modules)$/i;

function isWithin(root, candidate) {
  const relative = path.relative(root, candidate);
  return relative === "" || (!relative.startsWith(".." + path.sep) && relative !== ".." &&
    !path.isAbsolute(relative));
}

function actualPath(candidate) {
  let existing = candidate;
  while (!fs.existsSync(existing)) {
    const parent = path.dirname(existing);
    if (parent === existing) throw new Error("No existing parent for tool path");
    existing = parent;
  }
  const real = fs.realpathSync.native(existing);
  return path.resolve(real, path.relative(existing, candidate));
}

export function checkToolCall(event, cwd) {
  if(event.toolName === "host_recall") {
    const input=event.input ?? {};
    return typeof input.evidenceHash === "string" && /^[a-f0-9]{64}$/.test(input.evidenceHash) &&
      (input.offset===undefined || Number.isSafeInteger(input.offset) && input.offset>=0)
      ? undefined : {block:true,reason:"Invalid canonical task evidence recall"};
  }
  if (event.toolName === "edit" || event.toolName === "write")
    return { block: true, reason: "Native mutation tools are disabled; use the Windows Host tools" };
  if (event.toolName === "host_edit" || event.toolName === "host_write")
    event = { ...event, toolName: event.toolName === "host_edit" ? "edit" : "write" };
  if (event.toolName === "obs_recall") {
    const input = event.input ?? {};
    if (typeof input.id !== "string" || !OBSERVATION_ID.test(input.id) ||
        (input.offset !== undefined &&
          (!Number.isSafeInteger(input.offset) || input.offset < 0)))
      return { block: true, reason: "Invalid ObservationPack recall request" };
    return undefined;
  }
  if (!FILE_TOOLS.has(event.toolName))
    return { block: true, reason: "This tool is not in the LocalBrain WSL policy" };
  const root = fs.realpathSync.native(cwd);
  const input = event.input ?? {};
  const rawPath = typeof input.path === "string" ? input.path : ".";
  if (rawPath.includes("\0")) return { block: true, reason: "Invalid tool path" };
  const resolved = path.resolve(root, rawPath);
  const actual = actualPath(resolved);
  if (!isWithin(root, resolved) || !isWithin(root, actual))
    return { block: true, reason: "Tool path must stay inside the registered Git workspace" };
  const relative = path.relative(root, resolved);
  const segments = relative.split(path.sep);
  if (segments.some(segment => PROTECTED_SEGMENT.test(segment)) ||
      segments.some(segment => PRIVATE_NAME.test(segment)))
    return { block: true, reason: "Protected path is not available to Pi" };
  if (event.toolName === "edit" || event.toolName === "write") {
    let allowed;
    try { allowed = JSON.parse(process.env.LOCALBRAIN_ALLOWED_FILES || "[]"); }
    catch { return { block: true, reason: "Mutation allowlist is invalid" }; }
    if (!Array.isArray(allowed) || !allowed.every(item => typeof item === "string"))
      return { block: true, reason: "Mutation allowlist is invalid" };
    const relativeFile = relative.split(path.sep).join("/").toLowerCase();
    if (!allowed.some(item => item.replaceAll("\\", "/").toLowerCase() === relativeFile))
      return { block: true, reason: "File is outside the task mutation allowlist" };
  }
  return undefined;
}

export default function (pi) {
  pi.on("tool_call", (event, ctx) => checkToolCall(event, ctx.cwd));
}
