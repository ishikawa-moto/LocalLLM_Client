"""Experimental ledger; it does not establish OS ownership or snapshot safety.

The trusted Host must exclusively control the database directory and SQLite files,
protect credentials, and independently retain the issued registry identity durably.
Use a trusted local Windows or POSIX filesystem and SQLite VFS with reliable locks
and synchronization; network and synchronized filesystems are unsupported. The OS
and storage must honor those guarantees. The Host must prevent database replacement,
rollback and aliasing while active. Identity cannot detect an older valid copy.
Receipt verification and OS lifecycle, containment and authorization are outside
scope. Never delete/reset records or automatically recover on errors. A lost
acknowledgment can follow a committed transaction; inspect persisted state and retain
uncertainty if inspection fails. Quarantine does not itself verify external state.
"""

import hashlib
import os
from pathlib import Path
import re
import secrets
import sqlite3
import uuid
from dataclasses import dataclass, field


class RegistryError(Exception):
    """Ownership conflict or invalid registry state, without exposing credentials."""


@dataclass(frozen=True)
class Ownership:
    task_id: str
    generation_id: str
    owner_token: str = field(repr=False)


def _canonical_uuid(value):
    if not isinstance(value, str):
        raise ValueError('UUID must be a canonical string')
    try:
        parsed = uuid.UUID(value)
    except (ValueError, AttributeError):
        raise ValueError('UUID must be a canonical string') from None
    if str(parsed) != value:
        raise ValueError('UUID must be a canonical string')
    return value


_SCHEMA = {
    'registry_metadata': '''CREATE TABLE registry_metadata (
        singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
        registry_id TEXT NOT NULL
    )''',
    'generations': '''CREATE TABLE generations (
        generation_id TEXT NOT NULL PRIMARY KEY,
        task_id TEXT NOT NULL,
        token_hash BLOB NOT NULL CHECK (length(token_hash) = 32),
        state TEXT NOT NULL CHECK (state IN ('ACTIVE', 'QUARANTINED', 'COMPLETED')),
        receipt_hash BLOB CHECK (receipt_hash IS NULL OR length(receipt_hash) = 32)
    )''',
    'one_unresolved_per_task': '''CREATE UNIQUE INDEX one_unresolved_per_task
        ON generations(task_id) WHERE state != 'COMPLETED' ''',
}


