import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { openAICompletionsApi } from "@earendil-works/pi-ai/compat";
import { accountContext } from "./context-telemetry.mjs";
import { requestHost } from "./host-request.mjs";

const RELAY_URL = "http://localbrain.invalid/v1/chat/completions";
const MAX_REQUEST_BYTES = 8 * 1024 * 1024;
const MAX_RESPONSE_BYTES = 48 * 1024 * 1024;
const CONTEXT_SETTINGS = path.join(process.cwd(), ".localbrain", "contextTelemetry.json");
const CONTEXT_CURRENT = path.join(process.cwd(), ".localbrain", "context-current.json");
const CONTEXT_HISTORY = path.join(process.cwd(), ".localbrain", "context-telemetry.jsonl");
const DEFAULT_CONTEXT_SETTINGS = Object.freeze({ enabled: true, persist: true,
  layaEnabled: true, autoActionsEnabled: false });
let tokenizerProcess;
let tokenizerBuffer = "";
let tokenizerNextId = 0;
let contextCheck = 0;
const tokenizerPending = new Map();

function contextSettings() {
  try {
    if (!fs.existsSync(CONTEXT_SETTINGS)) return DEFAULT_CONTEXT_SETTINGS;
    if (fs.lstatSync(CONTEXT_SETTINGS).isSymbolicLink()) throw new Error("linked_settings");
    const values = JSON.parse(fs.readFileSync(CONTEXT_SETTINGS, "utf8"));
    const source = values.contextTelemetry ?? values;
    return Object.fromEntries(Object.keys(DEFAULT_CONTEXT_SETTINGS).map(key =>
      [key, typeof source[key] === "boolean" ? source[key] : DEFAULT_CONTEXT_SETTINGS[key]]));
  } catch { return { ...DEFAULT_CONTEXT_SETTINGS, enabled: false }; }
}

function countWithQwenTokenizer(texts, payload) {
  const tokenizer = process.env.LOCALBRAIN_CONTEXT_TOKENIZER_JSON;
  if (!tokenizer || !path.isAbsolute(tokenizer) || !fs.existsSync(tokenizer))
    return Promise.reject(new Error("verified_tokenizer_unavailable"));
  if (!tokenizerProcess || tokenizerProcess.exitCode !== null) {
    const script = fileURLToPath(new URL("./context-tokenizer.py", import.meta.url));
    tokenizerProcess = spawn("/home/worker/localbrain-v2/laya-venv/bin/python",
      ["-u", script, tokenizer], { stdio: ["pipe", "pipe", "ignore"] });
    tokenizerProcess.stdout.setEncoding("utf8");
    tokenizerProcess.stdout.on("data", chunk => {
      tokenizerBuffer += chunk;
      let end;
      while ((end = tokenizerBuffer.indexOf("\n")) >= 0) {
        const line = tokenizerBuffer.slice(0, end);
        tokenizerBuffer = tokenizerBuffer.slice(end + 1);
        try {
          const reply = JSON.parse(line);
          const pending = tokenizerPending.get(reply.id);
          if (!pending) continue;
          tokenizerPending.delete(reply.id);
          reply.error ? pending.reject(new Error(reply.error)) : pending.resolve({
            counts: reply.counts, promptTokens: reply.prompt_tokens });
        } catch { /* Numeric-only tokenizer protocol error is handled by timeout. */ }
      }
    });
    tokenizerProcess.on("exit", () => {
      for (const pending of tokenizerPending.values()) pending.reject(new Error("tokenizer_exited"));
      tokenizerPending.clear();
      tokenizerProcess = undefined;
    });
    tokenizerProcess.on("error", () => {
      for (const pending of tokenizerPending.values()) pending.reject(new Error("tokenizer_start_failed"));
      tokenizerPending.clear();
      tokenizerProcess = undefined;
    });
  }
  const id = ++tokenizerNextId;
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      tokenizerPending.delete(id);
      reject(new Error("tokenizer_timeout"));
      tokenizerProcess?.kill();
    }, 15000);
    tokenizerPending.set(id, {
      resolve: value => { clearTimeout(timeout); resolve(value); },
      reject: error => { clearTimeout(timeout); reject(error); },
    });
    tokenizerProcess.stdin.write(JSON.stringify({ id, texts, messages: payload.messages,
      tools: payload.tools ?? [], chat_template_kwargs: payload.chat_template_kwargs ?? {} }) + "\n", error => {
      if (error && tokenizerPending.has(id)) {
        tokenizerPending.delete(id);
        clearTimeout(timeout);
        reject(new Error("tokenizer_write_failed"));
      }
    });
  });
}

