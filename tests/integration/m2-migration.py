#!/usr/bin/env python3
"""Isolated v4 interruption, v5 upgrade and coordinated old-program recovery.

The supplied configuration is read only. Only its Danmuku program, plugin
configuration and data are copied into a disposable initialized Jellyfin.
"""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import select
import sqlite3
import sys
import time
import uuid
import zipfile

from m1 import ROOT, check, run
from m2 import M2Instance


def snapshot(root):
    result = {'tables': {}, 'files': {}}
    with sqlite3.connect((root / 'danmuku.db').as_uri() + '?mode=ro', uri=True) as db:
        db.execute('BEGIN')
        result['schema'] = db.execute('SELECT Version FROM SchemaVersion').fetchone()[0]
        check(db.execute('PRAGMA integrity_check').fetchone()[0] == 'ok', 'SQLite integrity failed')
        check(not db.execute('PRAGMA foreign_key_check').fetchall(), 'foreign keys failed')
        for (table,) in db.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT IN ('SchemaVersion','CombinePlans','CombineSegments') ORDER BY name").fetchall():
            quoted = '"' + table.replace('"', '""') + '"'
            columns = [r[1] for r in db.execute('PRAGMA table_info(' + quoted + ')') if r[1] not in ('ActivePlanId', 'SourceKind', 'PlanId', 'PlanVersion')]
            fields = ','.join('"' + c + '"' for c in columns)
            h = hashlib.sha256(); count = 0
            for row in db.execute('SELECT ' + fields + ' FROM ' + quoted + ' ORDER BY ' + ','.join(str(i + 1) for i in range(len(columns)))):
                h.update(json.dumps(row, ensure_ascii=False, default=str).encode() + b'\n'); count += 1
            result['tables'][table] = {'count': count, 'sha256': h.hexdigest()}
    for folder in ('originals', 'staging'):
        for file in sorted((root / folder).rglob('*')):
            if file.is_file():
                with file.open('rb') as stream:
                    result['files'][str(file.relative_to(root))] = hashlib.file_digest(stream, 'sha256').hexdigest()
    return result


