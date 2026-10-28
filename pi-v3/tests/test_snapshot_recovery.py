import json
import os
from pathlib import Path
import sys
import tempfile
import uuid
if not sys.platform.startswith('linux'):
    print('Snapshot recovery requires Linux; skipped')
    raise SystemExit(0)
import fcntl
sys.path.insert(0,str(Path(__file__).resolve().parent.parent/'bridge'))
import frozen_snapshot as candidate

with tempfile.TemporaryDirectory(prefix='lb-recovery-') as name:
    root=Path(name);task_id=str(uuid.uuid4());task=root/'tasks'/task_id
    task.mkdir(parents=True,mode=0o700)
    marker=task/'.snapshot-intent.json'
    snapshot_id=str(uuid.uuid4());uid=os.getuid()
    expected=f'/user.slice/user-{uid}.slice/user@{uid}.service/app.slice/lb-v3-{task_id}.scope'
    record={'task':task_id,'snapshot':snapshot_id,'pid':os.getpid(),'start':candidate._identity(os.getpid()),'group':expected}
    assert candidate.recover_interrupted(root,task_id) is False
    marker.write_text(json.dumps({**record,'group':'/wrong'}),encoding='utf-8')
    try: candidate.recover_interrupted(root,task_id)
    except ValueError: pass
    else: raise AssertionError('wrong scope accepted')
    assert marker.exists()
    marker.write_text(json.dumps(record),encoding='utf-8')
    with (task/'.snapshot.lock').open('a+b') as lock:
        fcntl.flock(lock,fcntl.LOCK_EX)
        try: candidate.recover_interrupted(root,task_id)
        except BlockingIOError: pass
        else: raise AssertionError('concurrent recovery accepted')
        assert marker.exists()
        fcntl.flock(lock,fcntl.LOCK_UN)
    assert candidate.recover_interrupted(root,task_id) is True
    assert not marker.exists()
print('Snapshot recovery intent, scope and concurrency guards passed')
