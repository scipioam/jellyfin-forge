#!/usr/bin/env python3
"""Run one local heavy check at a time, on one CPU, with a resource watchdog.

No Docker daemon or host service settings are changed. A pressure abort is a
failed/incomplete check, never a passing measurement. Drivers receive SIGTERM
so their own fixture cleanup can run before the process group is reaped.
"""
import argparse
import fcntl
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]


def resources():
    memory = dict(line.split(':', 1) for line in Path('/proc/meminfo').read_text().splitlines())
    return {'availableMiB': int(memory['MemAvailable'].split()[0]) // 1024,
            'diskFreeMiB': shutil.disk_usage(ROOT).free // (1024 * 1024)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--memory-floor-mib', type=int, default=600)
    parser.add_argument('--disk-floor-mib', type=int, default=2048)
    parser.add_argument('--record', type=Path)
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ['--'] else args.command
    if not command:
        parser.error('a command is required')
    directory = ROOT / 'artifacts/resource-guard'
    directory.mkdir(parents=True, exist_ok=True)
    record = args.record or directory / (time.strftime('%Y%m%d-%H%M%S') + '.json')
    record.parent.mkdir(parents=True, exist_ok=True)
    lock = (directory / 'heavy.lock').open('w')
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        raise SystemExit('Another guarded heavy check is running; refusing concurrency.')
    initial = resources()
    result = {'initial': initial, 'minimum': dict(initial), 'status': 'running', 'samples': 0}

    def save():
        record.write_text(json.dumps(result, indent=2) + '\n')

    def pressure(sample):
        return sample['availableMiB'] < args.memory_floor_mib or sample['diskFreeMiB'] < args.disk_floor_mib

    if pressure(initial):
        result['status'] = 'refused-resource-pressure'
        save()
        return 75
    env = dict(os.environ, DOTNET_PROCESSOR_COUNT='1', MSBUILDDISABLENODEREUSE='1',
               UseSharedCompilation='false', NODE_OPTIONS='--max-old-space-size=512',
               OMP_NUM_THREADS='1', OPENBLAS_NUM_THREADS='1', FORGE_TEST_CPUS='1', FORGE_TEST_MEMORY='1g')

    def limit_child():
        os.nice(10)
        os.sched_setaffinity(0, {min(os.sched_getaffinity(0))})

    child = subprocess.Popen(command, cwd=ROOT, env=env, start_new_session=True, preexec_fn=limit_child)
    interrupted = []
    for sig in (signal.SIGTERM, signal.SIGINT):
        signal.signal(sig, lambda number, _: interrupted.append(number))
    save()
    while child.poll() is None:
        sample = resources()
        result['samples'] += 1
        result['latest'] = sample
        for key in sample:
            result['minimum'][key] = min(result['minimum'][key], sample[key])
        if pressure(sample) or interrupted:
            result['status'] = 'interrupted' if interrupted else 'aborted-resource-pressure'
            # Notify the driver first; let its finally block stop its own container.
            child.terminate()
            try:
                child.wait(timeout=45)
            except subprocess.TimeoutExpired:
                os.killpg(child.pid, signal.SIGTERM)
                try:
                    child.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    os.killpg(child.pid, signal.SIGKILL)
                    child.wait()
            # A terminated Python driver may have left a browser child behind.
            try:
                os.killpg(child.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
            result['exitCode'] = 75 if not interrupted else 128 + interrupted[0]
            save()
            return result['exitCode']
        save()
        time.sleep(5)
    result.update(status='passed' if child.returncode == 0 else 'failed', exitCode=child.returncode)
    save()
    return child.returncode


if __name__ == '__main__':
    sys.exit(main())
