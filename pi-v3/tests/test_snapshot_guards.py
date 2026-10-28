import os
from pathlib import Path
import sys
import tempfile
import unittest
import uuid

sys.path.insert(0,str(Path(__file__).resolve().parent.parent/'bridge'))
if sys.platform.startswith('linux'):
    import frozen_snapshot


@unittest.skipUnless(sys.platform.startswith('linux'),'cgroup snapshots require Linux')
class SnapshotGuards(unittest.TestCase):
    def test_rejects_process_outside_task_scope_and_nonprivate_root(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parent) as name:
            root=Path(name); task=str(uuid.uuid4()); snap=str(uuid.uuid4())
            (root/'tasks'/task).mkdir(parents=True,mode=0o700)
            with self.assertRaises(ValueError):
                frozen_snapshot.snapshot(root,task,os.getpid(),snap,10,1024)
            root.chmod(0o755)
            try:
                with self.assertRaises(ValueError):
                    frozen_snapshot.snapshot(root,task,os.getpid(),snap,10,1024)
            finally:
                root.chmod(0o700)


if __name__=='__main__': unittest.main()