class SnapshotRegistry:
    """Explicitly provision once, then open with the Host-retained identity."""

    def __init__(self, path, registry_id):
        self.path = str(Path(path).absolute())
        self.registry_id = _canonical_uuid(registry_id)
        db = self._connect()
        try:
            db.execute('BEGIN IMMEDIATE')
            self._validate(db)
            db.commit()
        finally:
            db.close()

    @classmethod
    def provision(cls, path):
        """Reserve a new file and issue an identity the Host must retain durably.

        Failure leaves the reserved file in place; it is never reset or removed.
        """
        path = str(Path(path).absolute())
        fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        os.close(fd)
        registry_id = str(uuid.uuid4())
        registry = cls.__new__(cls)
        registry.path = path
        registry.registry_id = registry_id
        db = registry._connect()
        try:
            db.execute('BEGIN IMMEDIATE')
            for sql in _SCHEMA.values():
                db.execute(sql)
            db.execute('INSERT INTO registry_metadata VALUES (1, ?)', (registry_id,))
            db.execute('PRAGMA user_version=1')
            registry._validate(db)
            db.commit()
        finally:
            db.close()
        return registry

    def _connect(self):
        options = {'timeout': 30, 'isolation_level': None, 'uri': True}
        if hasattr(sqlite3, 'LEGACY_TRANSACTION_CONTROL'):
            options['autocommit'] = sqlite3.LEGACY_TRANSACTION_CONTROL
        db = sqlite3.connect(Path(self.path).as_uri() + '?mode=rw', **options)
        try:
            # Inspect before authentication: changing a misidentified database's
            # persistent journal mode would itself be an unauthorized mutation.
            if db.execute('PRAGMA journal_mode').fetchone() != ('delete',):
                raise RegistryError('DELETE journaling is unavailable')
            db.execute('PRAGMA synchronous=EXTRA')
            if db.execute('PRAGMA synchronous').fetchone() != (3,):
                raise RegistryError('EXTRA synchronization is unavailable')
            return db
        except BaseException:
            db.close()
            raise

    def _validate(self, db):
        if db.execute('PRAGMA user_version').fetchone() != (1,):
            raise RegistryError('unexpected registry version')
        objects = db.execute("SELECT name, type, sql FROM main.sqlite_master WHERE name NOT GLOB 'sqlite_*'").fetchall()
        expected = sorted((name, 'index' if name == 'one_unresolved_per_task' else 'table', sql.strip())
                          for name, sql in _SCHEMA.items())
        actual = sorted((name, kind, sql.strip() if sql is not None else None)
                        for name, kind, sql in objects)
        if actual != expected:
            raise RegistryError('unexpected registry schema')
        rows = db.execute('SELECT singleton, registry_id FROM registry_metadata').fetchall()
        if rows != [(1, self.registry_id)]:
            raise RegistryError('registry identity mismatch')

    def acquire(self, task_id):
        task_id = _canonical_uuid(task_id)
        generation_id = str(uuid.uuid4())
        token = secrets.token_urlsafe(32)
        token_hash = hashlib.sha256(token.encode('ascii')).digest()
        db = self._connect()
        try:
            db.execute('BEGIN IMMEDIATE')
            self._validate(db)
            if db.execute("SELECT 1 FROM generations WHERE task_id = ? AND state != 'COMPLETED'", (task_id,)).fetchone():
                raise RegistryError('task has an unresolved generation')
            try:
                result = db.execute('INSERT INTO generations VALUES (?, ?, ?, ?, NULL)',
                                    (generation_id, task_id, token_hash, 'ACTIVE'))
            except sqlite3.IntegrityError:
                raise RegistryError('generation already exists or task is unresolved') from None
            if result.rowcount != 1:
                raise RegistryError('generation insert failed')
            db.commit()
            return Ownership(task_id, generation_id, token)
        finally:
            db.close()

    def quarantine(self, ownership):
        """Mark an operation unresolved; never release it."""
        self._transition(ownership, 'QUARANTINED', None)

    def complete(self, ownership, receipt):
        """Resolve with opaque nonempty bytes; verification belongs to the Host."""
        if not isinstance(receipt, bytes) or not receipt:
            raise ValueError('a nonempty opaque bytes receipt is required')
        self._transition(ownership, 'COMPLETED', hashlib.sha256(receipt).digest())

    def _transition(self, ownership, state, receipt_hash):
        if not isinstance(ownership, Ownership):
            raise RegistryError('invalid ownership')
        task_id = _canonical_uuid(ownership.task_id)
        generation_id = _canonical_uuid(ownership.generation_id)
        if not isinstance(ownership.owner_token, str) or re.fullmatch(r'[A-Za-z0-9_-]{43}', ownership.owner_token) is None:
            raise RegistryError('invalid ownership')
        token_hash = hashlib.sha256(ownership.owner_token.encode('ascii')).digest()
        db = self._connect()
        try:
            db.execute('BEGIN IMMEDIATE')
            self._validate(db)
            row = db.execute('SELECT token_hash, state FROM generations WHERE task_id = ? AND generation_id = ?',
                             (task_id, generation_id)).fetchone()
            if row is None or not secrets.compare_digest(row[0], token_hash) or row[1] == 'COMPLETED':
                raise RegistryError('invalid ownership or state')
            result = db.execute('UPDATE generations SET state = ?, receipt_hash = ? WHERE task_id = ? AND generation_id = ?',
                                (state, receipt_hash, task_id, generation_id))
            if result.rowcount != 1:
                raise RegistryError('generation update failed')
            db.commit()
        finally:
            db.close()
