"""CPU-only, line-delimited Laya supervisor for the ClientPC orchestrator.

Only a compact, allowlisted English state crosses this process boundary.
Stdout is protocol-only. No request body or model answer is logged.
"""

import argparse
import json
import os
import sys
import time

MAX_LINE = 16_384
STATE_KEYS = {
    "phase", "tool_calls", "files_read", "files_changed", "tests_run",
    "tests_passed", "same_error_count", "same_command_count", "replans",
    "context_usage", "tool_schema_share", "tool_result_share",
    "conversation_share", "retrieval_share", "context_metrics_available",
    "progress", "last_action_changed_state",
}
REVIEW_STATE_KEYS = {
    "risk", "files_changed", "diff_lines", "tests_passed", "languages",
    "task_kind", "public_api", "security", "concurrency",
}
QUESTIONS = {
    "action": {
        "type": "choice",
        "instructions": "Which next supervisor action best advances this coding task?",
        "criteria": {
            "CONTINUE": "The agent is making progress and should continue its current plan.",
            "RUN_TEST": "Run a relevant test or build now to verify the current change.",
            "REPLAN": "Progress is poor; revise the plan before more edits.",
            "COMPACT": "Context is near its limit; preserve evidence and compact the context.",
            "FRESH_REVIEW": "Get an independent review from a fresh model context.",
            "ESCALATE_SOL": "An external expert review is warranted by uncertainty or repeated failure.",
            "STOP": "The task has met validation requirements or must stop for a hard rule.",
        },
    }
}
REVIEW_QUESTIONS = {
    "review_route": {
        "type": "choice",
        "instructions": "Which independent external reviewer route best fits this task? Choose both when uncertain. Do not review code.",
        "criteria": {
            "copilot": "Use GitHub Copilot for an ordinary scoped change with clear tests.",
            "sol": "Use Sol for a difficult or high-risk change needing deeper reasoning.",
            "both": "Use both independently when risk, uncertainty, or disagreement warrants it.",
        },
    }
}


def write(value):
    sys.stdout.write(json.dumps(value, ensure_ascii=True, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--threads", type=int, choices=(2, 4, 6), default=2)
    args = parser.parse_args()
    os.environ["USE_TF"] = "0"
    os.environ["CUDA_VISIBLE_DEVICES"] = ""
    os.environ["OMP_NUM_THREADS"] = str(args.threads)

    import torch
    import laya

    torch.set_num_threads(args.threads)
    if torch.cuda.is_available():
        raise RuntimeError("CPU-only supervisor unexpectedly has CUDA")
    started = time.monotonic()
    agent = laya.load("convaiinnovations/laya", subfolder="typed-decisions", device="cpu")
    write({"type": "ready", "load_ms": round((time.monotonic() - started) * 1000),
           "laya_version": laya.__version__, "torch_version": torch.__version__,
           "device": "cpu", "threads": args.threads})

    for line in sys.stdin:
        request_id = None
        try:
            if len(line) > MAX_LINE:
                raise ValueError("request_too_large")
            request = json.loads(line)
            request_id = request.get("id")
            state = request.get("state")
            kind = request.get("kind", "action")
            if not isinstance(request_id, str) or not isinstance(state, dict):
                raise ValueError("invalid_request")
            if kind not in ("action", "review_route"):
                raise ValueError("unknown_request_kind")
            if set(state) - (STATE_KEYS if kind == "action" else REVIEW_STATE_KEYS):
                raise ValueError("unknown_state_field")
            if any(isinstance(value, str) and (not value.isascii() or len(value) > 80)
                   for value in state.values()):
                raise ValueError("state_must_be_compact_english")
            started = time.monotonic()
            result = agent.predict(state, QUESTIONS if kind == "action" else REVIEW_QUESTIONS)
            answer = result["answers"][kind]
            write({"id": request_id, kind: answer["choice"],
                   "confidence": answer.get("confidence"),
                   "latency_ms": round((time.monotonic() - started) * 1000)})
        except Exception as error:
            write({"id": request_id, "error": type(error).__name__})


if __name__ == "__main__":
    main()
