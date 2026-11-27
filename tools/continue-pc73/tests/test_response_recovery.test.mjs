import { createRequire } from "node:module";
import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { assertEqual, assertTrue, summary, test } from "./lib/tinytest.mjs";

const stage = path.join(process.env.PC73_STAGE_DIR, "extension");
const bundle = fs.readFileSync(path.join(stage, "out/extension.js"), "utf8");
const start = bundle.indexOf("const pc73ResponseRecovery = ");
const end = bundle.indexOf("async function* llmStreamChat(", start);
const events = [];
const { recoverLocalResponse, superviseLocalResponse } = vm.runInNewContext(
  bundle.slice(start, end) + "\npc73ResponseRecovery",
  { require: createRequire(import.meta.url), URL, AbortController, AbortSignal, console: { info: (_, data) => events.push(JSON.parse(data)) } },
);
const read = { function: { name: "read_file", parameters: { required: ["filepath"], properties: { filepath: { type: "string" } } } } };
const options = { tools: [read] };
const source = [{ role: "system", content: "Keep existing constraints." }, { role: "user", content: "Read D:/synthetic only. Do not write." }];
const roots = ["file:///d%3A/synthetic"];
const text = (content) => ({ role: "assistant", content });
const tool = (...toolCalls) => ({ role: "assistant", content: "", toolCalls });

async function run(first, second, previousSideEffects = false) {
  const request = [];
  const lifecycle = [];
  async function* start(messages, signal, settings) {
    const attempt = request.length;
    request.push({ messages, settings });
    try {
      for (const value of attempt === 0 ? first : second) {
        if (value instanceof Error) throw value;
        yield value;
      }
      return { completion: "synthetic" };
    } finally {
      lifecycle.push(signal.aborted);
    }
  }
  const output = [];
  for await (const chunk of recoverLocalResponse(start, source, options, new AbortController().signal, roots, previousSideEffects)) output.push(chunk);
  return { output, request, lifecycle };
}

await test("scope is the actual PC73 loopback alias", () => {
  assertTrue(superviseLocalResponse("local-qwen38", "http://127.0.0.1:18081/v1"));
  assertEqual(superviseLocalResponse("local-qwen38", "https://example.invalid/v1"), false);
  assertEqual(superviseLocalResponse("another-model", "http://127.0.0.1:18081/v1"), false);
});

await test("exact and near-identical repetitions abort early and retry from the original input", async () => {
  for (const phrases of [
    Array.from({ length: 24 }, () => text("同じ確認結果を繰り返しています。取得済みの証拠は変わりません。")),
    Array.from({ length: 24 }, (_, i) => text(`現在の検証条件では${i % 2 ? "今回の" : "対象の"}合成データから同じ結果が得られました。`)),
    Array.from({ length: 24 }, () => text("The same synthetic fact is being restated without any progress here ")),
  ]) {
    const result = await run(phrases, [text("RECOVERED")]);
    assertEqual(result.request.length, 2);
    assertTrue(result.lifecycle[0]);
    assertTrue(result.output.length < phrases.length);
    assertEqual(result.request[1].messages[1], source[1]);
    assertTrue(result.request[1].messages[0].content.includes(roots[0]));
    assertTrue(!JSON.stringify(result.request[1]).includes("取得済みの証拠は変わりません"));
    const reset = result.output.findIndex((item) => item.pc73Recovery === "reset");
    assertTrue(reset > 0);
    assertEqual(result.output[reset + 1].content, "RECOVERED");
    assertEqual(events.at(-1).reason, "repeated_generation");
  }
});

await test("code, CSV, JSON and normal tool-looking examples do not trigger a retry", async () => {
  const cases = ["```xml\n<tool_call>example</tool_call>\n```\n", '{"example":"同じ値を示す長いサンプルです。"}\n',
    "1,同じ値を示す長いサンプルです。\n", "- 同じ値を示す長いサンプルです。\n", "`</tool_call>` is shown inline.\n"];
  for (const example of cases) {
    const result = await run([text(example.repeat(10))], []);
    assertEqual(result.request.length, 1);
    assertEqual(result.output[1].content, example.repeat(10));
  }
  const report = Array.from({ length: 30 }, (_, index) => `Module ID${index} covers a separate synthetic validation scenario.`).join("\n");
  const distinct = await run([text(report)], []);
  assertEqual(distinct.request.length, 1);
  assertEqual(distinct.output[1].content, report);
});

