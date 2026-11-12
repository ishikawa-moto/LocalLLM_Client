"""Experimental: materialize a Host-verified archive into an empty task mount.

The Windows Host must first inspect the complete stable archive with its v3
archive inspector, authorize every manifest path, and provide the exact archive
SHA-256 and inspector records. This helper does not make authorization
 decisions. Run only before any untrusted sandbox process starts. The trusted
caller must retain exclusive control of the destination throughout seeding.

On any error or cancellation the caller must discard the entire task mount and
must not start Pi; a failed extraction may have created partial files.
The optional cancelled callback provides cooperative cancellation between I/O
operations; it cannot interrupt a blocking system call or source.read().
This helper is not yet integrated into the Host or security-reviewed.
"""
from concurrent.futures import CancelledError
import hashlib
import hmac
import os
from pathlib import Path
import stat
import tarfile
import tempfile
import unicodedata


_RESERVED = {'CON', 'CONIN$', 'CONOUT$', 'PRN', 'AUX', 'NUL'} | {
    prefix + suffix
    for prefix in ('COM', 'LPT')
    for suffix in ('1', '2', '3', '4', '5', '6', '7', '8', '9', '¹', '²', '³')
}


def _check_cancelled(cancelled):
    if cancelled is not None and cancelled():
        raise CancelledError('workspace seeding cancelled; discard the task mount')


def _valid_digest(value):
    return (type(value) is str and len(value) == 64 and
            all(c in '0123456789abcdef' for c in value))


def _validate_name(name, directory):
    if (type(name) is not str or not name or name.startswith('/') or
            '\\' in name or (not directory and name.endswith('/'))):
        raise ValueError('unsafe workspace path')
    if len(name.encode('utf-8')) > 4096:
        raise ValueError('workspace path byte limit')
    parts = name.rstrip('/').split('/')
    if len(parts) > 32:
        raise ValueError('workspace path depth limit')
    for part in parts:
        if (not part or len(part.encode('utf-16-le')) // 2 > 255 or
                part in ('.', '..') or part.endswith((' ', '.')) or
                any(ord(c) < 32 or c in '<>:"|?*' for c in part) or
                part.split('.')[0].rstrip(' ').upper() in _RESERVED or
                part.upper() in ('.GIT', '.LOCALBRAIN')):
            raise ValueError('unsafe workspace path')
    return '/'.join(parts)


def _validate_manifest(manifest, limit, cancelled):
    if type(manifest) is not list or len(manifest) > 10000:
        raise ValueError('invalid Host manifest')
    records = []
    paths = {}
    parents = set()
    total = parent_bytes = 0
    for record in manifest:
        _check_cancelled(cancelled)
        if (type(record) is not dict or set(record) !=
                {'name', 'directory', 'size', 'sha256', 'executable'}):
            raise ValueError('invalid Host record')
        name = record['name']
        directory = record['directory']
        size = record['size']
        digest = record['sha256']
        executable = record['executable']
        if (type(directory) is not bool or type(size) is not int or
                size < 0 or type(executable) is not bool):
            raise ValueError('invalid Host record')
        if _validate_name(name, directory) != name:
            raise ValueError('noncanonical Host path')
        if directory:
            if size != 0 or digest is not None:
                raise ValueError('invalid directory record')
        else:
            if not _valid_digest(digest):
                raise ValueError('invalid file digest')
            total += size
            if total > limit:
                raise ValueError('manifest file byte limit')
        normalized = unicodedata.normalize('NFC', name)
        # Conservative local collision checks supplement the Host's comparer.
        key = normalized.upper()
        if key in paths:
            raise ValueError('duplicate Host path')
        segments = normalized.split('/')
        for i in range(1, len(segments)):
            parent = '/'.join(segments[:i])
            parent_bytes += len(parent.encode('utf-8'))
            if parent_bytes > 8 * 1024 * 1024:
                raise ValueError('manifest parent path limit')
            parent_key = parent.upper()
            if parent_key in paths and not paths[parent_key]:
                raise ValueError('file used as parent')
            parents.add(parent_key)
        if not directory and key in parents:
            raise ValueError('file replaces parent')
        paths[key] = directory
        records.append((name, directory, size, digest, executable))
    return records


