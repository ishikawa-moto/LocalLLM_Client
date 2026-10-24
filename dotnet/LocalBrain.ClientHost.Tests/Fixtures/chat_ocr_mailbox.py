"""Public fixture only: actual Server mailbox over a private subprocess pipe."""
import base64
import hashlib
import importlib.util
import json
from pathlib import Path
import sqlite3
import sys

source, root = map(Path, sys.argv[1:3])
root.mkdir(parents=True, exist_ok=False)
(root / "runtime").mkdir()
spec = importlib.util.spec_from_file_location("public_chat_ocr", source / "chat_ocr.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
certificate = b"PUBLIC_FIXTURE_CERTIFICATE"
pin = hashlib.sha256(certificate).hexdigest()
mailbox = module.Mailbox(root, "192.0.2.73", "pc73-windows-host", pin, pin)
with sqlite3.connect(root / "runtime/chat.sqlite3") as db:
    db.execute("CREATE TABLE conversations(id TEXT PRIMARY KEY)")

def call(value):
    action = value["action"]
    if action == "submit":
        body = value["body"]
        with sqlite3.connect(root / "runtime/chat.sqlite3") as db:
            db.execute("INSERT OR IGNORE INTO conversations VALUES(?)", (body["conversation_id"],))
        mailbox.authenticate("chat", "127.0.0.1", certificate)
        return mailbox.chat("submit", body)
    if action == "worker":
        path = value["path"]
        module.require(path in ("/v1/chat-ocr/next", "/v1/chat-ocr/update"))
        mailbox.authenticate("worker", "192.0.2.73", certificate, value["owner"])
        return mailbox.dispatch(path.rsplit("/", 1)[-1], value["body"])
    if action == "chat":
        mailbox.authenticate("chat", "127.0.0.1", certificate)
        return mailbox.chat(value["route"], value["body"])
    raise ValueError("PUBLIC_FIXTURE_UNKNOWN_ACTION")

for line in sys.stdin:
    try:
        result = {"status": 200, "body": call(json.loads(line))}
    except module.OcrError as error:
        result = {"status": error.status, "body": {"error": error.code}}
    except Exception:
        result = {"status": 500, "body": {"error": "PUBLIC_FIXTURE_FAILURE"}}
    if result["status"] != 200:
        with (root / "public-wire-errors.jsonl").open("a", encoding="utf-8") as out:
            out.write(json.dumps({"status": result["status"], "error_code": result["body"].get("error")}) + "\n")
    print(json.dumps(result, ensure_ascii=True, allow_nan=False), flush=True)
