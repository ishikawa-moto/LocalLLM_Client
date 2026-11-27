import { spawnSync } from "node:child_process";
import { createRequire } from "node:module";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import vm from "node:vm";
import { assertEqual, assertTrue, summary, test } from "./lib/tinytest.mjs";

const stage = process.env.PC73_STAGE_DIR;
const bundle = fs.readFileSync(path.join(stage, "extension/out/extension.js"), "utf8");
const start = bundle.indexOf("const pc73ToolBudget = ");
const end = bundle.indexOf("var LlamaEncoding, NonWorkerAsyncEncoder, ", start);
const logs = [];
const count = (text) => Math.ceil(Buffer.byteLength(text ?? "") / 2);
const { pc73ToolBudget, compileChatMessages } = vm.runInNewContext(
  bundle.slice(start, end) + "\n({ pc73ToolBudget, compileChatMessages })",
  {
    require: createRequire(import.meta.url), Buffer,
    console: { info: (tag, data) => logs.push(JSON.parse(data)) },
    countTokens: count,
    countToolsTokens: () => 350,
    countChatMessageTokens: (_, msg) => 4 + count(msg.content) + (msg.toolCalls ? count(JSON.stringify(msg.toolCalls)) : 0),
    chatMessageIsEmpty: (msg) => !msg.content && !msg.toolCalls?.length,
    addSpaceToAnyEmptyMessages: (msgs) => msgs,
    getTokenCountingBufferSafety: () => 1000,
    MIN_RESPONSE_TOKENS: 512,
    extractToolSequence(messages) {
      const latest = [messages.pop()];
      while (latest[0]?.role === "tool") latest.unshift(messages.pop());
      return latest;
    },
  },
);

const terminalStart = bundle.indexOf('case "../../util/terminalOutput": return ');
const terminalEnd = bundle.indexOf('\ncase "../../util/errors":', terminalStart);
const terminalCode = bundle.slice(terminalStart, terminalEnd).replace('case "../../util/terminalOutput": return ', "");
const { createTerminalOutput, windowsPowerShellArguments } = vm.runInNewContext(terminalCode.replace(/;\s*$/, ""), {
  require: createRequire(import.meta.url), pc73ToolBudget,
});
const readStart = bundle.indexOf("const { readUnchangedText } = ");
const readEnd = bundle.indexOf("var readFileImpl;", readStart);
const { readUnchangedText } = vm.runInNewContext(bundle.slice(readStart, readEnd) + "\n({ readUnchangedText })", {
  require: createRequire(import.meta.url), Buffer,
});

const row = "1,日本語のサンプル,".repeat(5);
const hugeCsv = Array(6350).fill(row).join("\r\n");
const turn = (id, output, name = "read_file") => [
  { role: "assistant", content: "", toolCalls: [{ id, function: { name, arguments: '{"filepath":"sample.csv"}' } }] },
  { role: "tool", toolCallId: id, content: output },
];
const user = { role: "user", content: "Inspect synthetic CSV only." };
const compile = (msgs, knownContextLength = 65536) => compileChatMessages({
  modelName: "local-qwen38", knownContextLength, maxTokens: 4096, msgs, supportsImages: true, tools: [],
});

await test("tool results respect byte, line and token caps with CRLF, LF and a huge single line", () => {
  for (const output of [hugeCsv, hugeCsv.replaceAll("\r\n", "\n"), "😀日本語".repeat(100_000)]) {
    const result = pc73ToolBudget.limitToolItems([{ content: output }], 2048, count)[0].content;
    assertTrue(result.includes("Tool output shortened"));
    assertTrue(Buffer.byteLength(result) <= 8192);
    assertTrue(result.split(/\r\n|\n/).length <= 160);
    assertTrue(count(result) <= 2048);
    assertTrue(!result.includes("�"));
  }
  const many = pc73ToolBudget.limitToolItems(Array.from({ length: 500 }, () => ({ content: "a" })), 2048, count);
  assertTrue(many.length <= 32);
  assertTrue(many.at(-1).content.includes("Tool output shortened"));
});

