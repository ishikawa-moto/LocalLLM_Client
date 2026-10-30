import importlib.util
import tempfile
from pathlib import Path

path = Path(__file__).resolve().parent.parent / "agent" / "run-isolated.py"
spec = importlib.util.spec_from_file_location("isolated_launcher", path)
launcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(launcher)

with tempfile.TemporaryDirectory(prefix="localbrain-launcher-test-") as name:
    root = Path(name)
    workspace = root / "workspace"
    workspace.mkdir()
    try:
        launcher.verify_resource_limits(workspace, 100_000_000, 1_000_000_000, 100, 200)
        raise AssertionError("unbounded workspace accepted")
    except ValueError:
        pass
    private = root / "sessions"
    private.mkdir(mode=0o700)
    launcher.checked_private_directory(private)
    private.chmod(0o755)
    try:
        launcher.checked_private_directory(private)
        raise AssertionError("non-private session directory accepted")
    except ValueError:
        pass
    link = root / "linked"
    link.symlink_to(workspace, target_is_directory=True)
    try:
        launcher.checked(link, "dir")
        raise AssertionError("linked workspace accepted")
    except ValueError:
        pass
print("Launcher rejects an unbounded or linked workspace")
