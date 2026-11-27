// Modified for LocalBrain PC73; see NOTICE.
type Call = { id?: string; index?: number; type?: string; function?: { name?: string; arguments?: string } };
type Message = { role: string; content?: unknown; toolCalls?: Call[]; pc73ToolFinish?: boolean; pc73Recovery?: string };
type AdvertisedTool = { function: { name: string; parameters?: { required?: string[]; properties?: Record<string, { type?: string | string[] }> } } };
type Options = { tools?: AdvertisedTool[] };
type Reason = "repeated_generation" | "invalid_tool_call";

const checkedModels = new Set(["local-qwen38"]);
const toolMarkup = /<\/?tool_call\b[^>\r\n]*>|<\/?function=(?:[^>\r\n]+)>/i;
const structuredLine = /^(?:[-*#|>{\[]|\d+[.)]\s|\w*[,\t]|\w+\s*[=:;(]|(?:const|let|var|function|class|import|export|return|def)\s)/;
const excerptLimit = 1536;

export function superviseLocalResponse(model: string, baseUrl: string | undefined): boolean {
  try {
    const url = new URL(baseUrl ?? "");
    return checkedModels.has(model) && url.protocol === "http:" && url.hostname === "127.0.0.1" && url.port === "18081";
  } catch {
    return false;
  }
}

function normalizeProse(text: string): string {
  return text.normalize("NFKC").toLowerCase().replace(/[^\p{L}\p{N}]/gu, "");
}

function closeMatch(left: string, right: string): boolean {
  if (left === right) return true;
  if (Math.abs(left.length - right.length) > 10) return false;
  let sameStart = 0;
  while (sameStart < left.length && left[sameStart] === right[sameStart]) sameStart++;
  let sameEnd = 0;
  while (sameEnd < Math.min(left.length, right.length) - sameStart && left.at(-1 - sameEnd) === right.at(-1 - sameEnd)) sameEnd++;
  return sameStart + sameEnd >= Math.max(left.length, right.length) * 0.89;
}

function proseDetector(canCallTools: boolean) {
  let line = "";
  let sentence = "";
  let fenced = false;
  let inline = false;
  let continuous = "";
  let sincePeriodCheck = 0;
  const recent: { text: string; facts: string }[] = [];

  return (chunk: string): Reason | undefined => {
    for (const char of chunk) {
      if (char === "\r") continue;
      if (char === "\n") {
        if (/^\s*(?:```|~~~)/.test(line)) fenced = !fenced;
        line = sentence = continuous = "";
        inline = false;
        continue;
      }
      line = (line + char).slice(-excerptLimit);
      if (fenced || /^\s*(?:```|~~~)/.test(line)) continue;
      if (char === "`") { inline = !inline; continue; }
      if (inline) continue;
      if (canCallTools && char === ">" && toolMarkup.test(line)) return "invalid_tool_call";
      if (structuredLine.test(line.trimStart()) || /[{}|\t]/.test(char)) {
        sentence = continuous = "";
        continue;
      }
      sentence = (sentence + char).slice(-384);
      continuous = (continuous + char).slice(-excerptLimit);
      if (/[。！？.!?]/.test(char)) {
        const text = normalizeProse(sentence);
        // Identifier or numeric changes often carry the actual evidence, even
        // when every item in a plain-text report uses the same wording.
        const facts = (sentence.match(/\b[A-Z][A-Z0-9_-]{1,}\b|\d+/g) ?? []).join("\0");
        sentence = "";
        if (text.length >= 16) {
          const exact = recent.filter((previous) => previous.text === text && previous.facts === facts).length;
          const near = recent.filter((previous) => previous.facts === facts && closeMatch(previous.text, text)).length;
          if (exact >= 5 || near >= 7) return "repeated_generation";
          recent.push({ text, facts });
          if (recent.length > 24) recent.shift();
        }
      }
      if (++sincePeriodCheck >= 128) {
        sincePeriodCheck = 0;
        const text = normalizeProse(continuous);
        for (let length = 24; length <= Math.min(256, text.length / 6); length++) {
          const period = text.slice(-length);
          if (text.endsWith(period.repeat(6)) && new Set(period).size >= 6) return "repeated_generation";
        }
      }
    }
    return undefined;
  };
}

function fitsType(value: unknown, type: string): boolean {
  if (type === "integer") return Number.isInteger(value);
  if (type === "array") return Array.isArray(value);
  if (type === "object") return value !== null && typeof value === "object" && !Array.isArray(value);
  if (type === "null") return value === null;
  return typeof value === type;
}

function validateCalls(calls: Call[], tools: AdvertisedTool[]): boolean {
  const ids = new Set<string>();
  return calls.every((call) => {
    const name = call.function?.name;
    const tool = tools.find((candidate) => candidate.function.name === name);
    if (!tool || !call.id || ids.has(call.id) || typeof call.function?.arguments !== "string") return false;
    ids.add(call.id);
    try {
      const args = JSON.parse(call.function.arguments);
      if (!fitsType(args, "object")) return false;
      const schema = tool.function.parameters;
      if (schema?.required?.some((key) => !Object.hasOwn(args, key))) return false;
      return Object.entries(schema?.properties ?? {}).every(([key, property]) =>
        !Object.hasOwn(args, key) || !property.type || [property.type].flat().some((type) => fitsType(args[key], type)));
    } catch {
      return false;
    }
  });
}

function addSystemGuidance(messages: Message[], text: string): Message[] {
  if (!messages.some((message) => message.role === "system" && typeof message.content === "string")) {
    return [{ role: "system", content: text }, ...messages];
  }
  return messages.map((message) => message.role === "system" && typeof message.content === "string"
    ? { ...message, content: `${message.content}\n\n${text}` } : message);
}

export async function* recoverLocalResponse<Log>(
  start: (messages: Message[], signal: AbortSignal, options: Options) => AsyncGenerator<Message, Log, void>,
  messages: Message[],
  options: Options,
  signal: AbortSignal,
  workspaceDirs: string[],
  previousSideEffects: boolean,
): AsyncGenerator<Message, Log | undefined, void> {
  const scope = workspaceDirs.length ? `\nThe open workspace roots are ${JSON.stringify(workspaceDirs)}. Keep the user's stated path and scope. Avoid broad searches above those roots unless the user explicitly requested one.` : "";
  const original = scope ? addSystemGuidance(messages, scope) : messages;
  let request = original;
  let settings = options;
  let prevReason: Reason | undefined;
  yield { role: "assistant", pc73Recovery: "begin" };

  for (let attempt = 0; attempt <= 1 && !signal.aborted; attempt++) {
    const controller = new AbortController();
    const iterator = start(request, AbortSignal.any([signal, controller.signal]), settings);
    const tools = settings.tools ?? [];
    const examine = proseDetector(tools.length > 0 || !!prevReason);
    const examineThinking = proseDetector(false);
    const pending: Message[] = [];
    const calls = new Map<number, Call>();
    let lastIndex = 0;
    let expectTool = false;
    let held = false;
    let invalid: Reason | undefined;
    let completed: Log | undefined;

    try {
      while (!signal.aborted) {
        const next = await iterator.next();
        if (next.done) { completed = next.value; break; }
        const chunk = next.value;
        if (chunk.pc73ToolFinish) { expectTool = true; continue; }
        if (chunk.toolCalls?.length) {
          held = true;
          expectTool = true;
          for (const part of chunk.toolCalls) {
            const known = part.id ? [...calls.entries()].find(([, call]) => call.id === part.id)?.[0] : undefined;
            const index = part.index ?? known ?? (part.id ? calls.size : lastIndex);
            if (!Number.isInteger(index) || index < 0 || index >= 32) {
              invalid = "invalid_tool_call";
              break;
            }
            const call = calls.get(index) ?? { type: "function", function: { name: "", arguments: "" } };
            if (part.id && call.id && part.id !== call.id || calls.size >= 32 && !calls.has(index)) {
              invalid = "invalid_tool_call";
              break;
            }
            if (part.id) call.id = part.id;
            call.function!.name += part.function?.name ?? "";
            call.function!.arguments += part.function?.arguments ?? "";
            if (call.function!.arguments!.length > 65536 || call.function!.name!.length > 128) {
              invalid = "invalid_tool_call";
              break;
            }
            calls.set(index, call);
            lastIndex = index;
          }
          if (invalid) break;
        }
        if (typeof chunk.content === "string") {
          invalid = (chunk.role === "thinking" ? examineThinking : examine)(chunk.content);
          if (invalid) break;
        }
        if (held) {
          // Delay only the tail after the first tool delta until validation.
          if (!chunk.toolCalls?.length || chunk.content) pending.push({ ...chunk, toolCalls: undefined });
        } else yield chunk;
      }
    } finally {
      controller.abort();
      await iterator.return(undefined as Log);
    }

    if (signal.aborted) return;
    const validatedCalls = [...calls.entries()].sort(([a], [b]) => a - b).map(([, call]) => call);
    if (!invalid && expectTool && (!validatedCalls.length || !validateCalls(validatedCalls, tools))) invalid = "invalid_tool_call";
    if (!invalid) {
      yield* pending;
      if (validatedCalls.length) yield { role: "assistant", content: "", toolCalls: validatedCalls };
      yield { role: "assistant", pc73Recovery: "end" };
      return completed;
    }

    console.info("[LocalBrain Continue] response recovery", JSON.stringify({ reason: invalid, attempt: attempt + 1, retry: attempt === 0 }));
    yield { role: "assistant", pc73Recovery: "reset" };
    if (attempt === 1) {
      yield { role: "assistant", content: "LocalBrain stopped after two abnormal responses. The earlier conversation and tool results are still available." };
      yield { role: "assistant", pc73Recovery: "end" };
      return;
    }
    prevReason = invalid;
    if (previousSideEffects) settings = { ...options, tools: [] };
    request = addSystemGuidance(original, "LocalBrain discarded the previous generated response. Follow the existing user request and verified tool results. Do not repeat the same sentence or emit tool syntax as plain text. Use only advertised native tools with valid arguments. Never repeat a completed operation." +
      (previousSideEffects ? " This recovery can only report current progress. State any remaining step without executing tools." : ""));
  }
}
