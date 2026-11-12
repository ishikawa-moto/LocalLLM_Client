"""Disposable socket/stdio tests; no Windows relay or model is used."""
import json
import os
from pathlib import Path
import select
import socket
import subprocess
import sys
import tempfile
import time
import unittest
import importlib.util

BRIDGE = Path(__file__).resolve().parents[1] / "bridge" / "model_bridge.py"
REQUEST = b'{"method":"POST","path":"/v1/chat/completions","contentType":"application/json","bodyBase64":"e30="}\n'


class HostStdioBridgeTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.directory.name, "model.sock")
        self.process = subprocess.Popen([sys.executable, "-u", str(BRIDGE), "--socket", self.path, "--host-stdio"],
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        deadline = time.monotonic() + 5
        while not os.path.exists(self.path):
            if self.process.poll() is not None or time.monotonic() > deadline:
                self.fail("bridge did not open socket")
            time.sleep(.02)

    def tearDown(self):
        if self.process.poll() is None:
            self.process.kill()
        self.process.wait(timeout=5)
        for stream in (self.process.stdin, self.process.stdout, self.process.stderr):
            if stream and not stream.closed:
                stream.close()
        self.directory.cleanup()

    def connect(self):
        client = socket.socket(socket.AF_UNIX)
        client.settimeout(5)
        client.connect(self.path)
        return client

    def request(self, client):
        client.sendall(REQUEST)
        self.assertTrue(select.select([self.process.stdout], [], [], 5)[0])
        self.assertEqual(json.loads(self.process.stdout.readline()), json.loads(REQUEST))

    def test_response_and_idle_host_eof(self):
        with self.connect() as client:
            self.request(client)
            self.process.stdin.write(b'{"status":200}\n')
            self.process.stdin.flush()
            self.assertEqual(json.loads(client.recv(4096)), {"status": 200})
        self.process.stdin.close()
        self.assertEqual(self.process.wait(timeout=5), 0)
        self.assertFalse(os.path.exists(self.path))

    def test_client_disconnect_stops_bridge_before_late_reply(self):
        client = self.connect()
        self.request(client)
        client.close()
        self.assertEqual(self.process.wait(timeout=5), 0)
        self.assertFalse(os.path.exists(self.path))

    def test_sequential_replies_are_not_shifted(self):
        for status in (201, 202):
            with self.connect() as client:
                self.request(client)
                self.process.stdin.write(json.dumps({"status": status}).encode() + b"\n")
                self.process.stdin.flush()
                self.assertEqual(json.loads(client.recv(4096)), {"status": status})
            # Wait for the server's one-client cleanup before the next request.
            time.sleep(.1)
        self.process.stdin.close()
        self.assertEqual(self.process.wait(timeout=5), 0)

    def test_memory_tool_frames(self):
        frames = [{"operation":"memory_search","query":"needle"},
                  {"operation":"recall","evidenceHash":"a"*64,"offset":0}]
        for frame in frames:
            with self.connect() as client:
                client.sendall(json.dumps(frame).encode() + b"\n")
                self.assertTrue(select.select([self.process.stdout], [], [], 5)[0])
                self.assertEqual(json.loads(self.process.stdout.readline()), frame)
                self.process.stdin.write(b'{"ok":true,"text":"fixture"}\n')
                self.process.stdin.flush()
                self.assertEqual(json.loads(client.recv(4096)), {"ok":True,"text":"fixture"})
            time.sleep(.1)

    def test_legacy_and_invalid_tool_frames_rejected(self):
        spec=importlib.util.spec_from_file_location("model_bridge",BRIDGE)
        module=importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
        good=b'{"operation":"memory_search","query":"needle"}\n'
        with self.assertRaises(ValueError): module.validate_request(good)
        for bad in (b'{"operation":"memory_search","query":"x","query":"y"}\n',
                    b'{"operation":"recall","evidenceHash":"bad","offset":0}\n',
                    b'{"operation":"recall","evidenceHash":"' + b'a'*64 + b'","offset":true}\n'):
            with self.assertRaises(ValueError): module.validate_request(bad,host_stdio=True)

    def test_host_eof_with_request_in_flight(self):
        with self.connect() as client:
            self.request(client)
            self.process.stdin.close()
            self.assertEqual(self.process.wait(timeout=5), 0)
        self.assertFalse(os.path.exists(self.path))

    def test_unsolicited_response_cannot_be_used_by_next_client(self):
        self.process.stdin.write(b'{"status":200}\n')
        self.process.stdin.flush()
        self.assertEqual(self.process.wait(timeout=5), 0)
        self.assertFalse(os.path.exists(self.path))


if __name__ == "__main__":
    unittest.main()
