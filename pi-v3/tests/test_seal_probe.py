import os
import ctypes
import subprocess
import sys
import tempfile
from pathlib import Path

if not sys.platform.startswith('linux'):
    raise RuntimeError('WSL Linux is required')
uid_map=Path('/proc/self/uid_map').read_text().split()
if os.geteuid()!=0 or len(uid_map)!=3 or uid_map[0]!='0' or uid_map[1]=='0' or uid_map[2]!='1':
    raise RuntimeError('run only in a separate root-mapped user and mount namespace')

with tempfile.TemporaryDirectory(prefix='lb-seal-probe-') as name:
    root=Path(name)/'mount';root.mkdir();alias=Path(name)/'alias';alias.mkdir()
    subprocess.run(['mount','-t','tmpfs','-o','size=8388608,mode=0700,nosuid,nodev','tmpfs',str(root)],check=True)
    bound=False
    try:
        (root/'evidence').write_text('initial',encoding='utf-8')
        subprocess.run(['mount','--bind',str(root),str(alias)],check=True);bound=True
        libc=ctypes.CDLL(None,use_errno=True)
        if libc.mount(None,os.fsencode(root),None,1|2|4|32,None)!=0:
            raise OSError(ctypes.get_errno(),'filesystem remount failed')
        for path in (root,alias):
            try:
                (path/'evidence').write_text('changed',encoding='utf-8')
            except OSError:
                continue
            raise RuntimeError('sealed workspace or its alias remained writable')
        print('READ_ONLY_REMOUNT_REJECTED_ALIAS_WRITES')
    finally:
        if bound:subprocess.run(['umount',str(alias)],check=True)
        subprocess.run(['umount',str(root)],check=True)
