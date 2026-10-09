import assert from "node:assert/strict";
import { test } from "node:test";
import { accountContext } from "./context-telemetry.mjs";

// Deterministic test tokenizer validates attribution. Production never uses it.
const count = async texts => texts.map(text => text.length);
const context = (messages, tools = []) => ({ systemPrompt: "SYSTEM", messages, tools });
const payload = tools => ({ tools });
const user = text => ({ role: "user", content: [{ type: "text", text }] });
const toolResult = text => ({ role: "toolResult", content: [{ type: "text", text }] });

test("A: conversation without tools or retrieval dominates", async () => {
  const state = await accountContext(context([user("x".repeat(100))]), payload([]), count);
  assert.equal(state.tokens.tool_result, 0);
  assert.equal(state.tokens.retrieval, 0);
  assert.ok(state.shares.conversation > state.shares.tool_schema);
});

test("B/C: active tool result shrinks when ObservationPack substitutes a preview", async () => {
  const full = await accountContext(context([user("task"), toolResult("x".repeat(8000))]),
    payload([]), count);
  const packed = await accountContext(context([user("task"), toolResult("preview obs_123")]),
    payload([]), count);
  assert.equal(full.tokens.tool_result, 8000);
  assert.equal(packed.tokens.tool_result, "preview obs_123".length);
  assert.ok(full.shares.tool_result > packed.shares.tool_result);
});

test("D: only actually exposed tool schemas are charged", async () => {
  const tools = Array.from({ length: 16 }, (_, i) => ({ type: "function", function: {
    name: `dummy_${i}`, description: "x".repeat(100), parameters: { type: "object" } } }));
  const few = await accountContext(context([user("task")]), payload(tools.slice(0, 1)), count);
  const many = await accountContext(context([user("task")]), payload(tools), count);
  assert.equal(few.tool_count_exposed, 1);
  assert.equal(many.tool_count_exposed, 16);
  assert.ok(many.shares.tool_schema > few.shares.tool_schema);
});

test("E/F: cited SecondBrain content is retrieval exactly once", async () => {
  const evidence = "SECOND_BRAIN_".repeat(20);
  const prompt = `Task\n<localbrain-retrieval>${evidence}</localbrain-retrieval>\nEnd`;
  const state = await accountContext(context([user(prompt)]), payload([]), count);
  assert.equal(state.tokens.retrieval, evidence.length);
  assert.equal(state.tokens.conversation, "Task\n\nEnd".length);
  assert.equal(state.tokens.tool_result, 0);
  assert.equal(state.context_usage, state.estimated_prompt_tokens / state.context_limit);
  assert.equal(state.shares.retrieval, evidence.length / state.estimated_prompt_tokens);
});

test("bad tokenizer response fails closed", async () => {
  await assert.rejects(accountContext(context([user("task")]), payload([]), async () => []),
    /invalid segment counts/);
});

test("full chat template count is the denominator and the unclassified balance is framing", async () => {
  const state = await accountContext(context([user("task")]), payload([]), async texts => ({
    counts: texts.map(text => text.length), promptTokens: 30,
  }));
  assert.equal(state.estimated_prompt_tokens, 30);
  assert.equal(state.tokens.framing, 30 - "SYSTEM".length - "task".length);
  assert.equal(state.shares.conversation, "task".length / 30);
});