def _snapshot(source, target, expected_hash, limit, cancelled):
    source.seek(0)
    digest = hashlib.sha256()
    count = 0
    while True:
        _check_cancelled(cancelled)
        requested = min(65536, limit - count + 1)
        chunk = source.read(requested)
        if type(chunk) is not bytes or len(chunk) > requested:
            raise ValueError('invalid binary archive source')
        if not chunk:
            break
        count += len(chunk)
        if count > limit:
            raise ValueError('archive exceeds limit')
        digest.update(chunk)
        target.write(chunk)
    if not hmac.compare_digest(digest.hexdigest(), expected_hash):
        raise ValueError('archive digest changed')
    target.seek(0)
    return count


def _preflight(source, length, max_entries, cancelled):
    # Run before tarfile can allocate or recursively process extension metadata.
    # Only one path-only PAX header per material entry is supported.
    headers = 0
    pending_pax = False
    entries = []
    while source.tell() < length:
        _check_cancelled(cancelled)
        header = source.read(512)
        if len(header) != 512:
            raise ValueError('truncated tar header')
        if not any(header):
            if pending_pax:
                raise ValueError('orphan PAX header')
            while True:
                _check_cancelled(cancelled)
                chunk = source.read(65536)
                if not chunk:
                    break
                if any(chunk):
                    raise ValueError('nonzero data after tar terminator')
            if len(entries) != max_entries:
                raise ValueError('archive entry count differs from Host manifest')
            source.seek(0)
            return entries
        headers += 1
        if headers > 2 * max_entries:
            raise ValueError('tar header limit')
        kind = header[156:157]
        if kind not in (b'\0', b'0', b'5', b'x'):
            raise ValueError('unsupported tar header type')
        octal = header[124:136].strip(b'\0 ')
        if any(c < ord('0') or c > ord('7') for c in octal):
            raise ValueError('invalid tar size')
        size = int(octal, 8) if octal else 0
        body_start = source.tell()
        padding = (512 - size % 512) % 512
        if size + padding > length - body_start:
            raise ValueError('truncated tar body or padding')
        if kind == b'x':
            if pending_pax or not 0 < size <= 65536:
                raise ValueError('unsupported or oversized PAX metadata')
            data = source.read(size)
            space = data.find(b' ')
            digits = data[:space] if space > 0 else b''
            if (not digits or len(digits) > 8 or digits.startswith(b'0') or
                    not digits.isdigit() or int(digits) != size or
                    data[-1:] != b'\n'):
                raise ValueError('invalid PAX record')
            record = data[space + 1:-1]
            if not record.startswith(b'path='):
                raise ValueError('unsupported PAX field')
            # Name validation rejects embedded newlines and therefore additional
            # records. Strict UTF-8 avoids fallback decoding differences.
            _validate_name(record[5:].decode('utf-8'), True)
            pending_pax = True
        else:
            if len(entries) >= max_entries or (kind == b'5' and size != 0):
                raise ValueError('invalid tar material entry')
            entries.append((kind == b'5', size))
            pending_pax = False
        source.seek(body_start + size + padding)
    raise ValueError('missing tar terminator')


def _directory_flags():
    return os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC


def _check_directory(info):
    if (not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or
            info.st_mode & 0o022):
        raise ValueError('workspace directories must be owned by the helper and not writable by others')


def _open_root(destination, cancelled):
    if os.name != 'posix':
        raise ValueError('workspace seeding requires WSL/POSIX')
    root = Path(destination)
    if (not root.is_absolute() or root.resolve(strict=True) != root or
            not os.path.ismount(root)):
        raise ValueError('workspace must be a dedicated, unlinked mount')
    fd = os.open('/', _directory_flags())
    try:
        for part in root.parts[1:]:
            _check_cancelled(cancelled)
            child = os.open(part, _directory_flags(), dir_fd=fd)
            os.close(fd)
            fd = child
        info = os.fstat(fd)
        current = os.stat(root, follow_symlinks=False)
        if ((info.st_dev, info.st_ino) != (current.st_dev, current.st_ino) or
                not os.path.ismount(root)):
            raise ValueError('workspace mount changed')
        _check_directory(info)
        with os.scandir(fd) as children:
            if next(children, None) is not None:
                raise ValueError('workspace mount must be empty')
        return fd
    except BaseException:
        os.close(fd)
        raise


