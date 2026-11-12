import hashlib
import io
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
if not sys.platform.startswith('linux'):
    print('Workspace seeding requires Linux; skipped')
    raise SystemExit(0)
uid_map=Path('/proc/self/uid_map').read_text().split()
if os.geteuid()!=0 or len(uid_map)!=3 or uid_map[0]!='0' or uid_map[1]=='0' or uid_map[2]!='1':
    raise RuntimeError('run only inside a separate root-mapped user and mount namespace')
sys.path.insert(0,str(Path(__file__).resolve().parent.parent/'bridge'))
import seed_workspace


def make_archive(kind=tarfile.REGTYPE):
    output=io.BytesIO()
    with tarfile.open(fileobj=output,mode='w') as archive:
        item=tarfile.TarInfo('src/code.txt');item.type=kind
        if kind==tarfile.REGTYPE:
            item.size=2;archive.addfile(item,io.BytesIO(b'ok'))
        else:
            item.linkname='../private';archive.addfile(item)
    return output.getvalue()


def record(name='src/code.txt',digest=None):
    return {'name':name,'directory':False,'size':2,'sha256':digest or hashlib.sha256(b'ok').hexdigest(),'executable':False}


with tempfile.TemporaryDirectory(prefix='lb-seed-') as name:
    base=Path(name)
    for index in range(4):
        workspace=base/str(index);workspace.mkdir()
        subprocess.run(['mount','-t','tmpfs','-o','size=8388608,mode=0700','tmpfs',str(workspace)],check=True)
        try:
            raw=make_archive(tarfile.SYMTYPE if index==3 else tarfile.REGTYPE)
            digest=hashlib.sha256(raw).hexdigest()
            manifest=[record(name='../private' if index==2 else 'src/code.txt')]
            try:
                seed_workspace.seed_workspace(io.BytesIO(raw),'0'*64 if index==1 else digest,manifest,workspace,100000)
            except ValueError:
                if index==0: raise
            else:
                if index!=0: raise AssertionError('unsafe archive accepted')
                assert (workspace/'src/code.txt').read_bytes()==b'ok'
        finally:
            subprocess.run(['umount',str(workspace)],check=True)
print('Host-verified workspace seeding fixture passed')
