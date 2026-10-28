"""Durable M2 manual fixture lifecycle. State and credentials stay outside Git."""
import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import signal
import shutil
import socket
import sqlite3
import sys
import uuid

from m1 import ROOT, PLUGIN, check, run
from m2 import M2Instance

SESSIONS = Path(os.environ.get('FORGE_MANUAL_TEST_ROOT', ROOT / 'artifacts/m2/manual')).resolve()
if not SESSIONS.is_relative_to((ROOT / 'artifacts').resolve()):
    raise ValueError('manual test root must remain inside repository artifacts')
DEV = ROOT / 'deploy/docker/dev/config'


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def write_private(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + '.tmp')
    fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, 'w') as stream:
        json.dump(value, stream, indent=2)
        stream.flush()
        os.fsync(stream.fileno())
    temporary.replace(path)


def dev_snapshot():
    result = {'tables': {}, 'files': {}}
    database = DEV / 'data/Danmuku/danmuku.db'
    if database.exists():
        with sqlite3.connect(database.as_uri() + '?mode=ro', uri=True) as db:
            db.execute('BEGIN')
            tables = [r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")]
            for table in sorted(tables):
                if table in ('PlaybackRequests',):
                    continue  # Playback caches expire across an ordinary restart.
                quoted = '"' + table.replace('"', '""') + '"'
                columns = len(db.execute('SELECT * FROM ' + quoted + ' LIMIT 0').description)
                h, count = hashlib.sha256(), 0
                for row in db.execute('SELECT * FROM ' + quoted + ' ORDER BY ' + ','.join(str(i + 1) for i in range(columns))):
                    h.update(json.dumps(row, ensure_ascii=False, default=str).encode() + b'\n')
                    count += 1
                result['tables'][table] = {'count': count, 'sha256': h.hexdigest()}
    for directory in ('plugins/configurations', 'data/Danmuku/originals', 'data/Danmuku/staging'):
        for file in sorted((DEV / directory).rglob('*')):
            if file.is_file():
                result['files'][str(file.relative_to(DEV))] = digest(file)
    return result


def dev_running():
    return run('docker', 'ps', '-q', '--filter', 'label=com.docker.compose.project=jellyfin-forge-dev', capture_output=True).stdout.split()


def port_available(allowed=()):
    with socket.socket() as probe:
        probe.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            probe.bind(('127.0.0.1', 18096))
            return True
        except OSError:
            if allowed:
                owners = run('docker', 'ps', '-q', '--filter', 'publish=18096', capture_output=True).stdout.split()
                if owners and set(owners) <= set(allowed):
                    return True
            raise RuntimeError('Port 18096 is occupied outside this session; no service was stopped.')


def load(session):
    check(bool(re.fullmatch('[0-9a-f]{12}', session)), 'invalid session ID')
    path = SESSIONS / session / 'state.json'
    state = json.loads(path.read_text())
    check(state['session'] == session and state['project'] == 'jellyfin-forge-m2-manual-' + session, 'session identity mismatch')
    return path, state


def instance_for(state):
    instance = M2Instance()
    instance.media_fixture_version = state.get("fixtureVersion")
    instance.package_path = Path(state["package"]) if state.get("package") else None
    instance.project = state['project']
    instance.data = SESSIONS / state['session']
    instance.command = ['docker', 'compose', '-f', str(instance.data / 'compose.json')]
    instance.restore = state['restore']
    instance.password = json.loads((instance.data / 'bootstrap-secret.json').read_text())['password']
    return instance


def development_account(username):
    """Read the existing local password verifier without exposing plaintext."""
    database = DEV / 'data/jellyfin.db'
    with sqlite3.connect(database.as_uri() + '?mode=ro', uri=True) as db:
        db.row_factory = sqlite3.Row
        row = db.execute('SELECT Username, NormalizedUsername, Password, AuthenticationProviderId, '
                         'PasswordResetProviderId FROM Users WHERE Username = ?', (username,)).fetchone()
    check(row is not None, 'development account does not exist')
    check(row['AuthenticationProviderId'] == 'Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider',
          'only the built-in local authentication provider is supported')
    check(bool(row['Password']), 'development account must have an existing password')
    return dict(row)


def replace_manual_admin(database, user_id, account):
    """Caller must stop the owned server and back up its database first."""
    with sqlite3.connect(database) as db:
        db.execute('BEGIN IMMEDIATE')
        existing = db.execute('SELECT Id FROM Users WHERE lower(replace(Id, \'-\', \'\')) = ?',
                              (user_id.replace('-', '').lower(),)).fetchone()
        check(existing is not None, 'manual administrator no longer exists')
        check(not db.execute('SELECT 1 FROM Users WHERE NormalizedUsername = ? AND Id != ?',
                             (account['NormalizedUsername'], existing[0])).fetchone(), 'account name already belongs to another user')
        db.execute('UPDATE Users SET Username = ?, NormalizedUsername = ?, Password = ?, '
                   'AuthenticationProviderId = ?, PasswordResetProviderId = ?, '
                   'MustUpdatePassword = 0, InvalidLoginAttemptCount = 0, RowVersion = RowVersion + 1 WHERE Id = ?',
                   (*(account[key] for key in ('Username', 'NormalizedUsername', 'Password',
                      'AuthenticationProviderId', 'PasswordResetProviderId')), existing[0]))


def use_dev_account(session, username, remember=True):
    """Reuse a local development login while retaining this fixture's user ID and data."""
    path, state = load(session)
    check(state['phase'] == 'ready', 'account handoff requires a ready manual session')
    account = development_account(username)
    instance = instance_for(state)
    check_compose(instance)
    info = instance.request('/System/Info/Public')
    check(info['ServerName'] == 'M2 manual isolated ' + session, 'wrong server on the manual port')
    credentials_path = instance.data / 'credentials.json'
    credentials = json.loads(credentials_path.read_text())
    user = instance.request('/Users/Me', token=credentials['admin']['token'])
    check(user['Policy']['IsAdministrator'], 'manual token is not an administrator')
    database = instance.data / 'config/data/jellyfin.db'
    backup = instance.data / 'account-backups' / uuid.uuid4().hex[:12]
    backup.mkdir(parents=True, mode=0o700)
    write_private(backup / 'credentials.json', credentials)
    write_private(backup / 'state.json', state)
    run(*instance.command, 'stop', 'jellyfin', capture_output=True)
    try:
        running = run('docker', 'ps', '-q', '--filter', 'label=com.docker.compose.project=' + instance.project,
                      capture_output=True).stdout.strip()
        check(not running, 'manual server is still running; refusing a database change')
        backup_db = backup / 'jellyfin.db'
        fd = os.open(backup_db, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        os.close(fd)
        with sqlite3.connect(database.as_uri() + '?mode=ro', uri=True) as source, sqlite3.connect(backup_db) as target:
            source.backup(target)
        replace_manual_admin(database, user['Id'], account)
        credentials['admin'].pop('password', None)
        credentials['admin'].update(username=account['Username'], passwordSource='existing-development-account')
        write_private(credentials_path, credentials)
        state['reuseDevAccount'] = account['Username']
        state['adminAccount'] = {'username': account['Username'], 'passwordSource': 'existing-development-account'}
        write_private(path, state)
    finally:
        run(*instance.command, 'start', 'jellyfin', capture_output=True)
    instance.wait(lambda: instance.request('/health', raw=True) == b'Healthy')
    current = instance.wait(lambda: instance.request('/Users/Me', token=credentials['admin']['token']))
    check(current['Id'] == user['Id'] and current['Name'] == account['Username'] and current['Policy']['IsAdministrator'],
          'administrator identity was not preserved')
    instance.wait(lambda: instance.request('/Danmuku/Files?limit=1', token=credentials['admin']['token']))
    if remember:
        write_private(SESSIONS / 'local-account.json', {'username': account['Username']})
    return status(session)


def check_compose(instance):
    compose = json.loads((instance.data / 'compose.json').read_text())
    check(compose['name'] == instance.project, 'compose project differs from session')
    check(set(compose['services']) == {'jellyfin'}, 'unexpected session services')
    service = compose['services']['jellyfin']
    check(service['ports'] == ['127.0.0.1:18096:8096'], 'fixture port changed')
    # Existing sessions retain their recorded limits, independent of this host's environment.
    if 'cpus' in service:
        check(float(service['cpus']) > 0, 'invalid fixture CPU limit')
    if 'mem_limit' in service:
        check(bool(re.fullmatch(r'[1-9][0-9]*[bkmgBKMG]?', str(service['mem_limit']))), 'invalid fixture memory limit')
    check(all(':ro' in v for v in service['volumes'] if ':/media/' in v), 'media is not read-only')


def recover_accounts(instance):
    """Finish an interrupted setup using the persisted password, never reset users."""
    info = instance.wait(lambda: instance.request('/System/Info/Public'))
    if not info.get('StartupWizardCompleted'):
        instance.wait(lambda: instance.request('/Startup/Configuration'))
        instance.request('/Startup/Configuration', {'ServerName': 'M2 manual isolated', 'UICulture': 'en-US',
                         'MetadataCountryCode': 'US', 'PreferredMetadataLanguage': 'en'}, expected=204)
        instance.wait(lambda: instance.request('/Startup/User'))
        instance.request('/Startup/User', {'Name': 'm1-admin', 'Password': instance.password}, expected=204)
        instance.request('/Startup/RemoteAccess', {'EnableRemoteAccess': False}, expected=204)
        instance.request('/Startup/Complete', {}, expected=204)
    login = instance.wait(lambda: instance.request('/Users/AuthenticateByName', {'Username': 'm1-admin', 'Pw': instance.password}))
    instance.admin, instance.admin_id = login['AccessToken'], login['User']['Id']
    users = instance.request('/Users', token=instance.admin)
    viewer = next((u for u in users if u['Name'] == 'm1-viewer'), None)
    if viewer is None:
        viewer = instance.request('/Users/New', {'Name': 'm1-viewer', 'Password': instance.password}, instance.admin)
    instance.viewer_id = viewer['Id']
    instance.viewer = instance.request('/Users/AuthenticateByName', {'Username': 'm1-viewer', 'Pw': instance.password})['AccessToken']
    folders = instance.request('/Library/VirtualFolders', token=instance.admin)
    instance.scan_library('M2' if not folders else None)
    items = instance.wait(lambda: (lambda rows: rows if len(rows) == 2 else None)(instance.request(
        '/Items?Recursive=true&IncludeItemTypes=Movie', token=instance.admin)['Items']))
    items.sort(key=lambda row: row['Name'])
    instance.item, instance.second = items[0]['Id'], items[1]['Id']
    config = instance.request('/Plugins/' + PLUGIN + '/Configuration', token=instance.admin)
    config['WebEnabled'] = True
    instance.request('/Plugins/' + PLUGIN + '/Configuration', config, instance.admin, expected=204)
    write_private(instance.data / 'credentials.json', {'base': instance.base, 'item': instance.item, 'secondItem': instance.second,
        'admin': {'username': 'm1-admin', 'password': instance.password, 'token': instance.admin},
        'viewer': {'username': 'm1-viewer', 'password': instance.password, 'token': instance.viewer}})


def seed(instance, state, path):
    config = instance.request('/System/Configuration', token=instance.admin)
    config['ServerName'] = 'M2 manual isolated ' + state['session']
    instance.request('/System/Configuration', config, instance.admin, expected=204)
    state.setdefault('sources', {})
    for label in ('A', 'B', 'C', 'F'):
        file = instance.data / 'samples' / (label + '.json')
        file.parent.mkdir(exist_ok=True)
        if not file.exists():
            times = [0, 1, 599999, 600000, 600001, 720000, 1199999, 1200000, 1800000]
            values = [{'progress': t, 'content': label + ' @' + str(t) + (' source 12:00' if t == 720000 else ''),
                       'mode': [1, 4, 5][i % 3], 'fontsize': 25, 'color': 16777215} for i, t in enumerate(times)]
            values += [{'progress': 720000, 'content': 'duplicate text', 'mode': 1}] * 2
            file.write_text(json.dumps(values, ensure_ascii=False))
        if label in state['sources']:
            continue
        # Independent import only; user creates bindings and plans during H01-H10.
        batch = instance.api('/Danmuku/ImportBatches', {'batchId': uuid.uuid4().hex, 'operation': 'import', 'fileNames': [file.name]})
        task = instance.api('/Danmuku/ImportBatches/' + batch['batchid'] + '/Files/0', file.read_bytes(), expected=202)
        done = instance.terminal(task['taskid'])
        check(done['Status'] == 'Completed', 'manual sample import did not complete')
        state['sources'][label] = {'fileId': done['FileId'], 'sha256': digest(file)}
        write_private(path, state)
    state.update(phase='ready', media={'V1': instance.item, 'V2': instance.second})
    if state.get('fixtureVersion'):
        if not (instance.data / 'mb-fixtures.json').exists():
            from m2_media import provision
            provision(instance)
        state['mediaFixtures'] = json.loads((instance.data / 'mb-fixtures.json').read_text())
    write_private(path, state)


def prepare(package):
    active = SESSIONS / 'active.json'
    if active.exists():
        raise RuntimeError('An active session exists; use status/resume with ' + json.loads(active.read_text())['session'])
    package = package.resolve()
    expected = Path(os.environ.get('FORGE_PACKAGE_DIR', ROOT / 'artifacts')) / 'jellyfin-plugin-danmuku-0.0.2.zip'
    check(package.is_file() and digest(package) == digest(expected), 'package must equal the tested repository package')
    preference = SESSIONS / 'local-account.json'
    username = json.loads(preference.read_text())['username'] if preference.exists() else None
    if username:
        development_account(username)  # Validate before creating or stopping anything.
    restore = dev_running()
    port_available(restore)
    session = uuid.uuid4().hex[:12]
    path = SESSIONS / session / 'state.json'
    path.parent.mkdir(parents=True, exist_ok=True)
    snapshot = path.parent / package.name
    shutil.copy2(package, snapshot)
    check(digest(snapshot) == digest(package), 'session package copy mismatch')
    package = snapshot
    state = {'session': session, 'project': 'jellyfin-forge-m2-manual-' + session, 'phase': 'preparing',
             'package': str(package), 'packageSha256': digest(package), 'restore': restore, 'devBefore': dev_snapshot(), 'fixtureVersion': 'mb-v1'}
    if username:
        state['reuseDevAccount'] = username
    write_private(path, state)
    write_private(path.parent / 'bootstrap-secret.json', {'password': secrets.token_urlsafe(24)})
    write_private(active, {'session': session})
    instance = instance_for(state)
    # Instance creates directories itself. The parent with durable state already exists.
    try:
        instance.__enter__()
        seed(instance, state, path)
        if username:
            return use_dev_account(session, username, remember=False)
    except BaseException:
        state['phase'] = 'interrupted'
        write_private(path, state)
        instance.__exit__(None, None, None)
        raise
    return status(session)


def verify_resume_package(instance, state):
    # Legacy ready sessions referenced the mutable default build ZIP. Existing
    # compose/config resumes never extract that ZIP: retain the installed plugin.
    if (instance.data / 'compose.json').exists() and not state.get('fixtureVersion'):
        check((instance.data / 'config/plugins/Danmuku/Jellyfin.Plugin.Danmuku.dll').is_file(),
              'legacy installed plugin is missing; refusing to replace it from another package')
        return
    check(digest(Path(state['package'])) == state['packageSha256'],
          'package changed; do not replace an existing session package')


def resume(session):
    path, state = load(session)
    check(state['phase'] != 'finished', 'session finished; prepare a new session')
    instance = instance_for(state)
    verify_resume_package(instance, state)
    owned = run('docker', 'ps', '-q', '--filter', 'label=com.docker.compose.project=' + instance.project, capture_output=True).stdout.split()
    running = dev_running()
    port_available(owned + running)
    try:
        if not (instance.data / 'compose.json').exists():
            # No container or accounts have existed. Complete the same scaffold,
            # without removing a persistent directory or changing its identity.
            instance.resume_scaffold = True
            instance.__enter__()
        else:
            check_compose(instance)
            if running:
                run('docker', 'stop', *running, capture_output=True)
            run(*instance.command, 'up', '-d', capture_output=True)
            instance.wait(lambda: instance.request('/System/Info/Public'))
            instance.wait(lambda: instance.request('/health', raw=True) == b'Healthy')
            if (instance.data / 'credentials.json').exists():
                credentials = json.loads((instance.data / 'credentials.json').read_text())
                instance.admin, instance.viewer = credentials['admin']['token'], credentials['viewer']['token']
                instance.item, instance.second = credentials['item'], credentials['secondItem']
                # Public server info can be served before restart initialization
                # finishes. Wait for the authenticated plugin endpoint as well.
                instance.wait(lambda: instance.request('/Plugins', token=instance.admin))
                instance.wait(lambda: instance.request('/Danmuku/Files?limit=1', token=instance.admin))
            else:
                recover_accounts(instance)
        if state['phase'] != 'ready':
            seed(instance, state, path)
        if state.get('reuseDevAccount') and not state.get('adminAccount'):
            return use_dev_account(session, state['reuseDevAccount'], remember=False)
    except BaseException:
        state['phase'] = 'interrupted'
        write_private(path, state)
        instance.__exit__(None, None, None)
        raise
    return status(session)


def finish(session):
    path, state = load(session)
    if state['phase'] == 'finished':
        return status(session)
    instance = instance_for(state)
    state['phase'] = 'finishing'
    write_private(path, state)
    if (instance.data / 'compose.json').exists():
        check_compose(instance)
        log = run(*instance.command, 'logs', '--no-color', capture_output=True)
        (instance.data / 'server.log').write_text(log.stdout + log.stderr)
        run(*instance.command, 'down', '--remove-orphans', capture_output=True)
    after = dev_snapshot()
    state['devAfter'] = after
    check(after == state['devBefore'], 'development business data changed; retained evidence, refusing an unverified finish')
    for container in state['restore']:
        label = run('docker', 'inspect', '--format', '{{index .Config.Labels "com.docker.compose.project"}}', container, capture_output=True).stdout.strip()
        check(label == 'jellyfin-forge-dev', 'restore target is not this repository development instance')
        run('docker', 'start', container, capture_output=True)
    state['phase'] = 'finished'
    write_private(path, state)
    active = SESSIONS / 'active.json'
    if active.exists() and json.loads(active.read_text())['session'] == session:
        active.unlink()
    return status(session)


def status(session):
    _, state = load(session)
    instance = instance_for(state)
    ready = False
    if state['phase'] == 'ready':
        try:
            info = instance.request('/System/Info/Public')
            if info['ServerName'] == 'M2 manual isolated ' + session:
                credentials = json.loads((instance.data / 'credentials.json').read_text())
                instance.request('/Danmuku/Files?limit=1', token=credentials['admin']['token'])
                ready = True
        except (OSError, AssertionError):
            pass
    return {'session': session, 'phase': state['phase'], 'ready': ready, 'project': state['project'],
            'url': instance.base, 'packageSha256': state['packageSha256'], 'media': state.get('media'),
            'credentialsFile': str(instance.data / 'credentials.json'), 'fixtureVersion': state.get('fixtureVersion'),
            'mediaFixtures': state.get('mediaFixtures')}


def main():
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(143))
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=('prepare', 'status', 'resume', 'finish', 'use-dev-account'))
    parser.add_argument('--session')
    parser.add_argument('--package', type=Path)
    parser.add_argument('--username', help='existing development login for use-dev-account; remembered locally')
    args = parser.parse_args()
    SESSIONS.mkdir(parents=True, exist_ok=True)
    with (SESSIONS / 'lifecycle.lock').open('w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if args.action == 'prepare':
            if args.package is None:
                parser.error('prepare requires --package')
            result = prepare(args.package)
        else:
            if not args.session:
                parser.error('this command requires --session')
            if args.action == 'use-dev-account':
                if not args.username:
                    parser.error('use-dev-account requires --username')
                result = use_dev_account(args.session, args.username)
            else:
                result = globals()[args.action](args.session)
        print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
