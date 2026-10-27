"""One-task Unix socket bridge to the Windows Host's existing model relay.

The caller must create a private per-task directory and run this outside the
Pi sandbox. Only the socket is mounted into that sandbox. No model or TLS
credentials are held here; the Windows Host retains its existing checks.
Clients must keep their write side open until the response completes. Closing
it is the explicit cancellation signal for this one-request protocol.
"""
import argparse
import asyncio
import base64
import binascii
import json
import os
import signal
import stat
from pathlib import Path

MAX_BODY = 8 * 1024 * 1024
MAX_LINE = 12 * 1024 * 1024
MAX_REPLY = 48 * 1024 * 1024
DEADLINE = 600
REQUEST_DEADLINE = 10


def validate_request(line):
    if not line.endswith(b"\n") or len(line) > MAX_LINE:
        raise ValueError("invalid request framing")
    request = json.loads(line)
    if not isinstance(request, dict) or set(request) != {"method", "path", "contentType", "bodyBase64"}:
        raise ValueError("invalid request")
    if request["method"] != "POST" or request["path"] != "/v1/chat/completions" or request["contentType"] != "application/json":
        raise ValueError("invalid route")
    try:
        body = base64.b64decode(request["bodyBase64"], validate=True)
    except (ValueError, TypeError, binascii.Error):
        raise ValueError("invalid body") from None
    if not 0 < len(body) <= MAX_BODY:
        raise ValueError("invalid body size")
    return json.dumps(request, separators=(",", ":")).encode() + b"\n"


async def read_limited(reader, limit):
    data = bytearray()
    while True:
        chunk = await reader.read(min(65536, limit + 1 - len(data)))
        if not chunk:
            return bytes(data)
        data.extend(chunk)
        if len(data) > limit:
            raise ValueError("relay output too large")


async def discard(reader):
    while await reader.read(65536):
        pass


async def handle(reader, writer, relay):
    process = None
    output_task = disconnect_task = None
    try:
        async with asyncio.timeout(DEADLINE):
            line = await asyncio.wait_for(reader.readline(), timeout=REQUEST_DEADLINE)
            request = validate_request(line)
            process = await asyncio.create_subprocess_exec(relay, "model-relay", stdin=asyncio.subprocess.PIPE,
                                                           stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL)
            async def run_relay():
                process.stdin.write(request)
                await process.stdin.drain()
                process.stdin.close()
                result = await read_limited(process.stdout, MAX_REPLY)
                if await process.wait() != 0 or not result.endswith(b"\n") or result.count(b"\n") != 1:
                    raise ValueError("relay failed")
                return result

            output_task = asyncio.create_task(run_relay())
            disconnect_task = asyncio.create_task(reader.read(1))
            done, _ = await asyncio.wait((output_task, disconnect_task), return_when=asyncio.FIRST_COMPLETED)
            if disconnect_task in done:
                raise ConnectionError("client disconnected or sent more than one request")
            result = await output_task
            disconnect_task.cancel()
            parsed = json.loads(result)
            if not isinstance(parsed, dict):
                raise ValueError("invalid relay reply")
            writer.write(result)
            await writer.drain()
    except (Exception, asyncio.CancelledError):
        if output_task is not None and not output_task.done():
            output_task.cancel()
            await asyncio.gather(output_task, return_exceptions=True)
        if process is not None and process.returncode is None:
            try:
                process.kill()
            except ProcessLookupError:
                pass
        if process is not None:
            try:
                await asyncio.wait_for(asyncio.gather(discard(process.stdout), process.wait()), timeout=2)
            except (TimeoutError, OSError):
                # Close inherited pipes too if a descendant retains stdout.
                process._transport.close()
        try:
            writer.write(b'{"error":"model_bridge_failed"}\n')
        except (ConnectionError, OSError):
            pass
    finally:
        pending = [task for task in (output_task, disconnect_task) if task is not None]
        for task in pending:
            if not task.done():
                task.cancel()
        if pending:
            await asyncio.gather(*pending, return_exceptions=True)
        writer.close()
        try:
            await asyncio.wait_for(writer.wait_closed(), timeout=1)
        except (ConnectionError, OSError, TimeoutError):
            writer.transport.abort()


async def serve(socket_path, relay):
    parent = socket_path.parent
    info = parent.stat(follow_symlinks=False)
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o077:
        raise ValueError("socket directory must be private and owned by the current user")
    if socket_path.exists() or socket_path.is_symlink():
        raise ValueError("socket path already exists")
    if not os.path.isabs(relay) or Path(relay).name.lower() != "localbrain.exe":
        raise ValueError("invalid relay executable")
    clients = set()
    def accept(reader, writer):
        # Reject concurrent requests immediately; the Gateway owns inference queuing.
        if clients:
            writer.close()
            return
        task = asyncio.create_task(handle(reader, writer, relay))
        clients.add(task)
        task.add_done_callback(clients.discard)

    server = await asyncio.start_unix_server(accept, path=str(socket_path), limit=MAX_LINE + 1)
    socket_identity = socket_path.lstat()
    try:
        os.chmod(socket_path, 0o600)
        await asyncio.Event().wait()
    finally:
        server.close()
        for task in list(clients):
            task.cancel()
        if clients:
            await asyncio.gather(*clients, return_exceptions=True)
        await server.wait_closed()
        try:
            current = socket_path.lstat()
            if stat.S_ISSOCK(current.st_mode) and (current.st_dev, current.st_ino) == (socket_identity.st_dev, socket_identity.st_ino):
                socket_path.unlink()
        except FileNotFoundError:
            pass


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--socket", type=Path, required=True)
    parser.add_argument("--relay", required=True)
    args = parser.parse_args()
    if not args.socket.is_absolute():
        parser.error("socket path must be absolute")
    async def run():
        loop = asyncio.get_running_loop()
        task = asyncio.create_task(serve(args.socket, args.relay))
        loop.add_signal_handler(signal.SIGTERM, task.cancel)
        try:
            await task
        except asyncio.CancelledError:
            pass
        finally:
            loop.remove_signal_handler(signal.SIGTERM)

    asyncio.run(run())


if __name__ == "__main__":
    main()
