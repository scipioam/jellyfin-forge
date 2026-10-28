#!/usr/bin/env python3
"""Real isolated lifecycle rehearsal; no human acceptance actions are performed."""
import os
import json
from pathlib import Path
import signal
import socket
import subprocess
import sys
import time
import uuid

from m1 import ROOT, check, run
import m2_manual as manual


def main():
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(143))
    output = ROOT / 'artifacts/m2/manual-checks' / (uuid.uuid4().hex + '.json')
    original = manual.dev_running()
    before = manual.dev_snapshot()
    result = {'status': 'failed', 'checks': [], 'originalRunning': bool(original), 'before': before}
    active = None
    started = []
    command = [sys.executable, str(ROOT / 'tests/integration/m2-manual.py')]
    package = Path(os.environ.get('FORGE_PACKAGE_DIR', ROOT / 'artifacts')) / 'jellyfin-plugin-danmuku-0.0.2.zip'

    def call(action, session=None, expected=0):
        args = ['--package', str(package)] if action == 'prepare' else ['--session', session]
        done = subprocess.run([*command, action, *args], capture_output=True, text=True)
        check((done.returncode == 0) == (expected == 0), action + ': ' + done.stderr[-2000:])
        return json.loads(done.stdout) if done.returncode == 0 else None

    try:
        check(not (manual.SESSIONS / 'active.json').exists(), 'existing manual session must not be disturbed')
        if original:
            run('docker', 'stop', *original, capture_output=True)
        with socket.socket() as foreign:
            foreign.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            foreign.bind(('127.0.0.1', 18096)); foreign.listen()
            call('prepare', expected=1)
            check(not (manual.SESSIONS / 'active.json').exists(), 'foreign port failure created an active session')
        result['checks'].append('foreign owner on fixed port rejected before stopping or changing anything')
        first = call('prepare'); active = first['session']
        check(first['ready'] and call('status', active)['ready'], 'fixture did not survive prepare command exit')
        check(first['fixtureVersion'] == 'mb-v1' and len(first['mediaFixtures']['videos']) == 2, 'new media fixtures missing')
        path, state = manual.load(active)
        credentials = path.parent / 'credentials.json'
        credentials_hash = manual.digest(credentials)
        check(credentials.stat().st_mode & 0o777 == 0o600, 'credentials permissions')
        call('prepare', expected=1)
        check(manual.digest(credentials) == credentials_hash, 'repeat prepare overwrote credentials')
        instance = manual.instance_for(state)
        run(*instance.command, 'stop', 'jellyfin', capture_output=True)
        resumed = call('resume', active)
        check(resumed['ready'] and manual.digest(credentials) == credentials_hash, 'resume reset fixture identity')
        finish = call('finish', active)
        check(finish['phase'] == 'finished' and not manual.dev_running(), 'originally stopped development was started')
        log_hash = manual.digest(path.parent / 'server.log')
        call('finish', active)
        check(manual.digest(path.parent / 'server.log') == log_hash, 'repeat finish overwrote evidence')
        result['checks'].append('prepare persists; repeated prepare refused; stopped session resumes without account reset; finish is idempotent and preserves evidence')
        active = None
        # Now rehearse an actual process interruption while starting a new fixture.
        process = subprocess.Popen([*command, 'prepare', '--package', str(package)], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            deadline = time.monotonic() + 90
            while time.monotonic() < deadline:
                if (manual.SESSIONS / 'active.json').exists():
                    active = json.loads((manual.SESSIONS / 'active.json').read_text())['session']
                    path, state = manual.load(active)
                    if (path.parent / 'compose.json').exists():
                        break
                check(process.poll() is None, 'prepare exited before interruption point')
                time.sleep(.2)
            check(active is not None, 'prepare did not reach durable checkpoint')
            process.terminate(); process.communicate(timeout=60)
            check(call('resume', active)['ready'], 'interrupted prepare could not resume')
            call('finish', active); active = None
        finally:
            if process.poll() is None:
                process.terminate(); process.communicate(timeout=60)
        result['checks'].append('SIGTERM during prepare retains checkpoint; resume completes same session; finish restores stopped state')
        # This host may opt into resource limits; other development hosts choose their own.
        containers = run('docker', 'ps', '-aq', '--filter', 'label=com.docker.compose.project=jellyfin-forge-dev', capture_output=True).stdout.split()
        check(containers, 'development container missing for running-state rehearsal')
        for container in containers:
            limits = json.loads(run('docker', 'inspect', '--format', '{{json .HostConfig}}', container, capture_output=True).stdout)
            if os.environ.get('FORGE_TEST_MEMORY') == '1g':
                check(0 < limits['Memory'] <= 1024 ** 3, 'development container lacks requested local memory limit')
            if os.environ.get('FORGE_TEST_CPUS') == '1':
                check(0 < limits['NanoCpus'] <= 1000000000, 'development container lacks requested local CPU limit')
        run('docker', 'start', *containers, capture_output=True); started = containers
        deadline = time.monotonic() + 240
        while time.monotonic() < deadline:
            try:
                import urllib.request
                with urllib.request.urlopen('http://127.0.0.1:18096/System/Info/Public', timeout=5) as response:
                    check(response.status == 200, 'development readiness')
                break
            except OSError:
                time.sleep(1)
        check(manual.dev_running(), 'development did not start')
        first = call('prepare'); active = first['session']
        check(not manual.dev_running(), 'prepare did not stop this repository development instance')
        call('finish', active); active = None
        check(set(manual.dev_running()) == set(containers), 'finish failed to restore originally running development')
        run('docker', 'stop', *containers, capture_output=True); started = []
        check(manual.dev_snapshot() == before, 'development business data changed during rehearsal')
        result['checks'].append('actual development running -> prepare stops -> finish restores running; business data and config hashes unchanged')
        result.update(status='passed', after=manual.dev_snapshot())
    finally:
        if active:
            try:
                call('finish', active)
            except Exception as error:
                result['cleanupError'] = str(error)
        if started:
            run('docker', 'stop', *started, capture_output=True)
        if original:
            run('docker', 'start', *original, capture_output=True)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(result, indent=2) + '\n')
    print('PASS durable manual fixture lifecycle')


if __name__ == '__main__':
    main()
