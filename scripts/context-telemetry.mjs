// Numeric-only context accounting at Pi's provider boundary.
// The caller supplies a tokenizer for the deployed Qwen model; no character
// heuristic is used for supervisor decisions.

export const CONTEXT_LIMIT = 32768;
const CATEGORIES = ["system_prompt", "tool_schema", "tool_result", "conversation", "retrieval"];
const START = "<localbrain-retrieval>";
const END = "</localbrain-retrieval>";

export function splitTaggedRetrieval(text) {
  if (typeof text !== "string") return [{ category: "conversation", text: "" }];
  const blocks = [];
  let cursor = 0;
  while (cursor < text.length) {
    const start = text.indexOf(START, cursor);
    if (start < 0) {
      blocks.push({ category: "conversation", text: text.slice(cursor) });
      break;
    }
    const end = text.indexOf(END, start + START.length);
    if (end < 0) {
      blocks.push({ category: "conversation", text: text.slice(cursor) });
      break;
    }
    if (start > cursor) blocks.push({ category: "conversation", text: text.slice(cursor, start) });
    blocks.push({ category: "system_prompt", text: START });
    blocks.push({ category: "retrieval", source: "secondbrain",
      text: text.slice(start + START.length, end) });
    blocks.push({ category: "system_prompt", text: END });
    cursor = end + END.length;
  }
  return blocks;
}

export function classifyContext(context, payload) {
  const blocks = [];
  if (context.systemPrompt) blocks.push({ category: "system_prompt", text: context.systemPrompt });
  // The finalized payload, rather than installed tools, is authoritative.
  for (const tool of payload.tools ?? [])
    blocks.push({ category: "tool_schema", text: JSON.stringify(tool) });
  for (const message of context.messages ?? []) {
    const category = message.role === "toolResult" ? "tool_result" : "conversation";
    const content = typeof message.content === "string" ? [{ type: "text", text: message.content }] :
      (message.content ?? []);
    for (const part of content) {
      if (part.type === "text" && typeof part.text === "string") {
        if (category === "conversation" && message.role === "user")
          blocks.push(...splitTaggedRetrieval(part.text));
        else blocks.push({ category, text: part.text });
      } else if (part.type === "toolCall") {
        blocks.push({ category: "conversation", text: JSON.stringify({
          name: part.name, arguments: part.arguments }) });
      } else if (part.type === "thinking" && typeof part.thinking === "string") {
        blocks.push({ category: "conversation", text: part.thinking });
      }
    }
  }
  return blocks.filter(block => block.text.length > 0);
}

export async function accountContext(context, payload, countTokens) {
  const blocks = classifyContext(context, payload);
  const counts = Object.fromEntries(CATEGORIES.map(category => [category, 0]));
  const result = await countTokens(blocks.map(block => block.text), payload);
  const segmentTokens = Array.isArray(result) ? result : result?.counts;
  if (!Array.isArray(segmentTokens) || segmentTokens.length !== blocks.length ||
      segmentTokens.some(value => !Number.isSafeInteger(value) || value < 0))
    throw new Error("Tokenizer returned invalid segment counts");
  for (let index = 0; index < blocks.length; index++) counts[blocks[index].category] += segmentTokens[index];
  const classifiedTokens = Object.values(counts).reduce((sum, value) => sum + value, 0);
  const promptTokens = Array.isArray(result) ? classifiedTokens : result.promptTokens;
  if (!Number.isSafeInteger(promptTokens) || promptTokens < classifiedTokens || promptTokens === 0)
    throw new Error("Invalid full prompt count");
  counts.framing = promptTokens - classifiedTokens;
  const shares = Object.fromEntries(["tool_schema", "tool_result", "conversation", "retrieval"]
    .map(category => [category, counts[category] / promptTokens]));
  return {
    context_limit: CONTEXT_LIMIT,
    estimated_prompt_tokens: promptTokens,
    context_usage: promptTokens / CONTEXT_LIMIT,
    tokens: counts,
    shares,
    tool_count_exposed: (payload.tools ?? []).length,
    context_metrics_available: true,
  };
}
