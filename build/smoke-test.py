"""Load each plugin alone and together in disposable, isolated Jellyfin instances."""
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
NAMES = ('Danmuku', 'AgentBridge')
IMAGE = 'jellyfin/jellyfin:12.1'


def run(*args, **kwargs):
    return subprocess.run(args, check=True, text=True, **kwargs)


def check(condition, message):
    if not condition:
        raise RuntimeError(message)


def expected_danmuku_schema_version():
    source = (ROOT / 'src/Jellyfin.Plugin.Danmuku/Storage/SchemaMigrations.cs').read_text()
    match = re.search(r'CurrentVersion\s*=\s*(\d+)', source)
    if not match:
        raise RuntimeError('could not read Danmuku schema version from SchemaMigrations.cs')
    return int(match.group(1))


def read_danmuku_db(db_path):
    """Read SchemaVersion and journal_mode; fall back to a copy if WAL read-only fails."""
    check(Path(db_path).is_file(), f'Danmuku database not found: {db_path}')

    def query(target):
        connection = sqlite3.connect(target, uri=str(target).startswith('file:'))
        try:
            journal = connection.execute('PRAGMA journal_mode;').fetchone()[0]
            tables = [row[0] for row in connection.execute(
                "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")]
            version = connection.execute('SELECT Version FROM SchemaVersion WHERE Id = 1;').fetchone()[0]
            return journal, tables, version
        finally:
            connection.close()

    try:
        journal, tables, version = query(f'file:{db_path}?mode=ro')
    except sqlite3.Error:
        with tempfile.TemporaryDirectory() as tmp:
            for suffix in ('', '-wal', '-shm'):
                source = Path(str(db_path) + suffix)
                if source.exists():
                    shutil.copy2(source, Path(tmp) / (db_path.name + suffix))
            journal, tables, version = query(Path(tmp) / db_path.name)
    check('SchemaVersion' in tables, 'SchemaVersion table missing')
    return {'journalMode': journal, 'schemaVersion': version, 'tables': tables}


