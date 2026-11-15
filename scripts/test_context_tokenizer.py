"""Public JSONL fixtures; no model inference or production input replay."""

import json
from pathlib import Path
import subprocess
import sys
import unittest


class ContextTokenizerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tokenizer = sys.argv[1]

    def test_jsonl_measurement_and_safe_failures(self):
        tools = [{"type": "function", "function": {
            "name": "public_probe",
            "parameters": {"type": "object", "properties": {}},
        }}]
        short = {"texts": [], "messages": [{"role": "user", "content": "Public fixture."}]}
        cases = [short, dict(short, tools=tools)]
        for arguments in ('{"value":"public"}', ""):
            cases.append(dict(short, tools=tools, messages=[
                short["messages"][0],
                {"role": "assistant", "content": None, "tool_calls": [{
                    "id": "public_1", "type": "function",
                    "function": {"name": "public_probe", "arguments": arguments},
                }]},
                {"role": "tool", "tool_call_id": "public_1", "content": "Public result."},
            ]))
        cases.append(dict(short, messages=[
            {"role": "system", "content": "Public first policy."},
            {"role": "system", "content": "PRIVATE_SENTINEL_NOT_FOR_DIAGNOSTICS"},
            short["messages"][0],
        ]))
        result = subprocess.run(
            [sys.executable, "-u", str(Path(__file__).with_name("context-tokenizer.py")),
             self.tokenizer],
            input="".join(json.dumps(dict(case, id=str(i))) + "\n"
                          for i, case in enumerate(cases)),
            text=True, capture_output=True, timeout=40, check=True,
        )
        self.assertEqual(result.stderr, "")
        replies = [json.loads(line) for line in result.stdout.splitlines()]
        self.assertEqual([r["id"] for r in replies], [str(i) for i in range(len(cases))])
        for reply in replies[:3]:
            self.assertGreater(reply["prompt_tokens"], 0)
            self.assertEqual(reply["counts"], [])
        self.assertEqual(replies[3], {"id": "3", "error": "JSONDecodeError",
                                      "error_code": "tool_arguments_json"})
        self.assertEqual(replies[4], {"id": "4", "error": "ValueError",
                                      "error_code": "template_system_position"})
        self.assertNotIn("PRIVATE_SENTINEL", result.stdout)
        self.assertNotIn("Traceback", result.stdout)


if __name__ == "__main__":
    unittest.main(argv=[sys.argv[0]])
