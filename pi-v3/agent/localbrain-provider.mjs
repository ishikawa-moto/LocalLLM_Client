import { createProvider, openAICompletionsApi } from "@earendil-works/pi-ai/compat";
import { Buffer } from "node:buffer";
import { connect } from "node:net";
import { TextDecoder } from "node:util";

const SOCKET_PATH = "/run/localbrain/model.sock";
const BASE_URL = "http://localbrain.invalid/v1";
const CHAT_URL = `${BASE_URL}/chat/completions`;
const AUTH_PLACEHOLDER = "localbrain-placeholder";
const MAX_REQUEST_BYTES = 8 * 1024 * 1024;
const MAX_RESPONSE_BYTES = 48 * 1024 * 1024;
const TIMEOUT_MS = 10 * 60 * 1000;
const CONTENT_TYPE_RE = /^[!#$%&'*+.^_`|~0-9A-Za-z-]+\/[!#$%&'*+.^_`|~0-9A-Za-z-]+(?:[ \t]*;[ \t]*[!#$%&'*+.^_`|~0-9A-Za-z-]+[ \t]*=[ \t]*(?:[!#$%&'*+.^_`|~0-9A-Za-z-]+|"(?:[\x20-\x21\x23-\x5b\x5d-\x7e]|\\[\x20-\x7e])*"))*[ \t]*$/;

function decodeResponse(bytes) {
  let frame;
  try {
    const text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
    frame = JSON.parse(text);
  } catch {
    throw new Error("LocalBrain relay returned invalid UTF-8 or JSON");
  }

  if (!frame || typeof frame !== "object" || Array.isArray(frame)) {
    throw new Error("LocalBrain relay returned an invalid response object");
  }
  if (frame.error !== undefined && frame.error !== null && frame.error !== false) {
    throw new Error("LocalBrain relay returned an error response");
  }

  const status = frame.status;
  if (!Number.isInteger(status) || status < 200 || status > 599) {
    throw new Error("LocalBrain relay returned an invalid status");
  }
  if (status >= 400) {
    throw new Error("LocalBrain relay returned an HTTP error response");
  }

  const contentType = frame.content_type;
  if (
    typeof contentType !== "string" ||
    /[\r\n]/.test(contentType) ||
    !CONTENT_TYPE_RE.test(contentType)
  ) {
    throw new Error("LocalBrain relay returned an invalid content type");
  }

  const encoded = frame.body_base64;
  if (
    typeof encoded !== "string" ||
    encoded.length % 4 !== 0 ||
    !/^[A-Za-z0-9+/]*={0,2}$/.test(encoded)
  ) {
    throw new Error("LocalBrain relay returned malformed base64");
  }
  const body = Buffer.from(encoded, "base64");
  if (body.toString("base64") !== encoded) {
    throw new Error("LocalBrain relay returned malformed base64");
  }

  const requiresNullBody = status === 204 || status === 205 || status === 304;
  if (requiresNullBody && body.length !== 0) {
    throw new Error("LocalBrain relay returned a body for a bodyless status");
  }
  return new Response(requiresNullBody ? null : body, {
    status,
    headers: { "content-type": contentType },
  });
}

export async function relayFetch(input, init) {
  const isRequest = input instanceof Request;
  const url = isRequest ? input.url : input instanceof URL ? input.href : input;
  if (url !== CHAT_URL) {
    throw new TypeError("LocalBrain relay only accepts its fixed chat completions URL");
  }
  const method = init?.method ?? (isRequest ? input.method : "GET");
  if (typeof method !== "string" || method.toUpperCase() !== "POST") {
    throw new TypeError("LocalBrain relay only accepts POST");
  }

  let request;
  try {
    request = new Request(input, { ...init, duplex: "half" });
  } catch {
    throw new TypeError("LocalBrain relay received an invalid request");
  }

  const signal = request.signal;
  return new Promise((resolve, reject) => {
    let socket;
    let reader;
    let timer;
    let settled = false;
    const responseChunks = [];
    let responseBytes = 0;

    function finish(error, response) {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      signal.removeEventListener("abort", onAbort);
      if (reader) {
        void reader.cancel().catch(() => {});
      }
      socket?.destroy();
      responseChunks.length = 0;
      if (error) reject(error);
      else resolve(response);
    }

    function onAbort() {
      finish(new DOMException("LocalBrain relay request aborted", "AbortError"));
    }

    timer = setTimeout(() => {
      finish(new Error("LocalBrain relay request timed out"));
    }, TIMEOUT_MS);
    signal.addEventListener("abort", onAbort, { once: true });
    if (signal.aborted) {
      onAbort();
      return;
    }

    async function sendRequest() {
      try {
        const bodyChunks = [];
        let bodyBytes = 0;
        if (request.body) {
          reader = request.body.getReader();
          try {
            while (!settled) {
              const { value, done } = await reader.read();
              if (settled) return;
              if (done) break;
              if (!(value instanceof Uint8Array)) {
                finish(new TypeError("LocalBrain relay received an invalid body chunk"));
                return;
              }
              bodyBytes += value.byteLength;
              if (bodyBytes > MAX_REQUEST_BYTES) {
                finish(new RangeError("LocalBrain relay request body exceeds 8 MiB"));
                return;
              }
              bodyChunks.push(Buffer.from(value));
            }
          } finally {
            reader.releaseLock();
            reader = undefined;
          }
        }
        if (settled) return;

        const body = Buffer.concat(bodyChunks, bodyBytes);
        const wireRequest = JSON.stringify({
          method: "POST",
          path: "/v1/chat/completions",
          contentType: "application/json",
          bodyBase64: body.toString("base64"),
        }) + "\n";

        socket = connect({ path: SOCKET_PATH });
        socket.on("error", () => {
          finish(new Error("LocalBrain relay socket failed"));
        });
        socket.on("connect", () => {
          if (settled) return;
          try {
            socket.write(wireRequest, "utf8");
          } catch {
            finish(new Error("LocalBrain relay could not send the request"));
          }
        });
        socket.on("data", (chunk) => {
          if (settled) return;
          try {
            responseBytes += chunk.length;
            if (responseBytes > MAX_RESPONSE_BYTES) {
              finish(new RangeError("LocalBrain relay response exceeds 48 MiB"));
              return;
            }
            const newline = chunk.indexOf(0x0a);
            if (newline === -1) {
              responseChunks.push(chunk);
              return;
            }
            if (newline !== chunk.length - 1) {
              finish(new Error("LocalBrain relay returned data after its response line"));
              return;
            }
            responseChunks.push(chunk.subarray(0, newline));
            const response = decodeResponse(
              Buffer.concat(responseChunks, responseBytes - 1),
            );
            finish(undefined, response);
          } catch {
            finish(new Error("LocalBrain relay returned an invalid or error response"));
          }
        });
        socket.on("end", () => {
          finish(new Error("LocalBrain relay reached EOF without a response newline"));
        });
        socket.on("close", () => {
          finish(new Error("LocalBrain relay socket closed before completing the response"));
        });
      } catch {
        finish(new Error("LocalBrain relay request failed"));
      }
    }

    void sendRequest();
  });
}

const api = openAICompletionsApi();

export default function localbrainProvider(pi) {
  pi.registerProvider(createProvider({
    id: "localbrain",
    name: "LocalBrain",
    baseUrl: BASE_URL,
    auth: {
      apiKey: {
        name: "LocalBrain socket authorization",
        async resolve() {
          // The separately implemented socket server authorizes requests.
          return {
            auth: { apiKey: AUTH_PLACEHOLDER },
            source: "LocalBrain placeholder",
          };
        },
      },
    },
    models: [{
      id: "local-qwen38",
      name: "Local Qwen",
      provider: "localbrain",
      api: "openai-completions",
      baseUrl: BASE_URL,
      contextWindow: 32768,
      maxTokens: 2048,
      cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
      input: ["text"],
      reasoning: false,
    }],
    api: {
      stream(model, context, options) {
        return api.stream(model, context, {
          ...options,
          apiKey: AUTH_PLACEHOLDER,
          fetch: relayFetch,
        });
      },
      streamSimple(model, context, options) {
        return api.streamSimple(model, context, {
          ...options,
          apiKey: AUTH_PLACEHOLDER,
          fetch: relayFetch,
        });
      },
    },
  }));
}