def main():
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(143))
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--old-config', required=True, type=Path)
    args = parser.parse_args()
    old = args.old_config.resolve()
    check((old / 'plugins/Danmuku/Jellyfin.Plugin.Danmuku.dll').is_file(), 'old program is missing')
    check(snapshot(old / 'data/Danmuku')['schema'] == 4, 'requires an actual v4 consistency copy')
    source_before = snapshot(old / 'data/Danmuku')
    instance = M2Instance()
    instance.data = ROOT / 'artifacts/m2/migration' / instance.project
    instance.command = ['docker', 'compose', '-f', str(instance.data / 'compose.json')]
    result = {'status': 'failed', 'checks': []}
    try:
        with instance:
            run(*instance.command, 'stop', 'jellyfin', capture_output=True)
            data = instance.data / 'config/data/Danmuku'
            data.rename(instance.data / 'empty-v5-data')
            shutil.copytree(old / 'data/Danmuku', data)
            # Turn an old archived task-retention timestamp into a current test
            # fixture before the baseline. This does not touch the source copy.
            now = int(time.time() * 1000)
            with sqlite3.connect(data / 'danmuku.db') as db:
                for table in ('ImportTasks', 'ImportBatches', 'ImportSlots'):
                    db.execute('UPDATE ' + table + ' SET CreatedAtUtcMs=?, FinishedAtUtcMs=CASE WHEN FinishedAtUtcMs IS NULL THEN NULL ELSE ? END', (now, now))
                db.execute('UPDATE PlaybackRequests SET Status=\'Expired\'')
            plugin = instance.data / 'config/plugins/Danmuku'
            def install(source=None):
                if plugin.exists():
                    plugin.rename(instance.data / ('program-history-' + uuid.uuid4().hex[:8]))
                if source:
                    shutil.copytree(source, plugin)
                else:
                    with zipfile.ZipFile(ROOT / 'artifacts/jellyfin-plugin-danmuku-0.0.2.zip') as archive:
                        archive.extractall(plugin)
            install(old / 'plugins/Danmuku')
            configuration = old / 'plugins/configurations/Jellyfin.Plugin.Danmuku.xml'
            if configuration.exists():
                shutil.copy2(configuration, instance.data / 'config/plugins/configurations' / configuration.name)
            run(*instance.command, 'up', '-d', capture_output=True)
            instance.wait(lambda: instance.api('/Danmuku/Health')['version'] == '0.0.1.0')
            run(*instance.command, 'stop', 'jellyfin', capture_output=True)
            before = snapshot(data)
            check(before['schema'] == 4, 'old program did not use v4')
            restore_data = instance.data / 'v4-consistency-copy'
            shutil.copytree(data, restore_data)
            # SQLite online backup consolidates any retained WAL into the copy.
            for suffix in ('-wal', '-shm'):
                (restore_data / ('danmuku.db' + suffix)).unlink(missing_ok=True)
            with sqlite3.connect(data / 'danmuku.db') as source, sqlite3.connect(restore_data / 'danmuku.db') as target:
                source.backup(target)
            restore_config = instance.data / 'v4-plugin-config.xml'
            config_path = instance.data / 'config/plugins/configurations/Jellyfin.Plugin.Danmuku.xml'
            shutil.copy2(config_path, restore_config)
            install()
            # Stop only this fixture's process at the actual backup-file creation
            # event. An external SQLite write lock can prevent connection setup
            # itself and would not demonstrate interruption of a real migration.
            libc = ctypes.CDLL(None, use_errno=True)
            watch = libc.inotify_init1(os.O_NONBLOCK | os.O_CLOEXEC)
            check(watch >= 0, 'could not initialize migration checkpoint watcher')
            check(libc.inotify_add_watch(watch, os.fsencode(data / 'migrations'), 0x100) >= 0, 'could not watch migration directory')
            try:
                run(*instance.command, 'up', '-d', capture_output=True)
                owned = run(*instance.command, 'ps', '-q', 'jellyfin', capture_output=True).stdout.strip()
                pid = int(run('docker', 'inspect', '--format', '{{.State.Pid}}', owned, capture_output=True).stdout)
                check(pid > 1, 'invalid fixture process')
                check(select.select([watch], [], [], 40)[0], 'migration backup creation was not observed')
                event = os.read(watch, 8192)
                check(b'danmuku-v4-' in event, 'unexpected migration directory event')
                os.kill(pid, signal.SIGSTOP)
                check(snapshot(data) == before, 'checkpoint was reached after migration commit')
            finally:
                os.close(watch)
                # SIGKILL also releases a paused fixture on an assertion failure.
                run(*instance.command, 'kill', '-s', 'SIGKILL', 'jellyfin', capture_output=True)
            check(snapshot(data) == before, 'interrupted migration changed v4 business data')
            result['checks'].append('v4 interrupted at pre-migration backup creation, before commit; database and files unchanged')
            run(*instance.command, 'up', '-d', capture_output=True)
            instance.wait(lambda: instance.api('/Danmuku/Health')['version'] == '0.0.2.0')
            run(*instance.command, 'stop', 'jellyfin', capture_output=True)
            upgraded = snapshot(data)
            check(upgraded['schema'] == 5 and upgraded['tables'] == before['tables'] and upgraded['files'] == before['files'], 'v5 migration lost data')
            result['checks'].append('restart completes v4 to v5; all pre-existing table projections and original/staging hashes match')
            install(old / 'plugins/Danmuku')
            run(*instance.command, 'up', '-d', capture_output=True)
            instance.wait(lambda: 'newer than the version 4 supported by this plugin' in run(*instance.command, 'logs', '--no-color', capture_output=True).stdout, seconds=60)
            run(*instance.command, 'stop', 'jellyfin', capture_output=True)
            check(snapshot(data) == upgraded, 'old program modified v5 after refusal')
            result['checks'].append('actual 0.0.1 program refuses v5 without modifying it')
            data.rename(instance.data / 'v5-retained-after-refusal')
            shutil.copytree(restore_data, data)
            shutil.copy2(restore_config, config_path)
            run(*instance.command, 'up', '-d', capture_output=True)
            instance.wait(lambda: instance.api('/Danmuku/Health')['version'] == '0.0.1.0')
            run(*instance.command, 'stop', 'jellyfin', capture_output=True)
            check(snapshot(data) == before, 'coordinated recovery differs from v4 baseline')
            result['checks'].append('old program, plugin configuration, consistency database and original/staging data restored together')
            check(snapshot(old / 'data/Danmuku') == source_before, 'source backup was modified')
            result.update(status='passed', before=before, upgraded=upgraded, restored=snapshot(data))
    finally:
        instance.data.mkdir(parents=True, exist_ok=True)
        (instance.data / 'migration-results.json').write_text(json.dumps(result, indent=2) + '\n')
    print('PASS isolated migration and coordinated recovery:', instance.data)


if __name__ == '__main__':
    main()
