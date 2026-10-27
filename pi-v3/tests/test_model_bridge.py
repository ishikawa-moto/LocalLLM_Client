import asyncio
import base64
import importlib.util
import json
import os
import tempfile
from pathlib import Path

module_path = Path(__file__).resolve().parent.parent / "bridge" / "model_bridge.py"
spec = importlib.util.spec_from_file_location("model_bridge", module_path)
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)
bridge.MAX_REPLY = 131072


async def main():
    with tempfile.TemporaryDirectory(prefix="localbrain-bridge-test-") as name:
        root = Path(name)
        marker = root / "called"
        relay = root / "localbrain.exe"
        relay.write_text("#!/usr/bin/python3\nimport sys,pathlib,json,base64,time,os\np=pathlib.Path(%r)\nq=json.loads(sys.stdin.readline())\np.write_text('called')\nmode=base64.b64decode(q['bodyBase64'])\nif mode in (b'slow',b'close_stdout'):\n p.write_text(str(os.getpid()))\n if mode == b'close_stdout': os.close(1)\n time.sleep(30)\n p.write_text('finished')\nelif mode == b'big':\n while True: os.write(1,b'x'*65536)\nelse: print('{\"status\":200,\"content_type\":\"application/json\",\"body_base64\":\"e30=\"}')\n" % str(marker))
        relay.chmod(0o700)
        socket_path = root / "model.sock"
        task = asyncio.create_task(bridge.serve(socket_path, str(relay)))
        try:
            for _ in range(100):
                if socket_path.exists(): break
                if task.done(): await task
                await asyncio.sleep(0.01)
            assert socket_path.exists()

            async def send(request):
                reader, writer = await asyncio.open_unix_connection(socket_path)
                writer.write(json.dumps(request).encode() + b"\n")
                await writer.drain()
                reply = json.loads(await reader.readline())
                writer.close()
                await writer.wait_closed()
                return reply

            request = {"method":"POST", "path":"/v1/chat/completions", "contentType":"application/json", "bodyBase64":base64.b64encode(b"{}").decode()}
            assert (await send({**request, "path":"/admin"}))["error"] == "model_bridge_failed"
            assert not marker.exists(), "invalid route reached the host relay"
            assert (await send({**request, "bodyBase64":"!!!!"}))["error"] == "model_bridge_failed"
            assert not marker.exists(), "invalid body reached the host relay"
            assert (await send(request))["status"] == 200
            assert marker.exists()
            assert (await asyncio.wait_for(send({**request, "bodyBase64":base64.b64encode(b"big").decode()}), timeout=5))["error"] == "model_bridge_failed"
            assert (await send(request))["status"] == 200
            for mode in (b"slow", b"close_stdout"):
                marker.write_text("waiting")
                first_reader, first_writer = await asyncio.open_unix_connection(socket_path)
                first_writer.write(json.dumps({**request, "bodyBase64":base64.b64encode(mode).decode()}).encode()+b"\n")
                await first_writer.drain()
                for _ in range(100):
                    if marker.read_text().isdigit(): break
                    await asyncio.sleep(0.01)
                assert marker.read_text().isdigit()
                relay_pid = int(marker.read_text())
                second_reader, second_writer = await asyncio.open_unix_connection(socket_path)
                assert await asyncio.wait_for(second_reader.read(1), timeout=2) == b""
                second_writer.close()
                await second_writer.wait_closed()
                first_writer.close()
                await first_writer.wait_closed()
                for _ in range(100):
                    try: os.kill(relay_pid, 0)
                    except ProcessLookupError: break
                    await asyncio.sleep(0.01)
                else: raise AssertionError("disconnected relay kept running")
        finally:
            task.cancel()
            try: await task
            except asyncio.CancelledError: pass
        assert not socket_path.exists()
        root.chmod(0o755)
        try:
            await bridge.serve(socket_path, str(relay))
            raise AssertionError("non-private socket directory was accepted")
        except ValueError:
            pass
    print("Model bridge route, framing and private-directory checks passed")


asyncio.run(main())