function writeContextSnapshot(snapshot, persist) {
  const directory = path.dirname(CONTEXT_CURRENT);
  if (!fs.existsSync(directory) || fs.lstatSync(directory).isSymbolicLink()) return;
  for (const output of [CONTEXT_CURRENT, CONTEXT_HISTORY]) {
    try { if (fs.lstatSync(output).isSymbolicLink()) return; }
    catch (error) { if (error?.code !== "ENOENT") return; }
  }
  let previous;
  try {
    if (fs.statSync(CONTEXT_CURRENT).size <= 4096)
      previous = JSON.parse(fs.readFileSync(CONTEXT_CURRENT, "utf8"));
  } catch { /* First snapshot or a damaged optional metric. */ }
  const sameTask = previous?.task_id === (process.env.LOCALBRAIN_CONTEXT_TASK_ID ?? null) &&
    previous?.context_metrics_available === true && snapshot.context_metrics_available === true;
  const line = JSON.stringify({ timestamp: new Date().toISOString(),
    task_id: process.env.LOCALBRAIN_CONTEXT_TASK_ID ?? null,
    phase: "preflight", agent_phase: process.env.LOCALBRAIN_CONTEXT_AGENT_PHASE ?? "unknown",
    sequence: ++contextCheck, ...snapshot,
    context_growth_since_last_check: sameTask ?
      snapshot.context_usage - previous.context_usage : null,
    tool_result_growth: sameTask ?
      snapshot.tokens.tool_result - previous.tokens.tool_result : null,
    conversation_growth: sameTask ?
      snapshot.tokens.conversation - previous.tokens.conversation : null,
    retrieval_growth: sameTask ?
      snapshot.tokens.retrieval - previous.tokens.retrieval : null });
  if (line.length > 4096) return;
  fs.writeFileSync(CONTEXT_CURRENT, line + "\n", { mode: 0o600 });
  if (persist) {
    fs.appendFileSync(CONTEXT_HISTORY, line + "\n", { mode: 0o600 });
    if (fs.statSync(CONTEXT_HISTORY).size > 2_000_000) {
      const recent = fs.readFileSync(CONTEXT_HISTORY, "utf8").slice(-1_000_000);
      fs.writeFileSync(CONTEXT_HISTORY, recent.slice(recent.indexOf("\n") + 1),
        { mode: 0o600 });
    }
  }
}

async function captureContext(context, payload) {
  const settings = contextSettings();
  if (!settings.enabled && !process.env.LOCALBRAIN_SIDEFX_PIPE) return;
  const start = performance.now();
  try {
    const snapshot = await accountContext(context, payload, countWithQwenTokenizer);
    if (!process.env.LOCALBRAIN_SIDEFX_PIPE) writeContextSnapshot({ ...snapshot, measurement_ms: Math.round(performance.now() - start),
      laya_enabled: settings.layaEnabled, auto_actions_enabled: settings.autoActionsEnabled },
      settings.persist);
    return snapshot;
  } catch (error) {
    // Model execution must continue. No prompt, tool output or exception text is logged.
    try { if(!process.env.LOCALBRAIN_SIDEFX_PIPE) writeContextSnapshot({ context_metrics_available: false,
      error_type: error instanceof Error ? error.constructor.name : "UnknownError" },
      settings.persist); } catch { /* Telemetry is optional. */ }
  }
}

