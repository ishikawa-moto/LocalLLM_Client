"""Launch one Pi RPC process in a Host-prepared, per-task WSL sandbox.

This launcher does not create or populate workspaces, authorize tasks, or
publish results. The Windows Host must do those things before invoking it.
"""
import argparse
import json
import os
import stat
import uuid
from pathlib import Path


def verify_resource_limits(workspace, max_workspace, max_memory, max_processes, max_cpu):
    # The Host must provision a dedicated, bounded filesystem and a systemd
    # scope before launching. Refuse to start if either boundary is missing.
    if not os.path.ismount(workspace):
        raise ValueError("workspace must be a dedicated filesystem mount")
    fs = os.statvfs(workspace)
    if not 0 < fs.f_blocks * fs.f_frsize <= max_workspace:
        raise ValueError("workspace filesystem exceeds its size budget")
    entries = Path("/proc/self/cgroup").read_text().splitlines()
    paths = [line[3:] for line in entries if line.startswith("0::/")]
    if len(paths) != 1 or ".." in Path(paths[0]).parts:
        raise ValueError("cgroup v2 membership unavailable")
    group = Path("/sys/fs/cgroup") / paths[0].lstrip("/")
    memory = (group / "memory.max").read_text().strip()
    processes = (group / "pids.max").read_text().strip()
    cpu = (group / "cpu.max").read_text().split()
    if not memory.isdecimal() or not 0 < int(memory) <= max_memory:
        raise ValueError("memory cgroup limit unavailable or too high")
    if not processes.isdecimal() or not 0 < int(processes) <= max_processes:
        raise ValueError("process cgroup limit unavailable or too high")
    if len(cpu) != 2 or not all(x.isdecimal() for x in cpu) or int(cpu[1]) == 0 or int(cpu[0]) * 100 > max_cpu * int(cpu[1]):
        raise ValueError("CPU cgroup limit unavailable or too high")


def checked(path, kind):
    if not path.is_absolute() or path.resolve(strict=True) != path:
        raise ValueError("path must be absolute and contain no symbolic links")
    info = path.stat()
    if kind == "dir" and not stat.S_ISDIR(info.st_mode):
        raise ValueError("expected directory")
    if kind == "file" and not stat.S_ISREG(info.st_mode):
        raise ValueError("expected regular file")
    if kind == "socket" and not stat.S_ISSOCK(info.st_mode):
        raise ValueError("expected socket")
    return info


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True, type=Path, help="Host-controlled private WSL v3 root")
    parser.add_argument("--task", required=True, help="Host-issued UUID")
    parser.add_argument("--max-workspace-bytes", type=int, required=True)
    parser.add_argument("--max-memory-bytes", type=int, required=True)
    parser.add_argument("--max-processes", type=int, required=True)
    parser.add_argument("--max-cpu-percent", type=int, required=True)
    args = parser.parse_args()
    if min(args.max_workspace_bytes, args.max_memory_bytes, args.max_processes, args.max_cpu_percent) <= 0:
        parser.error("resource limits must be positive")
    task_id = str(uuid.UUID(args.task))
    if task_id != args.task:
        parser.error("task must be a canonical UUID")
    root = args.root
    root_info = checked(root, "dir")
    task = root / "tasks" / task_id
    task_info = checked(task, "dir")
    for info in (root_info, task_info):
        if info.st_uid != os.getuid() or info.st_mode & 0o077:
            raise ValueError("Host root and task directory must be private")
    workspace = task / "workspace"
    socket = task / "model.sock"
    runtime = root / "runtime"
    node = runtime / "node"
    pi = runtime / "pi"
    bwrap = runtime / "bwrap"
    provider = pi / "localbrain-provider.mjs"
    settings = pi / "localbrain-settings.json"
    loop_guard = pi / "localbrain-loop.mjs"
    for directory in (workspace, runtime, node, pi): checked(directory, "dir")
    verify_resource_limits(workspace, args.max_workspace_bytes, args.max_memory_bytes, args.max_processes, args.max_cpu_percent)
    for regular in (bwrap, provider, settings, loop_guard, node / "bin/node", pi / "node_modules/@earendil-works/pi-coding-agent/package.json"):
        checked(regular, "file")
    socket_info = checked(socket, "socket")
    if socket_info.st_uid != os.getuid() or socket_info.st_mode & 0o077:
        raise ValueError("model socket must be private")
    package = json.loads((pi / "node_modules/@earendil-works/pi-coding-agent/package.json").read_text())
    if package.get("version") != "1.4.0":
        raise ValueError("Pi version must be 1.4.0")
    argv = [str(bwrap), "--unshare-all", "--die-with-parent", "--new-session", "--cap-drop", "ALL",
            "--ro-bind", "/usr", "/usr", "--symlink", "usr/bin", "/bin", "--symlink", "usr/lib", "/lib",
            "--symlink", "usr/lib64", "/lib64", "--ro-bind", str(node), "/opt/node", "--ro-bind", str(pi), "/opt/pi",
            "--dev", "/dev",
            "--size", str(min(args.max_memory_bytes, 256 * 1024 * 1024)), "--tmpfs", "/tmp", "--dir", "/tmp/pi-agent",
            "--ro-bind", str(settings), "/tmp/pi-agent/settings.json", "--dir", "/run/localbrain", "--ro-bind", str(socket), "/run/localbrain/model.sock",
            "--bind", str(workspace), "/workspace", "--chdir", "/workspace", "--clearenv",
            "--setenv", "HOME", "/tmp", "--setenv", "PI_CODING_AGENT_DIR", "/tmp/pi-agent",
            "--setenv", "PATH", "/opt/node/bin:/usr/bin:/bin", "/opt/node/bin/node",
            "/opt/pi/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js", "--mode", "rpc", "--offline",
            "--no-session", "--no-mcp", "--no-skills", "--no-context-files", "--no-approve",
            "--tools", "read,write,edit,bash,codemode,tool_search", "--extension", "/opt/pi/localbrain-provider.mjs",
            "--extension", "/opt/pi/localbrain-loop.mjs",
            "--provider", "localbrain", "--model", "local-qwen38"]
    os.execve(str(bwrap), argv, {"PATH": "/usr/bin:/bin"})


if __name__ == "__main__":
    main()
