"""Disposable PC70 WSL cgroup-v2 lifetime feasibility probe."""
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid

if not sys.platform.startswith('linux'):
    raise RuntimeError('WSL Linux is required')
if os.getuid()==0:
    uid_map=Path('/proc/self/uid_map').read_text().split()
    if len(uid_map)!=3 or uid_map!=['0','1000','1']:
        raise RuntimeError('only the mapped PC70 development user is allowed')
elif os.getuid()!=1000:
    raise RuntimeError('run only as the PC70 WSL development user')
paths=[line[3:] for line in Path('/proc/self/cgroup').read_text().splitlines() if line.startswith('0::/')]
if len(paths)!=1 or not Path(paths[0]).name.startswith('lb-snapshot-probe-') or not paths[0].endswith('.scope'):
    raise RuntimeError('run only inside a fresh delegated probe scope')
parent=Path('/sys/fs/cgroup')/paths[0].lstrip('/')
if not parent.is_dir() or parent.stat().st_uid!=os.getuid():raise RuntimeError('unexpected delegated cgroup parent')
group=parent/('lb-probe-'+str(uuid.uuid4()))
group.mkdir(mode=0o700)
child=None;pidfd=None;attached=False
try:
    # No workload code runs until this supervisor has placed the child in its
    # private cgroup and pinned its process identity.
    code=("import os,subprocess,sys,time; "
          "data=os.read(0,1); "
          "sys.exit(9) if data!=b'1' else None; "
          "open(sys.argv[1],'w').write('isolated evidence') if len(sys.argv)>1 else None; "
          "subprocess.Popen([sys.executable,'-c','import time; time.sleep(30)'],stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL); "
          "print('READY',flush=True);time.sleep(30)")
    child=subprocess.Popen([sys.executable,'-c',code]+sys.argv[1:],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
    pidfd=os.pidfd_open(child.pid)
    (group/'cgroup.procs').write_text(str(child.pid))
    attached=True
    child.stdin.write(b'1');child.stdin.flush();child.stdin.close()
    if child.stdout.readline().strip()!=b'READY':raise RuntimeError('workload did not enter scope')
    if 'populated 1' not in (group/'cgroup.events').read_text():raise RuntimeError('cgroup was not populated')
    (group/'cgroup.kill').write_text('1')
    deadline=time.monotonic()+10
    while 'populated 0' not in (group/'cgroup.events').read_text():
        if time.monotonic()>deadline:raise TimeoutError('cgroup did not become empty')
        time.sleep(.02)
    child.wait(timeout=5)
    print('CGROUP_KILL_AND_EMPTY_OBSERVED')
finally:
    if child and child.poll() is None:
        if attached:
            try:(group/'cgroup.kill').write_text('1')
            except OSError:pass
        if child.poll() is None:child.kill()
        child.wait(timeout=5)
    if pidfd is not None:os.close(pidfd)
    # The kernel permits rmdir only when this exact newly-created cgroup is empty.
    group.rmdir()
