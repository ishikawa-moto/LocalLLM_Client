"""Experimental Host-side snapshot; no files are applied.

Host lifecycle integration, recovery from an uncatchable termination, and an
independent security review are still required before production use.
"""
import argparse
import fcntl
import hashlib
import os
from pathlib import Path
import signal
import stat
import time
import uuid

from workspace_archive import archive_workspace


def _identity(pid):
    proc=Path('/proc')/str(pid)
    text=(proc/'stat').read_text()
    return int(text.rsplit(')',1)[1].split()[19])


def _group(pid,task_id):
    lines=(Path('/proc')/str(pid)/'cgroup').read_text().splitlines()
    paths=[line[3:] for line in lines if line.startswith('0::/')]
    uid=os.getuid()
    expected=f'/user.slice/user-{uid}.slice/user@{uid}.service/app.slice/lb-v3-{task_id}.scope'
    if paths!=[expected]: raise ValueError('process is not in the expected task scope')
    return Path('/sys/fs/cgroup')/expected.lstrip('/')


def _private_directory(path):
    if not path.is_absolute() or path.resolve(strict=True)!=path: raise ValueError('linked or relative task directory')
    info=path.stat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid!=os.getuid() or info.st_mode & 0o077:
        raise ValueError('task directory is not private')


def snapshot(root,task_id,pid,snapshot_id,max_files,max_bytes):
    if str(uuid.UUID(task_id))!=task_id or str(uuid.UUID(snapshot_id))!=snapshot_id or pid<=1:
        raise ValueError('invalid task identity')
    if not 0<max_files<=10000 or not 0<max_bytes<=512*1024*1024:
        raise ValueError('invalid snapshot limits')
    _private_directory(root)
    task=root/'tasks'/task_id
    _private_directory(task)
    lock_path=task/'.snapshot.lock'
    lock_fd=os.open(lock_path,os.O_RDWR|os.O_CREAT|getattr(os,'O_NOFOLLOW',0),0o600)
    try:
        lock_info=os.fstat(lock_fd)
        if not stat.S_ISREG(lock_info.st_mode) or lock_info.st_uid!=os.getuid() or lock_info.st_nlink!=1:
            raise ValueError('unsafe snapshot lock')
        fcntl.flock(lock_fd,fcntl.LOCK_EX|fcntl.LOCK_NB)
        return _snapshot_locked(task,task_id,pid,snapshot_id,max_files,max_bytes)
    finally:
        os.close(lock_fd)


def _snapshot_locked(task,task_id,pid,snapshot_id,max_files,max_bytes):
    group=_group(pid,task_id); identity=_identity(pid)
    freeze=group/'cgroup.freeze'; events=group/'cgroup.events'
    if freeze.read_text().strip()!='0' or 'frozen 0' not in events.read_text():
        raise ValueError('task was already frozen or is unavailable')
    temporary=task/('.snapshot-'+snapshot_id+'.partial')
    target=task/('snapshot-'+snapshot_id+'.tar')
    frozen=False; made=False
    try:
        freeze.write_text('1');frozen=True
        deadline=time.monotonic()+10
        while 'frozen 1' not in events.read_text():
            if time.monotonic()>deadline: raise TimeoutError('task did not freeze')
            time.sleep(.01)
        if _identity(pid)!=identity or _group(pid,task_id)!=group: raise ValueError('task process changed')
        workspace=Path('/proc')/str(pid)/'root/workspace'
        flags=os.O_WRONLY|os.O_CREAT|os.O_EXCL|getattr(os,'O_NOFOLLOW',0)
        fd=os.open(temporary,flags,0o600);made=True
        with os.fdopen(fd,'wb') as output:
            archive_workspace(workspace,output,max_files,max_bytes)
            output.flush();os.fsync(output.fileno())
        if _identity(pid)!=identity or _group(pid,task_id)!=group: raise ValueError('task process changed')
        with temporary.open('rb') as stream: digest=hashlib.file_digest(stream,'sha256').hexdigest()
        os.link(temporary,target,follow_symlinks=False)
        return target,digest
    finally:
        if frozen: freeze.write_text('0')
        if made:
            try: temporary.unlink()
            except FileNotFoundError: pass


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--root',type=Path,required=True);parser.add_argument('--task',required=True)
    parser.add_argument('--pid',type=int,required=True);parser.add_argument('--snapshot',required=True)
    parser.add_argument('--max-files',type=int,required=True);parser.add_argument('--max-bytes',type=int,required=True)
    parser.add_argument('--experimental',action='store_true')
    args=parser.parse_args()
    if not args.experimental: parser.error('snapshot helper is not yet approved for production use')
    def terminate(signum,frame): raise KeyboardInterrupt()
    signal.signal(signal.SIGTERM,terminate)
    target,digest=snapshot(args.root,args.task,args.pid,args.snapshot,args.max_files,args.max_bytes)
    print(target.name,digest)


if __name__=='__main__': main()
