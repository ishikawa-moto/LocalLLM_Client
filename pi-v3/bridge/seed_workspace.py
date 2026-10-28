"""Experimental: materialize a Host-verified archive into an empty task mount.

The Windows Host must first inspect the complete stable archive with its v3
archive inspector, authorize every manifest path, and provide the exact archive
SHA-256 and inspector records. This helper does not make authorization
decisions. Run only before any untrusted sandbox process starts.
On any error the caller must discard the entire task mount and must not start
Pi; a failed extraction may have created partial files.
This helper is not yet integrated into the Host or security-reviewed.
"""
import hashlib
import hmac
import os
from pathlib import Path
import stat
import tarfile


def _digest(stream):
    digest=hashlib.sha256()
    while chunk:=stream.read(65536): digest.update(chunk)
    return digest.hexdigest()


def seed_workspace(source,expected_hash,manifest,destination,max_archive_bytes):
    if not isinstance(expected_hash,str) or len(expected_hash)!=64 or any(c not in '0123456789abcdef' for c in expected_hash):
        raise ValueError('invalid Host archive digest')
    if type(max_archive_bytes) is not int or not 0<max_archive_bytes<=512*1024*1024:
        raise ValueError('invalid archive limit')
    if not source.seekable() or not isinstance(manifest,list) or len(manifest)>10000:
        raise ValueError('invalid Host manifest')
    root=Path(destination)
    if not root.is_absolute() or root.resolve(strict=True)!=root or not os.path.ismount(root):
        raise ValueError('workspace must be a dedicated, unlinked mount')
    info=root.stat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid!=os.getuid() or any(root.iterdir()):
        raise ValueError('workspace mount must be empty and Host-owned')
    source.seek(0,2)
    if source.tell()>max_archive_bytes: raise ValueError('archive exceeds limit')
    source.seek(0)
    if not hmac.compare_digest(_digest(source),expected_hash): raise ValueError('archive digest changed')
    source.seek(0)
    index=0
    with tarfile.open(fileobj=source,mode='r:') as archive:
        for entry in archive:
            if index>=len(manifest): raise ValueError('unexpected archive entry')
            record=manifest[index];index+=1
            if not isinstance(record,dict) or set(record)!={'name','directory','size','sha256','executable'}:
                raise ValueError('invalid Host record')
            name=record['name'];directory=record['directory'];size=record['size']
            if not isinstance(name,str) or type(directory) is not bool or type(size) is not int or size<0 or type(record['executable']) is not bool:
                raise ValueError('invalid Host record')
            parts=name.split('/')
            if not parts or any(not p or p in ('.','..') or '\\' in p or ':' in p for p in parts):
                raise ValueError('unsafe workspace path')
            if entry.name.rstrip('/')!=name or entry.size!=size or directory!=entry.isdir() or (not directory and not entry.isfile()):
                raise ValueError('archive differs from Host manifest')
            path=root.joinpath(*parts)
            if directory:
                if size or record['sha256'] is not None: raise ValueError('invalid directory record')
                path.mkdir(mode=0o700,parents=True,exist_ok=True)
            else:
                expected=record['sha256']
                if not isinstance(expected,str) or len(expected)!=64: raise ValueError('invalid file digest')
                path.parent.mkdir(mode=0o700,parents=True,exist_ok=True)
                flags=os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW
                fd=os.open(path,flags,0o700 if record['executable'] else 0o600)
                stream=archive.extractfile(entry)
                if stream is None: os.close(fd);raise ValueError('missing file body')
                digest=hashlib.sha256();count=0
                with os.fdopen(fd,'wb') as output:
                    while chunk:=stream.read(65536):
                        count+=len(chunk)
                        if count>size: raise ValueError('file exceeds Host record')
                        digest.update(chunk);output.write(chunk)
                if count!=size or not hmac.compare_digest(digest.hexdigest(),expected):
                    raise ValueError('file differs from Host record')
    if index!=len(manifest): raise ValueError('incomplete archive')
