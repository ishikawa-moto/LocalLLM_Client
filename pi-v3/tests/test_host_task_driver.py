"""Disposable Pi get_state/stop/capture fixture. Requires an explicit private v3 root.

No inference request is made. The newly created task directory is retained.
"""
import hashlib
import io
import json
import os
from pathlib import Path
import select
import socket
import subprocess
import sys
import tarfile
import tempfile
import time
import uuid

if len(sys.argv) not in (2, 3) or (len(sys.argv) == 3 and sys.argv[2] not in ('normal', 'host_stdin_eof', 'driver_killed')):
    raise SystemExit('usage: test_host_task_driver.py /absolute/private/v3/root [normal|host_stdin_eof|driver_killed]')
root = Path(sys.argv[1])
case = sys.argv[2] if len(sys.argv) == 3 else 'normal'
driver = Path(__file__).resolve().parents[1] / 'agent' / 'host-task-driver.py'
task, generation = str(uuid.uuid4()), str(uuid.uuid4())
directory = root / 'tasks' / task
subprocess.run([sys.executable, str(driver), 'prepare', '--root', str(root), '--task', task], check=True)
with tempfile.TemporaryDirectory(prefix='lb-v3-driver-input-') as tmp:
    tmp = Path(tmp)
    archive_path, manifest_path = tmp / 'input.tar', tmp / 'manifest.json'
    payload = b'original\n'
    names = ['sample.txt', '日本語' * 14 + '.txt']
    with tarfile.open(archive_path, 'w', format=tarfile.PAX_FORMAT) as archive:
        for name in names:
            entry = tarfile.TarInfo(name)
            entry.size = len(payload)
            entry.mode = 0o600
            archive.addfile(entry, io.BytesIO(payload))
    digest = hashlib.sha256(archive_path.read_bytes()).hexdigest()
    manifest_path.write_text(json.dumps([{'name': name, 'directory': False,
        'size': len(payload), 'sha256': hashlib.sha256(payload).hexdigest(), 'executable': False} for name in names]))
    with socket.socket(socket.AF_UNIX) as model:
        model.bind(str(directory / 'model.sock'))
        os.chmod(directory / 'model.sock', 0o600)
        model.listen(1)
        command = ['/usr/bin/unshare', '--user', '--map-root-user', '--mount', '--', sys.executable,
                   '-u', str(driver), 'run', '--root', str(root), '--task', task, '--generation', generation,
                   '--archive', str(archive_path), '--manifest', str(manifest_path), '--archive-hash', digest]
        process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        scope = f'lb-v3-{task}-{generation}.scope'
        try:
            process.stdin.write(b'{"id":"fixture-state","type":"get_state"}\n')
            process.stdin.flush()
            deadline = time.monotonic() + 30
            found = False
            while time.monotonic() < deadline:
                if select.select([process.stdout], [], [], .2)[0]:
                    line = process.stdout.readline()
                    if not line:
                        break
                    value = json.loads(line)
                    if value.get('id') == 'fixture-state':
                        if not value.get('success'):
                            raise RuntimeError('Pi rejected get_state')
                        found = True
                        break
                if process.poll() is not None:
                    break
            if not found:
                raise RuntimeError('Pi get_state did not succeed')
            group = subprocess.run(['/usr/bin/systemctl', '--user', 'show', '--property=ControlGroup',
                                    '--value', '--', scope], capture_output=True, text=True, check=True).stdout.strip()
            if not group.startswith('/user.slice/') or '..' in Path(group).parts:
                raise RuntimeError('fixture scope identity unavailable')
            group_path = Path('/sys/fs/cgroup') / group.lstrip('/')
            if case != 'normal':
                if case == 'host_stdin_eof':
                    process.stdin.close()
                    if process.wait(timeout=30) == 0:
                        raise RuntimeError('EOF incorrectly reported a successful candidate')
                else:
                    process.kill()
                    process.wait(timeout=10)
                if (directory / 'candidate.tar').exists():
                    raise RuntimeError('abnormal run produced a candidate')
                deadline = time.monotonic() + 10
                populated = 'unknown'
                while time.monotonic() < deadline:
                    try:
                        events = dict(line.split(' ', 1) for line in (group_path / 'cgroup.events').read_text().splitlines())
                        populated = events.get('populated', 'unknown')
                    except FileNotFoundError:
                        populated = 'unknown' if group_path.exists() else 'removed'
                    if populated in ('0', 'removed'):
                        break
                    time.sleep(.1)
                if populated not in ('0', 'removed'):
                    subprocess.run(['/usr/bin/systemctl', '--user', 'stop', '--', scope], check=False, timeout=20)
                    raise RuntimeError(f'{case}: scope remained populated={populated}; stop requested, outcome unknown')
                print(f'DRIVER_{case.upper()}_SCOPE_EMPTY_CANDIDATE_ABSENT', task, flush=True)
                raise SystemExit(0)
            subprocess.run(['/usr/bin/systemctl', '--user', 'stop', '--', scope], check=True, timeout=20)
            if process.wait(timeout=30) != 0:
                raise RuntimeError('driver did not confirm stop and capture: ' + process.stderr.read().decode(errors='replace'))
            with tarfile.open(directory / 'candidate.tar') as candidate:
                for name in names:
                    if candidate.extractfile(name).read() != payload:
                        raise RuntimeError('candidate content changed')
                pax = [item for item in candidate if 'path' in item.pax_headers]
                if len(pax) != 1 or pax[0].name != names[1] or set(pax[0].pax_headers) != {'path'}:
                    raise RuntimeError('expected exactly one long-path PAX record')
            print('DRIVER_GET_STATE_SCOPE_STOP_CANDIDATE_OK', task, flush=True)
        finally:
            if process.poll() is None:
                subprocess.run(['/usr/bin/systemctl', '--user', 'stop', '--', scope], timeout=20, check=False)
                process.kill()
                process.wait(timeout=5)
