import copy
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

import m2_manual
from m1 import Instance
from performance import GROUPS, summarize, validate


def passing(group):
    def record(maximum, width):
        samples = [{'phase': 'committed', 'elapsed': 10, 'playbackId': str(i)} for i in range(20)]
        before = dict(time=120, total=0, dropped=0)
        after = dict(time=720, total=14400, dropped=0, indexBytes=1000, peakActive=600)
        return dict(maximum=maximum, viewport=dict(width=width, height=900 if width == 1440 else 844),
                    speed=group.get('speed', 1), load=copy.deepcopy(samples), settings=copy.deepcopy(samples), seek=copy.deepcopy(samples),
                    cleanup=[dict(canvases=0, listeners=0, frames=0, selected=0, requests=0, observers=0, indexBytes=0, timers=1)] * 20,
                    frames=[dict(enabled=b, before=dict(before), after=dict(after), progress=[{}] * 10) for b in (False, True)],
                    source=dict(segments=50, candidateCount=1000000, profileVersion='m2-pressure-v3',
                                denseSourceCount=19920, tailSourceCount=80, denseDurationMs=60000,
                                burstIntervalMs=4000, selectedDenseBursts=[1067] * 14 + [1062]),
                    backend=[dict(candidateCount=1000000, scannedCandidates=2000000)] * 21,
                    sustainedHighLoadSeconds=40)
    records = [record(group['maximum'], group['viewport']['width'])] if group['milestone'] == 'm2' else [record(m, w) for m in (False, True) for w in (1440, 390)]
    return dict(status='passed', packageSha256='a' * 64, group=group, measurements=records)


