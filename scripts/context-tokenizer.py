"""Count segments with a verified local Qwen tokenizer; stdout is numeric JSONL only."""

import json
import hashlib
from pathlib import Path
import sys

from jinja2 import Environment
from tokenizers import Tokenizer

TEMPLATE_SHA256 = "c3cf9e34abf4f9e36c2d72165aa9c132d3e2a725b6c2586aaa3a8af9d7a81041"


def reject(message):
    raise ValueError(message)


def main():
    tokenizer = Tokenizer.from_file(sys.argv[1])
    template_file = Path(__file__).with_name("qwen-chat-template.jinja")
    template_bytes = template_file.read_bytes()
    if hashlib.sha256(template_bytes).hexdigest() != TEMPLATE_SHA256:
        raise ValueError("unverified_chat_template")
    template = Environment(autoescape=False).from_string(template_bytes.decode("utf-8"))
    for line in sys.stdin:
        request = None
        try:
            if len(line) > 12_000_000:
                raise ValueError("request_too_large")
            request = json.loads(line)
            texts = request["texts"]
            if not isinstance(texts, list) or len(texts) > 4096 or any(
                not isinstance(value, str) for value in texts
            ):
                raise ValueError("invalid_segments")
            counts = [len(item.ids) for item in tokenizer.encode_batch(
                texts, add_special_tokens=False
            )]
            messages = request["messages"]
            if not isinstance(messages, list) or len(messages) > 4096:
                raise ValueError("invalid_messages")
            # llama.cpp parses OpenAI function.arguments JSON before passing
            # messages into the GGUF Jinja template. Mirror that normalization
            # on a private copy; never rewrite the model request itself.
            normalized = []
            for message in messages:
                if not isinstance(message, dict):
                    raise ValueError("invalid_message")
                if message.get("role") != "assistant" or not message.get("tool_calls"):
                    normalized.append(message)
                    continue
                copy = dict(message)
                copy["tool_calls"] = []
                for call in message["tool_calls"]:
                    call = dict(call)
                    if isinstance(call.get("function"), dict):
                        function = dict(call["function"])
                        if isinstance(function.get("arguments"), str):
                            function["arguments"] = json.loads(function["arguments"])
                        call["function"] = function
                    copy["tool_calls"].append(call)
                normalized.append(copy)
            tools = request.get("tools") or []
            if not isinstance(tools, list) or len(tools) > 4096:
                raise ValueError("invalid_tools")
            supplied = request.get("chat_template_kwargs") or {}
            if not isinstance(supplied, dict):
                raise ValueError("invalid_template_kwargs")
            kwargs = {key: supplied[key] for key in
                      ("enable_thinking", "reasoning_effort", "preserve_thinking", "add_vision_id")
                      if key in supplied}
            rendered = template.render(messages=normalized, tools=tools,
                                       add_generation_prompt=True,
                                       raise_exception=reject, **kwargs)
            prompt_tokens = len(tokenizer.encode(rendered, add_special_tokens=False).ids)
            print(json.dumps({"id": request["id"], "counts": counts,
                              "prompt_tokens": prompt_tokens}), flush=True)
        except Exception as error:
            print(json.dumps({"id": request.get("id") if isinstance(request, dict) else None,
                              "error": type(error).__name__}), flush=True)


if __name__ == "__main__":
    main()