await test("latest legacy oversized result and grouped tool calls fit an adaptive budget", () => {
  const input = [user, ...turn("large", hugeCsv, "run_terminal_command")];
  for (const contextLength of [32768, 65536]) {
    const result = compile(input, contextLength).compiledChatMessages;
    assertEqual(result.at(-1).toolCallId, "large");
    assertTrue(result.at(-1).content.includes("Tool output shortened"));
    assertTrue(count(result.at(-1).content) <= 2048);
  }
  assertEqual(input.at(-1).content.length, hugeCsv.length, "stored source history must be unchanged");
  const grouped = [user, ...turn("small", hugeCsv, "ls")];
  grouped[1].toolCalls.push({ id: "second" });
  grouped.push({ role: "tool", toolCallId: "second", content: hugeCsv });
  const small = compile(grouped, 7000).compiledChatMessages.filter((message) => message.role === "tool");
  assertEqual(small.map((message) => message.toolCallId), ["small", "second"]);
  assertTrue(small.reduce((tokens, message) => tokens + count(message.content), 0) < 1300);
});

await test("unchanged reads are deduplicated, edits remain visible and history compacts before overflow", () => {
  const reads = [user, ...turn("one", "before edit"), ...turn("two", "before edit"), ...turn("three", "after edit")];
  const result = compile(reads).compiledChatMessages.filter((message) => message.role === "tool");
  assertTrue(result[0].content.includes("Identical read result"));
  assertEqual(result.slice(1).map((message) => message.content), ["before edit", "after edit"]);
  const long = [user];
  for (let index = 0; index < 35; index++) long.push(...turn(`old${index}`, `${index}:` + row.repeat(30)));
  long.push(...turn("latest", "completed"));
  const compacted = compile(long);
  assertTrue(compacted.didPrune);
  assertEqual(compacted.compiledChatMessages.at(-1).toolCallId, "latest");
  assertTrue(compacted.compiledChatMessages.some((message) => message.role === "user"));
  const metrics = logs.at(-1);
  assertEqual(metrics.planningContext, 32768);
  assertTrue(metrics.historyTokensBefore > metrics.historyTokensAfter);
  assertTrue(metrics.inputTokens < 22000);
  assertTrue(Object.values(metrics).every(Number.isFinite));
});

await test("bounded repeat read reuses the same version and refreshes when a file is edited or reopened", async () => {
  let readCount = 0;
  let revision = "1";
  let source = {};
  const ide = {
    getToolReadVersion: async () => ({ source, revision }),
    readFile: async () => `read ${++readCount}`,
  };
  assertEqual(await readUnchangedText(ide, "file:///test"), "read 1");
  assertEqual(await readUnchangedText(ide, "file:///test"), "read 1");
  revision = "2";
  assertEqual(await readUnchangedText(ide, "file:///test"), "read 2");
  source = {};
  assertEqual(await readUnchangedText(ide, "file:///test"), "read 3");
});

await test("terminal capture keeps UTF-8 across stream chunks and bounds stdout and stderr", () => {
  const capture = createTerminalOutput();
  const japanese = Buffer.from("日本語😀");
  for (const byte of japanese) capture.stdout.write(Buffer.from([byte]));
  capture.stderr.write(Buffer.from("stderr 日本語"));
  capture.stdout.write(Buffer.from("\n" + hugeCsv));
  capture.end();
  const preview = capture.preview();
  assertTrue(preview.includes("[stdout]\n日本語😀\n"));
  assertTrue(preview.includes("stderr 日本語"));
  assertTrue(preview.includes("Terminal output shortened"));
  assertTrue(Buffer.byteLength(preview) < 8192);
});

await test("the existing PowerShell -Command path preserves UTF-8 BOM, newlines and quotes", () => {
  if (process.platform !== "win32") return;
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "pc73-budget-test-"));
  const file = path.join(dir, "test.csv");
  try {
    for (const newline of ["\r\n", "\n"]) {
      fs.writeFileSync(file, "\ufeff" + Array(6350).fill("1,日本語").join(newline));
      const command = [
        "$raw = Get-Content -LiteralPath test.csv -Raw -Encoding UTF8",
        "$first = ($raw -split '\\r?\\n' | Select-String '日本語' | Select-Object -First 15).Count",
        'Write-Output "rows=$($first) quoted=`"日本語`""',
      ].join(newline);
      const result = spawnSync("powershell.exe", windowsPowerShellArguments(command), { cwd: dir, encoding: "utf8", windowsHide: true });
      assertEqual(result.status, 0);
      assertEqual(result.stdout.trim(), 'rows=15 quoted="日本語"');
    }
    const advanced = "param([string]$Mode = 'literal'); Write-Output $Mode";
    assertEqual(windowsPowerShellArguments(advanced).at(-1), advanced);
  } finally {
    fs.unlinkSync(file);
    fs.rmdirSync(dir);
  }
});

summary("tool_context_budget");
