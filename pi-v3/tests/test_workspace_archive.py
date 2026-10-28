"""Tests for the workspace archive utility.

The caller must keep the workspace stable and frozen throughout archiving.
These tests use a private temporary directory with no concurrent writers.
"""

import importlib.util
import io
import os
import posixpath
import tarfile
import tempfile
import unittest

_MODULE_PATH = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "bridge",
    "workspace_archive.py",
)
_spec = importlib.util.spec_from_file_location("workspace_archive", _MODULE_PATH)
_module = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_module)
archive_workspace = _module.archive_workspace


class TestArchiveWorkspace(unittest.TestCase):
    def setUp(self):
        try:
            self.tempdir = tempfile.TemporaryDirectory()
        except OSError as exc:
            self.skipTest(f"temporary directory unavailable: {exc}")
        self.addCleanup(self.tempdir.cleanup)
        self.root = os.path.abspath(self.tempdir.name)
        try:
            probe = os.path.join(self.root, "probe")
            with open(probe, "wb") as stream:
                stream.write(b"ok")
            os.remove(probe)
        except OSError as exc:
            self.skipTest(f"temporary workspace is not writable: {exc}")

    def make_file(self, relpath, content=b""):
        path = os.path.join(self.root, *relpath.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as stream:
            stream.write(content)
        return path

    def archive(self, max_files=100, max_bytes=10000):
        output = io.BytesIO()
        archive_workspace(self.root, output, max_files, max_bytes)
        return output.getvalue()

    def members(self, data):
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:") as archive:
            return archive.getmembers()

    def test_contents_names_metadata_and_order_are_deterministic(self):
        self.make_file("z.txt", b"last")
        self.make_file("sub/b.txt", b"second")
        self.make_file("sub/a.txt", b"first")
        first = self.archive()
        second = self.archive()
        self.assertEqual(first, second)
        with tarfile.open(fileobj=io.BytesIO(first), mode="r:") as archive:
            members = archive.getmembers()
            names = [member.name for member in members]
            self.assertEqual(set(names), {"sub", "sub/a.txt", "sub/b.txt", "z.txt"})
            self.assertEqual(len(names), len(set(names)))
            self.assertLess(names.index("sub/a.txt"), names.index("sub/b.txt"))
            self.assertLess(names.index("sub"), names.index("z.txt"))
            for member in members:
                self.assertFalse(posixpath.isabs(member.name))
                self.assertNotIn("..", member.name.split("/"))
                self.assertEqual(member.uid, 0)
                self.assertEqual(member.gid, 0)
                self.assertEqual(member.uname, "")
                self.assertEqual(member.gname, "")
                self.assertEqual(member.mtime, 0)
                self.assertTrue(member.isfile() or member.isdir())
            self.assertEqual(archive.extractfile("sub/a.txt").read(), b"first")
            self.assertEqual(archive.extractfile("sub/b.txt").read(), b"second")
            self.assertEqual(archive.extractfile("z.txt").read(), b"last")

    def test_file_permissions(self):
        ordinary = self.make_file("ordinary", b"data")
        executable = self.make_file("executable", b"script")
        try:
            os.chmod(ordinary, 0o600)
            os.chmod(executable, 0o755)
        except OSError as exc:
            self.skipTest(f"cannot set executable permission: {exc}")
        if not os.stat(executable).st_mode & 0o111:
            self.skipTest("platform does not represent executable permission")
        with tarfile.open(fileobj=io.BytesIO(self.archive()), mode="r:") as archive:
            self.assertEqual(archive.getmember("ordinary").mode, 0o644)
            self.assertEqual(archive.getmember("executable").mode, 0o755)

    def test_excludes_directories_at_all_depths(self):
        self.make_file(".git/config", b"excluded")
        self.make_file(".localbrain/state", b"excluded")
        self.make_file("sub/.git/HEAD", b"excluded")
        self.make_file("sub/.localbrain/state", b"excluded")
        self.make_file("sub/keep", b"included")
        names = {member.name for member in self.members(self.archive())}
        self.assertEqual(names, {"sub", "sub/keep"})

    def test_entry_limit_counts_directories(self):
        self.make_file("dir/file", b"x")
        self.assertEqual(len(self.members(self.archive(max_files=2))), 2)
        with self.assertRaises((ValueError, OSError)):
            self.archive(max_files=1)

    def test_byte_limit_counts_total_regular_file_bytes(self):
        self.make_file("a", b"abc")
        self.make_file("b", b"de")
        self.assertEqual(len(self.members(self.archive(max_bytes=5))), 2)
        with self.assertRaises((ValueError, OSError)):
            self.archive(max_bytes=4)

    def test_symlink_is_rejected(self):
        self.make_file("target", b"data")
        try:
            os.symlink("target", os.path.join(self.root, "link"))
        except (OSError, NotImplementedError) as exc:
            self.skipTest(f"symlink creation is unavailable: {exc}")
        with self.assertRaises((ValueError, OSError)):
            self.archive()

    def test_hard_link_is_rejected(self):
        target = self.make_file("target", b"data")
        try:
            os.link(target, os.path.join(self.root, "link"))
        except (OSError, NotImplementedError) as exc:
            self.skipTest(f"hard link creation is unavailable: {exc}")
        if os.stat(target).st_nlink < 2:
            self.skipTest("filesystem does not report hard link counts")
        with self.assertRaises((ValueError, OSError)):
            self.archive()

    def test_empty_directory(self):
        self.assertEqual(self.members(self.archive(max_files=0, max_bytes=0)), [])


if __name__ == "__main__":
    unittest.main()
