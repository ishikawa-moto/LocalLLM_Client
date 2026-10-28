"""Create bounded archives of stable workspace directories.

A trusted Host must select the process-root workspace and keep its sandbox
cgroup frozen for the entire call to :func:`archive_workspace`. This module
neither freezes processes nor guarantees workspace stability itself.
"""

import ntpath
import os
import stat
import sys
import tarfile


_EXCLUDED_DIRECTORIES = {".git", ".localbrain"}


def _archive_name(parts):
    """Return a safe, relative tar name for filesystem path components."""
    if not parts:
        raise ValueError("empty archive name")
    for part in parts:
        if (
            not part
            or part in (".", "..")
            or "/" in part
            or "\\" in part
            or "\x00" in part
            or ntpath.splitdrive(part)[0]
        ):
            raise ValueError("unsafe archive name")
    name = "/".join(parts)
    if name.startswith("/") or ntpath.isabs(name):
        raise ValueError("unsafe archive name")
    return name


def _same_file(expected, actual):
    return (
        expected.st_dev == actual.st_dev
        and expected.st_ino == actual.st_ino
        and stat.S_ISREG(actual.st_mode)
        and actual.st_nlink == 1
        and expected.st_size == actual.st_size
    )


def _open_regular(path, expected):
    flags = os.O_RDONLY | getattr(os, "O_BINARY", 0)
    nofollow = getattr(os, "O_NOFOLLOW", None)
    if sys.platform.startswith("linux") and nofollow is None:
        raise RuntimeError("O_NOFOLLOW is required on Linux")
    if nofollow is not None:
        flags |= nofollow
    fd = os.open(path, flags)
    try:
        actual = os.fstat(fd)
        if not _same_file(expected, actual):
            raise ValueError("workspace file changed or is unsafe")
        stream = os.fdopen(fd, "rb")
    except BaseException:
        os.close(fd)
        raise
    return stream, actual


def _tar_info(name, mode, size=0, directory=False):
    info = tarfile.TarInfo(name)
    info.type = tarfile.DIRTYPE if directory else tarfile.REGTYPE
    info.size = size
    info.mode = (0o755 if mode & 0o111 else 0o644) if not directory else 0o755
    info.uid = 0
    info.gid = 0
    info.uname = ""
    info.gname = ""
    info.mtime = 0
    return info


def archive_workspace(root, output, max_files, max_bytes):
    """Write an uncompressed tar archive of a stable, frozen workspace.

    A trusted Host must select the process-root workspace and keep the sandbox
    cgroup frozen throughout this call. ``root`` must be an absolute existing
    directory and ``output`` an already opened binary stream. ``max_files``
    bounds archive entries, including directories; ``max_bytes`` bounds the
    sum of regular-file sizes. The output may contain a partial archive if an
    error occurs.
    """
    root = os.fspath(root)
    if not os.path.isabs(root):
        raise ValueError("workspace root must be absolute")
    root_stat = os.lstat(root)
    if not stat.S_ISDIR(root_stat.st_mode):
        raise ValueError("workspace root must be an existing directory")
    if not isinstance(max_files, int) or max_files < 0:
        raise ValueError("max_files must be a nonnegative integer")
    if not isinstance(max_bytes, int) or max_bytes < 0:
        raise ValueError("max_bytes must be a nonnegative integer")

    entries = 0
    total_bytes = 0

    def visit(archive, directory, parts):
        nonlocal entries, total_bytes
        children = []
        with os.scandir(directory) as iterator:
            for child in iterator:
                # Bound allocation before sorting. Excluded directories still
                # require inspection, but cannot be added to the result.
                if child.name.casefold() in _EXCLUDED_DIRECTORIES and child.is_dir(follow_symlinks=False):
                    continue
                children.append(child)
                if len(children) > max_files - entries:
                    raise ValueError("workspace entry limit exceeded")
        children.sort(key=lambda entry: entry.name)
        for child in children:
            # On Windows DirEntry.stat may omit the file identity and link
            # count; lstat obtains those fields for the subsequent fstat check.
            child_stat = os.lstat(child.path)
            mode = child_stat.st_mode
            if stat.S_ISDIR(mode) and child.name.casefold() in _EXCLUDED_DIRECTORIES:
                continue
            child_parts = parts + (child.name,)
            name = _archive_name(child_parts)
            if stat.S_ISDIR(mode):
                if entries + 1 > max_files:
                    raise ValueError("workspace entry limit exceeded")
                entries += 1
                archive.addfile(_tar_info(name, mode, directory=True))
                visit(archive, child.path, child_parts)
            elif stat.S_ISREG(mode):
                if child_stat.st_nlink != 1:
                    raise ValueError("hard-linked workspace file")
                if entries + 1 > max_files:
                    raise ValueError("workspace entry limit exceeded")
                if total_bytes + child_stat.st_size > max_bytes:
                    raise ValueError("workspace byte limit exceeded")
                with _open_regular(child.path, child_stat)[0] as stream:
                    actual = os.fstat(stream.fileno())
                    if not _same_file(child_stat, actual):
                        raise ValueError("workspace file changed or is unsafe")
                    entries += 1
                    total_bytes += actual.st_size
                    archive.addfile(_tar_info(name, actual.st_mode, actual.st_size), stream)
            else:
                raise ValueError("symlink or special file in workspace")

    with tarfile.open(fileobj=output, mode="w|") as archive:
        visit(archive, root, ())
