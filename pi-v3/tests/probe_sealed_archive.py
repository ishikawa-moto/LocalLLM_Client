"""Disposable feasibility probe; does not authorize a production snapshot."""
import ctypes
import io
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile

if sys.platform!='linux' or os.getuid()!=0 or Path('/proc/self/uid_map').read_text().split()!=['0','1000','1']:
    raise RuntimeError('requires a separate root-mapped PC70 user namespace')
here=Path(__file__).resolve().parent
sys.path.insert(0,str(here.parent/'bridge'))
from workspace_archive import archive_workspace

with tempfile.TemporaryDirectory(prefix='lb-stopped-archive-') as name:
    mount=Path(name)/'workspace';mount.mkdir()
    subprocess.run(['mount','-t','tmpfs','-o','size=8388608,mode=0700,nosuid,nodev','tmpfs',str(mount)],check=True)
    try:
        subprocess.run([sys.executable,str(here/'probe_cgroup_stop.py'),str(mount/'evidence.txt')],check=True)
        libc=ctypes.CDLL(None,use_errno=True)
        if libc.mount(None,os.fsencode(mount),None,1|2|4|32,None)!=0:
            raise OSError(ctypes.get_errno(),'filesystem remount failed')
        archive=io.BytesIO();archive_workspace(mount,archive,10,1024)
        archive.seek(0)
        with tarfile.open(fileobj=archive) as value:
            if value.extractfile('evidence.txt').read()!=b'isolated evidence':raise RuntimeError('archive mismatch')
        try:(mount/'evidence.txt').write_text('changed')
        except OSError:pass
        else:raise RuntimeError('filesystem is still writable')
        print('STOP_SEAL_ARCHIVE_OK')
    finally:
        subprocess.run(['umount',str(mount)],check=True)
