import multiprocessing
import os
import sqlite3
from pathlib import Path
import sys
import tempfile
import time
import unittest
import uuid
from contextlib import closing
from dataclasses import replace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / 'bridge'))
from snapshot_registry import Ownership, RegistryError, SnapshotRegistry


def competing_acquire(path, registry_id, task, start, results):
    registry = SnapshotRegistry(path, registry_id)
    start.wait()
    try:
        registry.acquire(task)
        results.put('acquired')
    except RegistryError:
        results.put('blocked')


def acquire_until_killed(path, registry_id, task, acquired):
    SnapshotRegistry(path, registry_id).acquire(task)
    acquired.set()
    time.sleep(30)


def competing_complete(path, registry_id, owner, start, results):
    registry = SnapshotRegistry(path, registry_id)
    start.wait()
    try:
        registry.complete(owner, b'receipt')
        results.put('completed')
    except RegistryError:
        results.put('blocked')


class SnapshotRegistryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = str(Path(self.temp.name) / 'registry.sqlite')
        self.registry = SnapshotRegistry.provision(self.path)
        self.registry_id = self.registry.registry_id
        self.task = str(uuid.uuid4())

    def test_provision_and_open_fail_closed(self):
        with self.assertRaises(FileExistsError):
            SnapshotRegistry.provision(self.path)
        with self.assertRaises(RegistryError):
            SnapshotRegistry(self.path, str(uuid.uuid4()))
        missing = str(Path(self.temp.name) / 'missing.sqlite')
        with self.assertRaises(sqlite3.Error):
            SnapshotRegistry(missing, self.registry_id)
        self.assertFalse(Path(missing).exists())
        malformed = str(Path(self.temp.name) / 'malformed.sqlite')
        Path(malformed).touch()
        with self.assertRaises(RegistryError):
            SnapshotRegistry(malformed, self.registry_id)

    def test_effective_pragmas_and_validation_on_write(self):
        db = self.registry._connect()
        try:
            self.assertEqual(db.execute('PRAGMA journal_mode').fetchone(), ('delete',))
            self.assertEqual(db.execute('PRAGMA synchronous').fetchone(), (3,))
        finally:
            db.close()
        with closing(sqlite3.connect(self.path)) as db:
            db.execute('UPDATE registry_metadata SET registry_id = ?', (str(uuid.uuid4()),))
            db.commit()
        with self.assertRaises(RegistryError):
            self.registry.acquire(self.task)

    def test_unrelated_wal_database_is_not_modified(self):
        other = str(Path(self.temp.name) / 'unrelated.sqlite')
        with closing(sqlite3.connect(other)) as db:
            self.assertEqual(db.execute('PRAGMA journal_mode=WAL').fetchone(), ('wal',))
            db.execute('CREATE TABLE content (value INTEGER)')
            db.commit()
        with self.assertRaises(RegistryError):
            SnapshotRegistry(other, self.registry_id)
        with closing(sqlite3.connect(other)) as db:
            self.assertEqual(db.execute('PRAGMA journal_mode').fetchone(), ('wal',))

    def test_unexpected_schema_and_missing_database_on_write(self):
        with closing(sqlite3.connect(self.path)) as db:
            db.execute('CREATE TABLE unexpected (value TEXT)')
            db.commit()
        with self.assertRaises(RegistryError):
            self.registry.acquire(self.task)
        Path(self.path).unlink()
        with self.assertRaises(sqlite3.Error):
            self.registry.acquire(self.task)
        self.assertFalse(Path(self.path).exists())

    def test_unexpected_triggers_rejected_on_open_and_all_mutations(self):
        owner = self.registry.acquire(self.task)
        for name in ('sqliteXignore', 'one_unresolved_per_task'):
            with self.subTest(trigger=name):
                with closing(sqlite3.connect(self.path)) as db:
                    db.execute(f'CREATE TRIGGER {name} BEFORE INSERT ON generations '
                               'BEGIN SELECT RAISE(IGNORE); END')
                    if name == 'one_unresolved_per_task':
                        index_sql = db.execute("SELECT sql FROM sqlite_master WHERE type='index' AND name=?",
                                               (name,)).fetchone()[0]
                        db.execute('DROP INDEX one_unresolved_per_task')
                        db.execute(index_sql)
                    db.commit()
                with self.assertRaises(RegistryError):
                    SnapshotRegistry(self.path, self.registry_id)
                for operation in (lambda: self.registry.acquire(str(uuid.uuid4())),
                                  lambda: self.registry.quarantine(owner),
                                  lambda: self.registry.complete(owner, b'receipt')):
                    with self.assertRaises(RegistryError):
                        operation()
                with closing(sqlite3.connect(self.path)) as db:
                    self.assertEqual(db.execute('SELECT state FROM generations').fetchall(), [('ACTIVE',)])
                    db.execute(f'DROP TRIGGER {name}')
                    db.commit()
                SnapshotRegistry(self.path, self.registry_id)

    def test_malformed_token_does_not_escape_in_exception(self):
        owner = self.registry.acquire(self.task)
        bad_token = owner.owner_token + '\ud800'
        for operation in (self.registry.quarantine,
                          lambda value: self.registry.complete(value, b'receipt')):
            with self.assertRaises(RegistryError) as caught:
                operation(replace(owner, owner_token=bad_token))
            self.assertNotIn(owner.owner_token, repr(caught.exception))
            self.assertNotIn(bad_token, repr(caught.exception))
            self.assertIsNone(caught.exception.__cause__)
            self.assertIsNone(caught.exception.__context__)

    def test_commit_failure_before_and_after_commit(self):
        connect = self.registry._connect

        class FailingCommit:
            def __init__(self, db, after):
                self.db, self.after = db, after

            def __getattr__(self, name):
                return getattr(self.db, name)

            def commit(self):
                if self.after:
                    self.db.commit()
                raise sqlite3.OperationalError('simulated lost acknowledgment')

        for after in (False, True):
            task = str(uuid.uuid4())
            with self.subTest(after_commit=after):
                with patch.object(self.registry, '_connect', side_effect=lambda: FailingCommit(connect(), after)):
                    with self.assertRaises(sqlite3.OperationalError):
                        self.registry.acquire(task)
                with closing(sqlite3.connect(self.path)) as db:
                    rows = db.execute('SELECT state FROM generations WHERE task_id = ?', (task,)).fetchall()
                self.assertEqual(rows, [('ACTIVE',)] if after else [])
                if after:
                    unresolved = SnapshotRegistry(self.path, self.registry_id).unresolved(task)
                    self.assertEqual(unresolved[1], 'ACTIVE')
                    self.assertEqual(str(uuid.UUID(unresolved[0])), unresolved[0])
                    with self.assertRaises(RegistryError):
                        self.registry.acquire(task)
                else:
                    self.assertIsNone(SnapshotRegistry(self.path, self.registry_id).unresolved(task))

    def test_concurrent_processes_only_one_acquires(self):
        ctx = multiprocessing.get_context('spawn')
        start, results = ctx.Event(), ctx.Queue()
        workers = [ctx.Process(target=competing_acquire, args=(self.path, self.registry_id, self.task, start, results)) for _ in range(2)]
        for worker in workers:
            worker.start()
        start.set()
        for worker in workers:
            worker.join(30)
            self.assertEqual(worker.exitcode, 0)
        self.assertEqual(sorted(results.get(timeout=5) for _ in workers), ['acquired', 'blocked'])
        results.close()

    def test_crashed_owner_cannot_be_replaced_automatically(self):
        ctx = multiprocessing.get_context('spawn')
        acquired = ctx.Event()
        worker = ctx.Process(target=acquire_until_killed,
                             args=(self.path, self.registry_id, self.task, acquired))
        worker.start()
        try:
            self.assertTrue(acquired.wait(15))
            worker.terminate()
            worker.join(15)
            self.assertFalse(worker.is_alive())
            with self.assertRaises(RegistryError):
                SnapshotRegistry(self.path, self.registry_id).acquire(self.task)
        finally:
            if worker.is_alive():
                worker.terminate()
                worker.join(15)

    def test_all_credentials_required_for_each_mutation(self):
        owner = self.registry.acquire(self.task)
        other = self.registry.acquire(str(uuid.uuid4()))
        for wrong in (replace(owner, task_id=str(uuid.uuid4())),
                      replace(owner, generation_id=str(uuid.uuid4())),
                      replace(owner, task_id=other.task_id),
                      replace(owner, generation_id=other.generation_id),
                      replace(owner, owner_token=other.owner_token),
                      replace(owner, owner_token='wrong')):
            with self.assertRaises(RegistryError):
                self.registry.quarantine(wrong)
            with self.assertRaises(RegistryError):
                self.registry.complete(wrong, b'receipt')
        self.assertNotIn(owner.owner_token, repr(owner))
        with self.assertRaises(ValueError):
            self.registry.complete(owner, b'')
        with self.assertRaises(RegistryError):
            self.registry.acquire(self.task)

    def test_concurrent_completion_only_one_succeeds(self):
        owner = self.registry.acquire(self.task)
        ctx = multiprocessing.get_context('spawn')
        start, results = ctx.Event(), ctx.Queue()
        workers = [ctx.Process(target=competing_complete,
                               args=(self.path, self.registry_id, owner, start, results)) for _ in range(2)]
        for worker in workers:
            worker.start()
        start.set()
        for worker in workers:
            worker.join(30)
            self.assertEqual(worker.exitcode, 0)
        self.assertEqual(sorted(results.get(timeout=5) for _ in workers), ['blocked', 'completed'])
        results.close()

    def test_replaced_database_rejected_on_mutation(self):
        owner = self.registry.acquire(self.task)
        replacement = str(Path(self.temp.name) / 'replacement.sqlite')
        SnapshotRegistry.provision(replacement)
        os.replace(replacement, self.path)
        for operation in (lambda: self.registry.acquire(self.task),
                          lambda: self.registry.quarantine(owner),
                          lambda: self.registry.complete(owner, b'receipt')):
            with self.assertRaises(RegistryError):
                operation()

    def test_quarantine_persists_and_blocks_until_explicit_completion(self):
        owner = self.registry.acquire(self.task)
        self.registry.quarantine(owner)
        restarted = SnapshotRegistry(self.path, self.registry_id)
        self.assertEqual(restarted.unresolved(self.task), (owner.generation_id, 'QUARANTINED'))
        self.assertIsNone(restarted.unresolved(str(uuid.uuid4())))
        with self.assertRaises(RegistryError):
            restarted.acquire(self.task)
        restarted.complete(owner, b'externally verified receipt')
        successor = SnapshotRegistry(self.path, self.registry_id).acquire(self.task)
        self.assertNotEqual(owner.generation_id, successor.generation_id)
        self.assertNotEqual(owner.owner_token, successor.owner_token)
        with self.assertRaises(RegistryError):
            restarted.complete(owner, b'another receipt')


if __name__ == '__main__':
    unittest.main()
