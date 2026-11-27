// Modified for LocalBrain PC73; see NOTICE.
export const TOOL_OUTPUT_BYTES = 8192;
export const TOOL_OUTPUT_LINES = 160;
const TOOL_OUTPUT_TOKENS = 2048;
const TRUNCATED = "\n[Tool output shortened. Request a narrower range or filter.]";
const DUPLICATE = "[Identical read result appears later in this conversation.]";

type Count = (text: string) => number;
type ResultItem = { content?: string };
type Message = {
  role: string;
  content?: unknown;
  toolCallId?: string;
  toolCalls?: { id: string; function?: { name?: string; arguments?: string } }[];
};

export function prefixWithinLimits(text: string, maxBytes: number, maxLines: number): string {
  let bytes = 0;
  let lines = 1;
  let end = 0;
  let previous = "";
  for (const character of text) {
    bytes += Buffer.byteLength(character);
    if (character === "\r" || (character === "\n" && previous !== "\r")) lines++;
    if (bytes > maxBytes || lines > maxLines) break;
    end += character.length;
    previous = character;
  }
  if (text.charAt(end - 1) === "\r" && text.charAt(end) === "\n") end--;
  return text.slice(0, end);
}

export function limitToolText(
  value: string,
  tokens: number,
  count: Count,
  maxBytes = TOOL_OUTPUT_BYTES,
  maxLines = TOOL_OUTPUT_LINES,
): string {
  const candidate = prefixWithinLimits(value, maxBytes, maxLines);
  if (candidate === value && count(candidate) <= tokens) return candidate;
  if (maxLines < 2 || maxBytes < Buffer.byteLength(TRUNCATED) || count(TRUNCATED) > tokens) return "";
  let prefix = prefixWithinLimits(candidate, maxBytes - Buffer.byteLength(TRUNCATED), maxLines - 1);
  // Recount the final text, including the notice. Token counts are not additive.
  while (prefix && count(prefix + TRUNCATED) > tokens) {
    prefix = prefixWithinLimits(prefix, Math.floor(Buffer.byteLength(prefix) / 2), maxLines - 1);
  }
  return prefix + TRUNCATED;
}

export function getToolOutputTokenBudget(contextLength: number, outputTokens: number): number {
  return Math.min(TOOL_OUTPUT_TOKENS, Math.max(0, Math.floor((contextLength - outputTokens) / 8)));
}

export function limitToolItems<T extends ResultItem>(items: T[], tokens: number, count: Count): T[] {
  const result: T[] = [];
  let used = "";
  // Context items are serialized with separators. Keep enough space to tell the
  // model when entire items were omitted, including empty and metadata-only items.
  const bodyBytes = TOOL_OUTPUT_BYTES - Buffer.byteLength(TRUNCATED) - 1;
  const bodyLines = TOOL_OUTPUT_LINES - 1;
  const bodyTokens = Math.max(0, tokens - count(TRUNCATED) - 2);
  let omitted = false;
  for (const item of items) {
    if (result.length >= 32) { omitted = true; break; }
    const separator = result.length ? "\n" : "";
    const text = item.content ?? "";
    const bytesLeft = bodyBytes - Buffer.byteLength(used + separator);
    const linesLeft = bodyLines - (used + separator).split(/\r\n|\r|\n/).length + 1;
    const remaining = bodyTokens - count(used + separator);
    const preview = limitToolText(text, remaining, count, bytesLeft, linesLeft);
    if ((text && !preview) || count(used + separator + preview) > bodyTokens) { omitted = true; break; }
    result.push({ ...item, content: preview });
    used += separator + preview;
    if (preview !== text) { omitted = true; break; }
  }
  if (omitted) {
    const last = result.at(-1);
    if (last && !last.content?.endsWith(TRUNCATED) && count(used + TRUNCATED) <= tokens) {
      last.content = (last.content ?? "") + TRUNCATED;
    } else if (!last && items[0]) {
      result.push({ ...items[0], content: limitToolText(TRUNCATED, tokens, count) });
    }
  }
  return result;
}

export function prepareToolMessages(
  history: Message[],
  latest: Message[],
  latestTokens: number,
  perToolTokens: number,
  count: Count,
): { shortened: number; duplicateReads: number } {
  const readKeys = new Map<string, string>();
  const messages = [...history, ...latest];
  for (const message of messages) {
    for (const call of message.toolCalls ?? []) {
      const name = call.function?.name;
      const args = call.function?.arguments;
      if ((name === "read_file" || name === "read_file_range" || name === "ls") && args && args.length <= 4096) {
        readKeys.set(call.id, `${name}\0${args}`);
      }
    }
  }

  const latestTools = latest.filter((message) => message.role === "tool");
  let shortened = 0;
  for (const [index, message] of latestTools.entries()) {
    const budget = Math.min(perToolTokens, Math.floor(latestTokens / (latestTools.length - index)));
    const text = typeof message.content === "string" ? message.content : "";
    message.content = limitToolText(text, budget, count);
    latestTokens = Math.max(0, latestTokens - count(message.content as string));
    if (message.content !== text) shortened++;
  }

  const seen = new Set<string>();
  let duplicateReads = 0;
  // Keep the most recent result. A changed file/range/list has different content.
  for (let index = messages.length - 1; index >= 0; index--) {
    const message = messages[index];
    if (message.role !== "tool" || typeof message.content !== "string") continue;
    const text = message.content;
    const preview = index < history.length ? limitToolText(text, perToolTokens, count) : text;
    const key = message.toolCallId ? readKeys.get(message.toolCallId) : undefined;
    if (key && preview && preview === text && !preview.includes(TRUNCATED.trim())) {
      const signature = `${key}\0${preview}`;
      if (index < history.length && seen.has(signature)) {
        message.content = DUPLICATE;
        duplicateReads++;
        continue;
      }
      seen.add(signature);
    }
    message.content = preview;
    if (preview !== text) shortened++;
  }
  return { shortened, duplicateReads };
}