def scenario(names):
    tag = '-'.join(n.lower() for n in names)
    project = f'jellyfin-forge-smoke-{uuid.uuid4().hex[:10]}'
    data = ROOT / 'artifacts/smoke' / project
    for directory in ('config', 'cache', 'media'):
        (data / directory).mkdir(parents=True)
    ids = {}
    for name in names:
        xml = ET.parse(ROOT / f'src/Jellyfin.Plugin.{name}/Jellyfin.Plugin.{name}.csproj')
        ids[name] = xml.findtext('.//PluginGuid')
        version = xml.findtext('.//Version')
        with zipfile.ZipFile(ROOT / f'artifacts/jellyfin-plugin-{name.lower()}-{version}.zip') as archive:
            archive.extractall(data / 'config/plugins' / name)
    compose = data / 'compose.json'
    compose.write_text(json.dumps({'name': project, 'services': {'jellyfin': {
        'image': IMAGE, 'user': f'{os.getuid()}:{os.getgid()}',
        'ports': ['127.0.0.1:18096:8096'],
        'volumes': [f'{data}/config:/config', f'{data}/cache:/cache', f'{data}/media:/media:ro'],
    }}}))
    command = ['docker', 'compose', '-f', str(compose)]
    try:
        run(*command, 'up', '-d')
        address = run(*command, 'port', 'jellyfin', '8096', capture_output=True).stdout.strip()
        base = 'http://' + address
        auth = 'MediaBrowser Client="jellyfin-forge-smoke", Device="CI", DeviceId="forge-smoke", Version="0.1.0"'

        def request(path, body=None, token=None, expected=200):
            headers = {'Authorization': auth, 'Content-Type': 'application/json'}
            if token:
                headers['Authorization'] = auth + ', Token="' + token + '"'
            req = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(), headers=headers)
            try:
                with urllib.request.urlopen(req, timeout=10) as response:
                    status, content = response.status, response.read().decode()
            except urllib.error.HTTPError as error:
                status, content = error.code, error.read().decode()
            check(status == expected, f'{path}: expected {expected}, received {status}')
            try:
                return json.loads(content)
            except ValueError:
                return content

        for attempt in range(120):
            try:
                info = request('/System/Info/Public')
                request('/health')
                if isinstance(info, dict) and info.get('Version'):
                    break
            except (OSError, RuntimeError):
                pass
            time.sleep(2)
        else:
            raise RuntimeError('Jellyfin did not become ready within 240 seconds')
        password = secrets.token_urlsafe(32)
        # Jellyfin 12.1 exposes POST /Startup/User only after the initial
        # configuration has been submitted (same order as the Web wizard).
        startup_config = request('/Startup/Configuration')
        request('/Startup/Configuration', {
            'ServerName': 'jellyfin-forge-smoke',
            'UICulture': startup_config.get('UICulture') or 'en-US',
            'MetadataCountryCode': startup_config.get('MetadataCountryCode') or 'US',
            'PreferredMetadataLanguage': startup_config.get('PreferredMetadataLanguage') or 'en',
        }, expected=204)
        for attempt in range(30):
            try:
                # GET first: after the configuration reload POST /Startup/User
                # answers 404 on 12.1 until the route has been initialized.
                request('/Startup/User')
                request('/Startup/User', {'Name': 'forge-smoke', 'Password': password}, expected=204)
                break
            except RuntimeError as error:
                if attempt == 29:
                    raise
                time.sleep(2)
        request('/Startup/RemoteAccess', {'EnableRemoteAccess': False}, expected=204)
        request('/Startup/Complete', {}, expected=204)
        token = request('/Users/AuthenticateByName', {'Username': 'forge-smoke', 'Pw': password})['AccessToken']
        plugins = request('/Plugins', token=token)
        pages = request('/web/ConfigurationPages', token=token)
        for name in NAMES:
            if name not in names:
                check(not any(p['Name'] == name for p in plugins), f'{name} unexpectedly installed')
                request(f'/{name}/Health', token=token, expected=404)
                continue
            plugin = next(p for p in plugins if p['Name'] == name)
            check(uuid.UUID(plugin['Id']) == uuid.UUID(ids[name]), 'Plugin GUID mismatch')
            check(plugin['Status'] == 'Active', f'{name} is not Active')
            request(f'/{name}/Health', expected=401)
            health = request(f'/{name}/Health', token=token)
            check(health['Plugin'] == name and health['Status'] == 'ok', 'Invalid health response')
            check(any(p['Name'] == name for p in pages), 'Dashboard page not registered')
            html = request(f'/web/ConfigurationPage?name={name}', token=token)
            check(ids[name] in html and f'{name}ConfigForm' in html, 'Dashboard resource mismatch')
            endpoint = f'/Plugins/{ids[name]}/Configuration'
            config = request(endpoint, token=token)
            check(config['InstanceLabel'] == name, 'Unexpected initial configuration')
            config['InstanceLabel'] = f'{name} smoke 保存'
            request(endpoint, config, token, expected=204)
            check(request(endpoint, token=token)['InstanceLabel'] == config['InstanceLabel'], 'Configuration round-trip failed')

        danmuku_db = None
        if 'Danmuku' in names:
            expected_version = expected_danmuku_schema_version()
            # DanmukuDataPaths uses IApplicationPaths.DataPath directly.
            db_path = data / 'config/data/Danmuku/danmuku.db'
            for attempt in range(90):
                if db_path.is_file():
                    try:
                        danmuku_db = read_danmuku_db(db_path)
                        break
                    except (RuntimeError, sqlite3.Error):
                        pass
                time.sleep(2)
            check(danmuku_db is not None, 'Danmuku database was not created')
            check(danmuku_db['schemaVersion'] == expected_version,
                  f"unexpected schema version {danmuku_db['schemaVersion']} (expected {expected_version})")
            check(danmuku_db['journalMode'].lower() == 'wal',
                  f"unexpected journal mode {danmuku_db['journalMode']}")

        # Verify persistence across a server restart, not just an in-memory update.
        run(*command, 'restart', 'jellyfin')
        base = 'http://' + run(*command, 'port', 'jellyfin', '8096', capture_output=True).stdout.strip()
        for attempt in range(120):
            try:
                request('/health')
                request('/Plugins', token=token)
                break
            except (OSError, RuntimeError):
                time.sleep(2)
        else:
            raise RuntimeError('Restart timed out')
        for name in names:
            config = request(f'/Plugins/{ids[name]}/Configuration', token=token)
            check(config['InstanceLabel'] == f'{name} smoke 保存', 'Configuration did not persist')
        if danmuku_db is not None:
            after_restart = read_danmuku_db(data / 'config/data/Danmuku/danmuku.db')
            check(after_restart['schemaVersion'] == danmuku_db['schemaVersion'],
                  'Danmuku schema version changed after restart')
            danmuku_db = after_restart
        checks = ['load', 'health', 'anonymous rejected', 'dashboard resource',
                  'configuration persistence', 'sibling absence or coexistence']
        if danmuku_db is not None:
            checks.append('danmuku database schema version and WAL mode')
        result = {'scenario': tag, 'result': 'passed', 'image': IMAGE, 'plugins': ids,
                  'danmukuDb': danmuku_db, 'checks': checks}
        (data / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
        print(json.dumps(result), flush=True)
        return result
    finally:
        logs = subprocess.run([*command, 'logs', '--no-color'], text=True, capture_output=True)
        (data / 'server.log').write_text(logs.stdout + logs.stderr)
        run(*command, 'down', '--remove-orphans')


if __name__ == '__main__':
    results = [scenario(names) for names in [('Danmuku',), ('AgentBridge',), NAMES]]
    (ROOT / 'artifacts/smoke/results.json').write_text(json.dumps(results, indent=2) + '\n')