function withContextTelemetry(stream, selected, context, options) {
  const upstream = options.onPayload;
  return stream(selected, context, { ...options,
    onPayload: async (payload, model) => {
      const replacement = await upstream?.(payload, model);
      const snapshot = await captureContext(context, replacement ?? payload);
      if (process.env.LOCALBRAIN_SIDEFX_PIPE) {
        if (!snapshot) throw new Error("ContextMeasurementUnavailable");
        const decision = await requestHost("preflight", {
          payloadTokens: snapshot.estimated_prompt_tokens, contextLimit: snapshot.context_limit,
          payloadJson: JSON.stringify(replacement ?? payload), snapshotJson: JSON.stringify(snapshot)
        });
        if (!decision.ok) throw new Error(decision.handoff ? "LocalBrainContextHandoffRequired" : decision.status);
      }
      return replacement;
    },
    fetch: relayFetch });
}

async function relayFetch(input, init = {}) {
  const request = new Request(input, init);
  if (request.method !== "POST" || request.url !== RELAY_URL) {
    throw new Error("LocalBrain provider rejected an unexpected endpoint");
  }
  const body = Buffer.from(await request.arrayBuffer());
  if (body.length > MAX_REQUEST_BYTES) throw new Error("LocalBrain request exceeds limit");
  const relay = process.env.LOCALBRAIN_RELAY_EXE;
  if (!relay || !/^\/mnt\/[a-z]\/.*\/localbrain\.exe$/i.test(relay)) {
    throw new Error("Windows LocalBrain relay path is not configured");
  }
  const payload = JSON.stringify({
    method: "POST",
    path: "/v1/chat/completions",
    contentType: request.headers.get("content-type") || "application/json",
    bodyBase64: body.toString("base64"),
  }) + "\n";
  const child = spawn(relay, ["model-relay"], { stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
  const chunks = [];
  let size = 0;
  let stderrLines = 0;
  const timer = setTimeout(() => child.kill(), 10 * 60 * 1000);
  const abort = () => child.kill();
  request.signal?.addEventListener("abort", abort, { once: true });
  try {
    child.stdin.end(payload);
    child.stdout.on("data", chunk => {
      size += chunk.length;
      if (size > MAX_RESPONSE_BYTES) child.kill();
      else chunks.push(chunk);
    });
    child.stderr.on("data", chunk => { stderrLines += chunk.toString().split("\n").length - 1; });
    const code = await new Promise((resolve, reject) => {
      child.once("error", reject);
      child.once("close", resolve);
    });
    if (size > MAX_RESPONSE_BYTES) throw new Error("LocalBrain response exceeds limit");
    const result = JSON.parse(Buffer.concat(chunks).toString("utf8"));
    if (code !== 0 || result.error) {
      throw new Error(`Windows model relay failed (${result.cause || "process"})`);
    }
    if (!Number.isInteger(result.status) || result.status < 200 || result.status > 599) {
      throw new Error("Windows model relay returned an invalid status");
    }
    return new Response(Buffer.from(result.body_base64, "base64"), {
      status: result.status,
      headers: { "content-type": result.content_type || "application/json" },
    });
  } finally {
    clearTimeout(timer);
    request.signal?.removeEventListener("abort", abort);
    if (!child.killed && child.exitCode === null) child.kill();
    void stderrLines; // Never log stderr or provider content.
  }
}

export default function (pi) {
  const api = openAICompletionsApi();
  const model = {
    id: "local-qwen38",
    name: "LocalBrain Qwen 27B via Windows Bridge",
    provider: "localbrain",
    api: "openai-completions",
    baseUrl: "http://localbrain.invalid/v1",
    reasoning: false,
    input: ["text"],
    cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
    contextWindow: 32768,
    maxTokens: 2048,
  };
  pi.registerProvider({
    id: "localbrain",
    name: "LocalBrain Windows Bridge",
    baseUrl: model.baseUrl,
    auth: {
      apiKey: {
        name: "LocalBrain loopback placeholder",
        async check() { return { type: "api_key", source: "Windows loopback Bridge" }; },
        async resolve() { return { auth: { apiKey: "local-loopback-only" }, source: "Windows loopback Bridge" }; },
      },
    },
    getModels() { return [model]; },
    stream(selected, context, options) {
      return withContextTelemetry(api.stream, selected, context, options ?? {});
    },
    streamSimple(selected, context, options) {
      return withContextTelemetry(api.streamSimple, selected, context, options ?? {});
    },
  });
}