await test("raw tool markup and invalid structured calls are discarded before tool execution", async () => {
  const good = tool({ id: "good", index: 0, function: { name: "read_file", arguments: '{"filepath":"synthetic"}' } });
  for (const bad of [
    [text("Write-Output alpha </tool_"), text("call>")],
    [tool({ id: "bad", index: 0, function: { name: "unadvertised", arguments: "{}" } })],
    [tool({ id: "bad", index: 0, function: { name: "read_file", arguments: '{"filepath":7}' } })],
    [tool({ id: "bad", index: 0, function: { name: "read_file", arguments: '{"filepath":' } })],
    [{ role: "assistant", pc73ToolFinish: true }],
  ]) {
    const result = await run(bad, [good]);
    assertEqual(result.request.length, 2);
    assertEqual(result.output.flatMap((chunk) => chunk.toolCalls ?? []).map((call) => call.id), ["good"]);
    assertEqual(events.at(-1).reason, "invalid_tool_call");
  }
});

await test("native tools assemble by index and recovery cannot repeat a possible side effect", async () => {
  const delta = [tool({ id: "a", index: 0, function: { name: "read_file", arguments: '{"filepath":' } },
                      { id: "b", index: 1, function: { name: "read_file", arguments: '{"filepath":' } }),
    tool({ index: 1, function: { arguments: '"second"}' } }, { index: 0, function: { arguments: '"first"}' } })];
  const good = await run(delta, []);
  assertEqual(good.request.length, 1);
  assertEqual(good.output.flatMap((chunk) => chunk.toolCalls ?? []).map((call) => JSON.parse(call.function.arguments).filepath), ["first", "second"]);
  const unsafe = await run([text("</tool_call>")], delta, true);
  assertEqual(unsafe.request[1].settings.tools, []);
  assertEqual(unsafe.output.flatMap((chunk) => chunk.toolCalls ?? []).length, 0);
  assertTrue(unsafe.output.some((chunk) => chunk.content?.includes("stopped after two")));
});

await test("network failures do not start another inference", async () => {
  const failure = new Error("synthetic network error");
  let attempts = 0;
  async function* start() { attempts++; throw failure; }
  try {
    for await (const _ of recoverLocalResponse(start, source, options, new AbortController().signal, roots, false)) { /* only count attempts */ }
    throw new Error("Expected the original failure");
  } catch (error) { assertEqual(error === failure, true); }
  assertEqual(attempts, 1);
});

await test("GUI reset removes only the current partial assistant and keeps prior tool receipts", () => {
  const gui = fs.readFileSync(path.join(stage, "gui/assets/index.js"), "utf8");
  const begin = gui.indexOf("if(o.pc73Recovery){");
  const stop = gui.indexOf("let s=e.history[e.history.length-1]", begin);
  const update = vm.runInNewContext(`(e, marker) => { for (const o of [marker]) { ${gui.slice(begin, stop)} } }`,
    { g8e: (message) => message.content === "" && !message.toolCalls, ro: () => "new-assistant" });
  const receipt = { message: { role: "assistant", id: "tool-owner", content: "", toolCalls: [{}] }, toolCallStates: [{ status: "done" }] };
  const state = { history: [{ message: { role: "user", id: "original-task" } }, receipt], isStreaming: true };
  update(state, { pc73Recovery: "begin" });
  state.history.push({ message: { id: "bad", role: "assistant", content: "bad output" } });
  update(state, { pc73Recovery: "reset" });
  assertEqual(state.history.map((item) => item.message.id), ["original-task", "tool-owner", "new-assistant"]);
  assertEqual(state.history[1], receipt);
});

await test("GUI summary retains a verbatim user goal when no later instruction supersedes it", () => {
  const gui = fs.readFileSync(path.join(stage, "gui/assets/index.js"), "utf8");
  const start = gui.indexOf("if(A){a=A,o=e.slice(O+1);if(!o.some(");
  const end = gui.indexOf("break}", start) + "break}".length;
  const select = vm.runInNewContext(`(e) => { let a='',o=e; for(let O=e.length-1;O>=0;O--){
    const A=e[O].conversationSummary;${gui.slice(start, end)}} return o; }`);
  const original = { message: source[1], contextItems: [{ content: "old attachment" }] };
  const compacted = { message: text("old answer"), conversationSummary: "Working notes only." };
  const continuing = { message: text("unfinished") };
  assertEqual(select([original, compacted, continuing]), [{ ...original, contextItems: [] }, continuing]);
  const newer = { message: { role: "user", content: "Now inspect the other fixture." } };
  assertEqual(select([original, compacted, newer]), [newer]);
});

summary("response_recovery");