def _open_directory(root_fd, parts, cancelled):
    fd = os.dup(root_fd)
    try:
        for part in parts:
            _check_cancelled(cancelled)
            try:
                os.mkdir(part, mode=0o700, dir_fd=fd)
            except FileExistsError:
                pass
            child = os.open(part, _directory_flags(), dir_fd=fd)
            try:
                _check_directory(os.fstat(child))
            except BaseException:
                os.close(child)
                raise
            os.close(fd)
            fd = child
        return fd
    except BaseException:
        os.close(fd)
        raise


def _write_file(archive, entry, parent_fd, leaf, size, expected, executable, cancelled):
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC
    fd = os.open(leaf, flags, 0o600, dir_fd=parent_fd)
    try:
        stream = archive.extractfile(entry)
        if stream is None:
            raise ValueError('missing file body')
        with stream:
            with os.fdopen(fd, 'wb') as output:
                fd = None
                digest = hashlib.sha256()
                remaining = size
                while remaining:
                    _check_cancelled(cancelled)
                    chunk = stream.read(min(65536, remaining))
                    if not chunk or len(chunk) > remaining:
                        raise ValueError('file length differs from Host record')
                    remaining -= len(chunk)
                    digest.update(chunk)
                    output.write(chunk)
                _check_cancelled(cancelled)
                if stream.read(1) or not hmac.compare_digest(digest.hexdigest(), expected):
                    raise ValueError('file differs from Host record')
                output.flush()
                os.fchmod(output.fileno(), 0o700 if executable else 0o600)
    finally:
        if fd is not None:
            os.close(fd)


def seed_workspace(source, expected_hash, manifest, destination, max_archive_bytes,
                   *, cancelled=None):
    if not _valid_digest(expected_hash):
        raise ValueError('invalid Host archive digest')
    if type(max_archive_bytes) is not int or not 0 < max_archive_bytes <= 512 * 1024 * 1024:
        raise ValueError('invalid archive limit')
    if cancelled is not None and not callable(cancelled):
        raise ValueError('invalid cancellation callback')
    _check_cancelled(cancelled)
    if not source.readable() or not source.seekable():
        raise ValueError('archive source must be readable and seekable')
    records = _validate_manifest(manifest, max_archive_bytes, cancelled)
    with tempfile.TemporaryFile(mode='w+b') as snapshot:
        length = _snapshot(source, snapshot, expected_hash, max_archive_bytes, cancelled)
        raw_entries = _preflight(snapshot, length, len(records), cancelled)
        with tarfile.open(fileobj=snapshot, mode='r:', encoding='utf-8', errors='strict') as archive:
            root_fd = _open_root(destination, cancelled)
            try:
                index = 0
                for entry in archive:
                    _check_cancelled(cancelled)
                    if index >= len(records):
                        raise ValueError('unexpected archive entry')
                    name, directory, size, digest, executable = records[index]
                    raw_directory, raw_size = raw_entries[index]
                    index += 1
                    if (entry.type not in (tarfile.REGTYPE, tarfile.AREGTYPE, tarfile.DIRTYPE) or
                            raw_directory != directory or raw_size != size or
                            entry.isdir() != directory or entry.size != size or
                            bool(entry.mode & 0o111) != executable or
                            _validate_name(entry.name, directory) != name):
                        raise ValueError('archive differs from Host manifest')
                    parts = name.split('/')
                    parent_fd = _open_directory(
                        root_fd, parts if directory else parts[:-1], cancelled)
                    try:
                        if not directory:
                            _write_file(archive, entry, parent_fd, parts[-1], size,
                                        digest, executable, cancelled)
                    finally:
                        os.close(parent_fd)
                if index != len(records):
                    raise ValueError('incomplete archive')
                _check_cancelled(cancelled)
            finally:
                os.close(root_fd)
