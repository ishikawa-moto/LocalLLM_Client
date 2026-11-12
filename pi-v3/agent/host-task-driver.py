"""One Host-owned development task, run inside a private user/mount namespace.

The caller owns authorization, model relay, process lifetime and result review.
No output from this driver authorizes applying a candidate.
"""
import argparse
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tarfile
import threading
import time
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "bridge"))
from seed_workspace import seed_workspace


def private(path):
    if not path.is_absolute() or path.resolve(strict=True) != path:
        raise ValueError("private path must be absolute and unlinked")
    info = path.stat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o077:
        raise ValueError("private directory required")


def canonical_uuid(value):
    if str(uuid.UUID(value)) != value:
        raise ValueError("canonical UUID required")
    return value


def prepare(root, task):
    private(root)
    tasks = root / "tasks"
    tasks.mkdir(mode=0o700, exist_ok=True)
    private(tasks)
    directory = tasks / task
    directory.mkdir(mode=0o700)  # Never adopt or overwrite an existing attempt.
    (directory / "workspace").mkdir(mode=0o700)
    (directory / "sessions").mkdir(mode=0o700)


def run(args):
    root, task, generation = args.root, canonical_uuid(args.task), canonical_uuid(args.generation)
    private(root)
    directory = root / "tasks" / task
    for path in (directory, directory / "workspace", directory / "sessions"):
        private(path)
    # Refuse operation outside the caller's root-mapped user namespace.
    mapping = Path('/proc/self/uid_map').read_text().split()
    if os.geteuid() != 0 or len(mapping) != 3 or mapping[0] != '0' or mapping[1] == '0' or mapping[2] != '1':
        raise ValueError("private root-mapped namespace required")
    subprocess.run(['/usr/bin/mount', '--make-rprivate', '/'], check=True, timeout=10)
    workspace = directory / 'workspace'
    subprocess.run(['/usr/bin/mount', '-t', 'tmpfs', '-o', 'size=67108864,mode=0700', 'tmpfs', str(workspace)],
                   check=True, timeout=10)
    subprocess.run(['/usr/bin/mount', '-t', 'tmpfs', '-o', 'size=16777216,mode=0700', 'tmpfs',
                    str(directory / 'sessions')], check=True, timeout=10)
    with open(args.manifest, 'rb') as source:
        manifest = json.load(source)
    with open(args.archive, 'rb') as source:
        seed_workspace(source, args.archive_hash, manifest, workspace, 128 * 1024 * 1024)
    scope = f'lb-v3-{task}-{generation}.scope'
    launch = ['/usr/bin/systemd-run', '--user', '--scope', '--quiet', '--unit', scope,
              '--property=MemoryMax=1073741824', '--property=TasksMax=128', '--property=CPUQuota=200%',
              '/usr/bin/python3', str(Path(__file__).with_name('run-isolated.py')),
              '--root', str(root), '--task', task, '--max-workspace-bytes', '67108864',
              '--max-memory-bytes', '1073741824', '--max-processes', '128', '--max-cpu-percent', '200']
    child = subprocess.Popen(launch, stdin=subprocess.PIPE, stdout=sys.stdout.buffer, stderr=sys.stderr.buffer)
    failed = threading.Event()
    group = ''
    deadline = time.monotonic() + 10
    try:
        while time.monotonic() < deadline and child.poll() is None:
            probe = subprocess.run(['/usr/bin/systemctl', '--user', 'show', '--property=ControlGroup',
                                    '--value', '--', scope], capture_output=True, text=True, timeout=3)
            group = probe.stdout.strip() if probe.returncode == 0 else ''
            if group:
                break
            time.sleep(.05)
    except BaseException:
        subprocess.run(['/usr/bin/systemctl', '--user', 'stop', '--', scope], timeout=20, check=False)
        raise
    if not group.startswith('/user.slice/') or '..' in Path(group).parts:
        subprocess.run(['/usr/bin/systemctl', '--user', 'stop', '--', scope], timeout=20, check=False)
        raise RuntimeError('scope cgroup identity unavailable')

    def forward_input():
        try:
            while True:
                data = os.read(sys.stdin.fileno(), 65536)
                if not data:
                    failed.set()
                    subprocess.run(['/usr/bin/systemctl', '--user', 'stop', '--', scope], timeout=20, check=True)
                    return
                child.stdin.write(data)
                child.stdin.flush()
        except (OSError, subprocess.SubprocessError):
            failed.set()

    threading.Thread(target=forward_input, daemon=True).start()
    child.wait()
    # The Host must request stop. A command exit alone is not proof that every
    # descendant is gone: only an inactive scope with no remaining cgroup is accepted.
    group_path = Path('/sys/fs/cgroup') / group.lstrip('/')
    deadline = time.monotonic() + 10
    state = reported_group = populated = 'unknown'
    confirmed = False
    while time.monotonic() < deadline:
        query = subprocess.run(['/usr/bin/systemctl', '--user', 'show',
                                '--property=ActiveState', '--property=ControlGroup', '--', scope],
                               check=False, capture_output=True, text=True, timeout=3)
        values = dict(line.split('=', 1) for line in query.stdout.splitlines() if '=' in line)
        state = values.get('ActiveState', 'unknown')
        reported_group = values.get('ControlGroup', '')
        try:
            lines = (group_path / 'cgroup.events').read_text().splitlines()
            event_values = dict(line.split(' ', 1) for line in lines if ' ' in line)
            populated = event_values.get('populated', 'unknown')
        except FileNotFoundError:
            populated = 'unknown' if group_path.exists() else 'removed'
        if (not failed.is_set() and query.returncode in (0, 1) and state in ('inactive', 'failed') and
                reported_group in ('', group) and populated in ('0', 'removed')):
            confirmed = True
            break
        time.sleep(.1)
    if not confirmed:
        raise RuntimeError(f'scope termination unknown: state={state}, controlGroupPresent={bool(reported_group)}, populated={populated}')
    output = directory / 'candidate.tar'
    paths = []
    for base, dirs, files in os.walk(workspace, followlinks=False):
        for name in sorted(dirs + files):
            item = Path(base) / name
            info = item.lstat()
            if not (stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode)):
                raise ValueError('candidate contains a non-regular entry')
            paths.append(item)
            if len(paths) > 10000:
                raise ValueError('candidate entry limit')
    # The workspace itself is bounded. Precompute an upper bound before writing
    # outside that mount, including tar headers and final blocking padding.
    size = sum(p.lstat().st_size for p in paths if p.is_file())
    metadata_budget = 0
    for entry in paths:
        name_bytes = len(str(entry.relative_to(workspace)).encode('utf-8'))
        if name_bytes > 4096:
            raise ValueError('candidate path exceeds budget')
        metadata_budget += 2048 + name_bytes
    if size + metadata_budget + 10240 > 48 * 1024 * 1024:
        raise ValueError('candidate archive exceeds budget')
    with tarfile.open(output, 'x', format=tarfile.PAX_FORMAT, dereference=False) as archive:
        for entry in paths:
            info = entry.lstat()
            record = tarfile.TarInfo(str(entry.relative_to(workspace)))
            record.uid = record.gid = record.mtime = 0
            record.uname = record.gname = ''
            record.mode = 0o700 if stat.S_ISDIR(info.st_mode) or info.st_mode & 0o111 else 0o600
            if stat.S_ISDIR(info.st_mode):
                record.type = tarfile.DIRTYPE
                archive.addfile(record)
            else:
                record.size = info.st_size
                with entry.open('rb') as data:
                    archive.addfile(record, data)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('mode', choices=('prepare', 'run'))
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--task', required=True)
    parser.add_argument('--generation')
    parser.add_argument('--archive', type=Path)
    parser.add_argument('--manifest', type=Path)
    parser.add_argument('--archive-hash')
    args = parser.parse_args()
    canonical_uuid(args.task)
    if args.mode == 'prepare':
        prepare(args.root, args.task)
    else:
        if not all((args.generation, args.archive, args.manifest, args.archive_hash)):
            parser.error('run requires generation, archive, manifest and archive hash')
        run(args)


if __name__ == '__main__':
    main()
