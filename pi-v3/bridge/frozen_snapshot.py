"""Experimental Host-side snapshot; no files are applied.

Host lifecycle integration, recovery from an uncatchable termination, and an
independent security review are still required before production use.
"""
import argparse
import fcntl
import hashlib
import json
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


def _sync_directory(path):
    fd=os.open(path,os.O_RDONLY|os.O_DIRECTORY)
    try: os.fsync(fd)
    finally: os.close(fd)


def _lock_task(task):
    fd=os.open(task/'.snapshot.lock',os.O_RDWR|os.O_CREAT|os.O_NOFOLLOW,0o600)
    try:
        info=os.fstat(fd)
        if not stat.S_ISREG(info.st_mode) or info.st_uid!=os.getuid() or info.st_nlink!=1:
            raise ValueError('unsafe snapshot lock')
        fcntl.flock(fd,fcntl.LOCK_EX|fcntl.LOCK_NB)
        return fd
    except BaseException:
        os.close(fd);raise


def _wait_thawed(group):
    events=group/'cgroup.events';deadline=time.monotonic()+10
    while 'frozen 0' not in events.read_text():
        if time.monotonic()>deadline: raise TimeoutError('task did not thaw')
        time.sleep(.01)


def recover_interrupted(root,task_id):
    """Thaw only a task for which our durable snapshot intent exists.

    Partial archives are retained for inspection. An ambiguous process identity
    or scope fails closed and requires Host intervention.
    """
    if str(uuid.UUID(task_id))!=task_id: raise ValueError('invalid task identity')
    _private_directory(root);task=root/'tasks'/task_id;_private_directory(task)
    lock_fd=_lock_task(task)
    try:
        intent=task/'.snapshot-intent.json'
        try: fd=os.open(intent,os.O_RDONLY|os.O_NOFOLLOW)
        except FileNotFoundError: return False
        with os.fdopen(fd,'rb') as stream:
            info=os.fstat(stream.fileno())
            if not stat.S_ISREG(info.st_mode) or info.st_uid!=os.getuid() or info.st_nlink!=1 or info.st_size>4096:
                raise ValueError('unsafe snapshot intent')
            value=json.load(stream)
        if set(value)!={'task','snapshot','pid','start','group'} or value['task']!=task_id or str(uuid.UUID(value['snapshot']))!=value['snapshot']:
            raise ValueError('invalid snapshot intent')
        uid=os.getuid();expected=f'/user.slice/user-{uid}.slice/user@{uid}.service/app.slice/lb-v3-{task_id}.scope'
        if value['group']!=expected or type(value['pid']) is not int or value['pid']<=1 or type(value['start']) is not int or value['start']<=0:
            raise ValueError('invalid snapshot identity')
        group=Path('/sys/fs/cgroup')/expected.lstrip('/')
        if group.exists():
            try:
                matches=_identity(value['pid'])==value['start'] and _group(value['pid'],task_id)==group
            except (FileNotFoundError,ProcessLookupError,ValueError): matches=False
            # A dead task has no live descendants; do not thaw an ambiguous
            # populated scope if the recorded process identity changed.
            if not matches and 'populated 0' not in (group/'cgroup.events').read_text():
                raise ValueError('snapshot process identity changed in a populated scope')
            freeze=group/'cgroup.freeze'
            if freeze.read_text().strip()=='1': freeze.write_text('0')
            _wait_thawed(group)
        intent.unlink();_sync_directory(task)
        return True
    finally: os.close(lock_fd)


def snapshot(root,task_id,pid,snapshot_id,max_files,max_bytes):
    if str(uuid.UUID(task_id))!=task_id or str(uuid.UUID(snapshot_id))!=snapshot_id or pid<=1:
        raise ValueError('invalid task identity')
    if not 0<max_files<=10000 or not 0<max_bytes<=512*1024*1024:
        raise ValueError('invalid snapshot limits')
    _private_directory(root)
    task=root/'tasks'/task_id
    _private_directory(task)
    lock_fd=_lock_task(task)
    try:
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
    intent=task/'.snapshot-intent.json'; made=False; intent_made=False
    try:
        fd=os.open(intent,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600);intent_made=True
        with os.fdopen(fd,'w',encoding='utf-8') as marker:
            json.dump({'task':task_id,'snapshot':snapshot_id,'pid':pid,'start':identity,
                       'group':'/'+str(group.relative_to('/sys/fs/cgroup'))},marker,separators=(',',':'))
            marker.flush();os.fsync(marker.fileno())
        _sync_directory(task)
        freeze.write_text('1')
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
        try:
            if intent_made:
                if group.exists():
                    if freeze.read_text().strip()=='1': freeze.write_text('0')
                    _wait_thawed(group)
                intent.unlink();_sync_directory(task)
        finally:
            if made:
                try: temporary.unlink()
                except FileNotFoundError: pass


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--root',type=Path,required=True);parser.add_argument('--task',required=True)
    parser.add_argument('--pid',type=int);parser.add_argument('--snapshot')
    parser.add_argument('--max-files',type=int);parser.add_argument('--max-bytes',type=int)
    parser.add_argument('--recover',action='store_true')
    parser.add_argument('--experimental',action='store_true')
    args=parser.parse_args()
    if not args.experimental: parser.error('snapshot helper is not yet approved for production use')
    def terminate(signum,frame): raise KeyboardInterrupt()
    signal.signal(signal.SIGTERM,terminate)
    fields=(args.pid,args.snapshot,args.max_files,args.max_bytes)
    if args.recover:
        if any(value is not None for value in fields): parser.error('recovery does not accept snapshot arguments')
        print('recovered' if recover_interrupted(args.root,args.task) else 'no_pending_intent')
        return
    if any(value is None for value in fields): parser.error('snapshot arguments are required')
    target,digest=snapshot(args.root,args.task,args.pid,args.snapshot,args.max_files,args.max_bytes)
    print(target.name,digest)


if __name__=='__main__': main()