class PerformanceToolsTests(unittest.TestCase):
    def test_manifest_is_complete_and_unique(self):
        self.assertEqual(26, len(GROUPS))
        self.assertEqual(26, len({g['id'] for g in GROUPS}))
        self.assertEqual(24, len({(g['baseUrl'], g['viewport']['width'], g['maximum'], g['speed']) for g in GROUPS if g['milestone'] == 'm2'}))

    def test_aggregator_rejects_missing_failed_cancelled_and_mixed_packages(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for group in GROUPS:
                target = root / group['id'] / 'group-performance.json'
                target.parent.mkdir()
                target.write_text(json.dumps(passing(group)))
            self.assertEqual(0, summarize(root, root / 'summary.json'))
            target = root / GROUPS[0]['id'] / 'group-performance.json'
            for change in [{'status': 'failed'}, {'status': 'cancelled'}, {'packageSha256': 'b' * 64}, {'measurements': None}]:
                data = passing(GROUPS[0]); data.update(change)
                target.write_text(json.dumps(data))
                self.assertEqual(1, summarize(root, root / 'summary.json'))
            target.unlink()
            self.assertEqual(1, summarize(root, root / 'summary.json'))

    def test_false_green_measurements_are_rejected(self):
        group = GROUPS[0]
        mutations = [lambda r: r['load'].pop(), lambda r: r['cleanup'].pop(),
                     lambda r: r['frames'].pop(), lambda r: r['source'].update(candidateCount=20000),
                     lambda r: r['load'][0].update(phase='cancelled'),
                     lambda r: r['settings'][0].update(elapsed=1000) or r['settings'][1].update(elapsed=1000),
                     lambda r: r['frames'][1]['after'].update(time=719),
                     lambda r: r['backend'].clear()]
        for mutate in mutations:
            data = copy.deepcopy(passing(group))
            mutate(data['measurements'][0])
            with self.assertRaises(AssertionError):
                validate(data, group)

    def test_driver_failure_propagates_and_writes_failed_evidence(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / 'artifacts').mkdir()
            (root / 'artifacts/jellyfin-plugin-danmuku-0.0.2.zip').write_bytes(b'fixture')
            code = '''import sys
from pathlib import Path
import m2
m2.ROOT = Path(sys.argv[1])
class Broken:
    def __init__(self, prefix): self.data = m2.ROOT / 'failed-fixture'
    def __enter__(self): return self
    def __exit__(self, *args): return False
    def plan_http_tests(self): raise AssertionError('intentional M2 failure')
m2.M2Instance = Broken
sys.argv = ['m2.py']
m2.main()
'''
            outcome = subprocess.run([sys.executable, '-c', code, str(root)], cwd=Path(__file__).parent, capture_output=True, text=True)
            self.assertNotEqual(0, outcome.returncode)
            self.assertIn('intentional M2 failure', outcome.stderr)
            evidence = json.loads((root / 'failed-fixture/http-results.json').read_text())
            self.assertEqual('failed', evidence['status'])
            self.assertNotIn('token', json.dumps(evidence))

    def test_maximum_requires_current_stress_input_and_sustained_load(self):
        group = next(g for g in GROUPS if g.get('maximum'))
        for change in ({'sustainedHighLoadSeconds': 29.9}, {'source': dict(segments=50, candidateCount=1000000)}):
            data = passing(group)
            data['measurements'][0].update(change)
            with self.assertRaises(AssertionError):
                validate(data, group)
        data = passing(group)
        bursts = data['measurements'][0]['source']['selectedDenseBursts']
        bursts[0] -= 468
        bursts[-1] += 468  # The total is unchanged; one pressure interval is missing.
        with self.assertRaisesRegex(AssertionError, 'under-filled'):
            validate(data, group)


class ManualToolsTests(unittest.TestCase):
    def test_reused_login_preserves_identity_other_users_and_permissions(self):
        with tempfile.TemporaryDirectory() as temp:
            database = Path(temp) / 'jellyfin.db'
            with sqlite3.connect(database) as db:
                db.execute('CREATE TABLE Users (Id TEXT PRIMARY KEY, Username TEXT, NormalizedUsername TEXT UNIQUE, '
                           'Password TEXT, AuthenticationProviderId TEXT, PasswordResetProviderId TEXT, '
                           'MustUpdatePassword INTEGER, InvalidLoginAttemptCount INTEGER, RowVersion INTEGER)')
                db.executemany('INSERT INTO Users VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)', [
                    ('aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee', 'fixture-admin', 'FIXTURE-ADMIN', 'old', 'provider', 'reset', 0, 2, 7),
                    ('viewer-id', 'viewer', 'VIEWER', 'viewer-password', 'provider', 'reset', 0, 0, 3)])
                db.execute('CREATE TABLE Permissions(UserId TEXT, IsAdministrator INTEGER)')
                db.execute("INSERT INTO Permissions VALUES ('aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee', 1)")
                before_viewer = db.execute("SELECT * FROM Users WHERE Id = 'viewer-id'").fetchone()
                before_permissions = db.execute('SELECT * FROM Permissions').fetchall()
            account = dict(Username='existing-admin', NormalizedUsername='EXISTING-ADMIN', Password='source-verifier',
                           AuthenticationProviderId='provider', PasswordResetProviderId='reset')
            m2_manual.replace_manual_admin(database, 'aaaaaaaabbbbccccddddeeeeeeeeeeee', account)
            with sqlite3.connect(database) as db:
                self.assertEqual(('aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee', 'existing-admin', 'EXISTING-ADMIN',
                                  'source-verifier', 'provider', 'reset', 0, 0, 8), db.execute(
                                      "SELECT * FROM Users WHERE Username = 'existing-admin'").fetchone())
                self.assertEqual(before_viewer, db.execute("SELECT * FROM Users WHERE Id = 'viewer-id'").fetchone())
                self.assertEqual(before_permissions, db.execute('SELECT * FROM Permissions').fetchall())
                before_collision = db.execute('SELECT * FROM Users ORDER BY Id').fetchall()
            account.update(Username='viewer', NormalizedUsername='VIEWER')
            with self.assertRaisesRegex(AssertionError, 'another user'):
                m2_manual.replace_manual_admin(database, 'aaaaaaaabbbbccccddddeeeeeeeeeeee', account)
            with sqlite3.connect(database) as db:
                self.assertEqual(before_collision, db.execute('SELECT * FROM Users ORDER BY Id').fetchall())

    def test_development_login_is_read_only_and_rejects_external_authentication(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(m2_manual, 'DEV', Path(temp)):
            database = Path(temp) / 'data/jellyfin.db'
            database.parent.mkdir()
            with sqlite3.connect(database) as db:
                db.execute('CREATE TABLE Users (Username TEXT, NormalizedUsername TEXT, Password TEXT, '
                           'AuthenticationProviderId TEXT, PasswordResetProviderId TEXT)')
                db.execute('INSERT INTO Users VALUES (?, ?, ?, ?, ?)', ('existing', 'EXISTING', 'stored-verifier',
                           'Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider', 'reset'))
                db.execute("INSERT INTO Users VALUES ('external', 'EXTERNAL', 'opaque', 'LDAP', 'reset')")
            before = database.read_bytes()
            self.assertEqual('stored-verifier', m2_manual.development_account('existing')['Password'])
            with self.assertRaisesRegex(AssertionError, 'built-in'):
                m2_manual.development_account('external')
            with self.assertRaisesRegex(AssertionError, 'does not exist'):
                m2_manual.development_account('missing')
            self.assertEqual(before, database.read_bytes())

    def test_library_setup_waits_for_cancellation_and_requests_one_scan(self):
        instance = Instance()
        instance.admin = 'test-token'
        states = iter(('Running', 'Cancelling', 'Idle', 'Cancelling', 'Idle'))
        calls = []

        def request(path, *args, **kwargs):
            calls.append(path)
            if path == '/ScheduledTasks':
                return [{'Key': 'RefreshLibrary', 'State': next(states)}]

        instance.request = request
        with patch('m1.time.sleep'):
            instance.scan_library('M1')
        self.assertEqual(['/ScheduledTasks'] * 3 + [
            '/Library/VirtualFolders?name=M1&collectionType=movies&paths=%2Fmedia&refreshLibrary=false',
            '/ScheduledTasks', '/ScheduledTasks', '/Library/Refresh'], calls)

    def test_library_resume_waits_for_scan_without_recreating_folder(self):
        instance = Instance()
        instance.admin = 'test-token'
        instance.request = Mock(side_effect=[
            [{'Key': 'RefreshLibrary', 'State': 'Running'}],
            [{'Key': 'RefreshLibrary', 'State': 'Idle'}], None])
        with patch('m1.time.sleep'):
            instance.scan_library()
        self.assertEqual(['/ScheduledTasks', '/ScheduledTasks', '/Library/Refresh'],
                         [call.args[0] for call in instance.request.call_args_list])

    def test_status_requires_healthy_plugin_and_does_not_authenticate_foreign_server(self):
        from types import SimpleNamespace
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            m2_manual.write_private(root / 'credentials.json', {'admin': {'token': 'fixture-token'}})
            session = 'a' * 12
            state = dict(session=session, phase='ready', project='jellyfin-forge-m2-manual-' + session,
                         packageSha256='a' * 64)
            request = Mock(side_effect=[{'ServerName': 'M2 manual isolated ' + session}, AssertionError('storage unavailable')])
            instance = SimpleNamespace(data=root, base='http://127.0.0.1:18096', request=request)
            with patch.object(m2_manual, 'load', return_value=(root / 'state.json', state)), patch.object(m2_manual, 'instance_for', return_value=instance):
                self.assertFalse(m2_manual.status(session)['ready'])
                request.reset_mock(side_effect=True)
                request.return_value = {'ServerName': 'unrelated service'}
                self.assertFalse(m2_manual.status(session)['ready'])
                request.assert_called_once_with('/System/Info/Public')

    def test_private_state_is_atomic_and_mode_600(self):
        with tempfile.TemporaryDirectory() as temp:
            target = Path(temp) / 'state.json'
            m2_manual.write_private(target, {'phase': 'preparing'})
            m2_manual.write_private(target, {'phase': 'ready'})
            self.assertEqual(0o600, target.stat().st_mode & 0o777)
            self.assertEqual({'phase': 'ready'}, json.loads(target.read_text()))
            self.assertFalse(target.with_suffix('.json.tmp').exists())

    def test_repeat_prepare_never_overwrites_active_session(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(m2_manual, 'SESSIONS', Path(temp)), patch.object(m2_manual, 'run') as run:
            m2_manual.write_private(Path(temp) / 'active.json', {'session': 'a' * 12})
            with self.assertRaisesRegex(RuntimeError, 'active session'):
                m2_manual.prepare(Path('unused.zip'))
            run.assert_not_called()

    def test_foreign_port_fails_without_stopping_anything(self):
        with patch('m2_manual.socket.socket') as socket_type, patch.object(m2_manual, 'run') as run:
            socket_type.return_value.__enter__.return_value.bind.side_effect = OSError('occupied')
            with self.assertRaisesRegex(RuntimeError, 'occupied outside'):
                m2_manual.port_available()
            run.assert_not_called()

    def test_session_path_cannot_escape(self):
        with self.assertRaises(AssertionError):
            m2_manual.load('../outside')

    def test_snapshot_reads_business_tables_and_originals(self):
        import sqlite3
        with tempfile.TemporaryDirectory() as temp, patch.object(m2_manual, 'DEV', Path(temp)):
            directory = Path(temp) / 'data/Danmuku'
            (directory / 'originals').mkdir(parents=True)
            original = directory / 'originals/a.json'; original.write_text('[]')
            with sqlite3.connect(directory / 'danmuku.db') as db:
                db.execute('CREATE TABLE Files(Id TEXT, Name TEXT)')
                db.execute("INSERT INTO Files VALUES('a','example')")
            before = m2_manual.dev_snapshot()
            self.assertEqual(1, before['tables']['Files']['count'])
            original.write_text('[1]')
            self.assertNotEqual(before, m2_manual.dev_snapshot())


class ResourceConfigurationTests(unittest.TestCase):
    def test_legacy_resume_uses_existing_program_without_replacing_it_from_new_zip(self):
        from types import SimpleNamespace
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / 'compose.json').write_text('{}')
            dll = root / 'config/plugins/Danmuku/Jellyfin.Plugin.Danmuku.dll'
            dll.parent.mkdir(parents=True); dll.write_bytes(b'original installed program')
            package = root / 'build.zip'; package.write_bytes(b'new build')
            state = dict(package=str(package), packageSha256='old package hash')
            instance = SimpleNamespace(data=root)
            m2_manual.verify_resume_package(instance, state)
            self.assertEqual(b'original installed program', dll.read_bytes())
            self.assertEqual(b'new build', package.read_bytes())
            with self.assertRaisesRegex(AssertionError, 'package changed'):
                m2_manual.verify_resume_package(instance, dict(state, fixtureVersion='mb-v1'))
            dll.unlink()
            with self.assertRaisesRegex(AssertionError, 'installed plugin is missing'):
                m2_manual.verify_resume_package(instance, state)

    def test_container_limits_are_opt_in(self):
        from test_resources import compose_limits
        with patch.dict(os.environ, {}, clear=True):
            self.assertEqual({}, compose_limits())
        with patch.dict(os.environ, {'FORGE_TEST_CPUS': '2', 'FORGE_TEST_MEMORY': '3g'}, clear=True):
            self.assertEqual({'cpus': 2.0, 'mem_limit': '3g'}, compose_limits())
        for value in ['0', '-1', 'nan', 'inf']:
            with patch.dict(os.environ, {'FORGE_TEST_CPUS': value}, clear=True), self.assertRaises(ValueError):
                compose_limits()

    def test_old_manual_session_does_not_acquire_new_media_fixture(self):
        state = dict(project='jellyfin-forge-m2-manual-aaaaaaaaaaaa', session='aaaaaaaaaaaa', restore=[])
        with patch.object(Path, 'read_text', return_value='{"password":"test"}'):
            self.assertIsNone(m2_manual.instance_for(state).media_fixture_version)
            self.assertEqual('mb-v1', m2_manual.instance_for(dict(state, fixtureVersion='mb-v1')).media_fixture_version)


if __name__ == '__main__':
    unittest.main()
