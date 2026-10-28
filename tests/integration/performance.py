#!/usr/bin/env python3
"""Shared 26-group performance manifest, serial runner, and strict aggregator."""
import argparse
import hashlib
import json
from pathlib import Path
import signal
import sys
import time

from m1 import ROOT, Instance, check, run

GROUPS = json.loads(Path(__file__).with_name('performance-groups.json').read_text())


def validate(result, group):
    check(result.get('status') == 'passed', 'group failed or incomplete')
    check(result.get('group') == group, 'group parameters do not match manifest')
    check(len(result.get('packageSha256', '')) == 64, 'package hash missing')
    records = result['measurements']
    check(len(records) == (1 if group['milestone'] == 'm2' else 4), 'missing viewport/configuration')
    if group['milestone'] == 'm1':
        check({(r['maximum'], r['viewport']['width']) for r in records} ==
              {(m, w) for m in (False, True) for w in (1440, 390)}, 'M1 matrix incomplete')
    for r in records:
        for key, budget in [('load', 2000), ('settings', 200), ('seek', 200)]:
            values = r[key]
            check(len(values) == 20, f'{key}: requires 20 measurements')
            check(all(v['phase'] == 'committed' and 0 <= v['elapsed'] for v in values), 'invalid completion')
            check(sorted(v['elapsed'] for v in values)[18] <= budget, f'{key} P95 exceeded')
        check(len({v['playbackId'] for v in r['load']}) == 20, 'loads must use new playback IDs')
        check(len(r['cleanup']) == 20, 'cleanup requires 20 cycles')
        for sample in r['cleanup']:
            check(all(sample[k] == 0 for k in ('canvases', 'listeners', 'frames', 'selected', 'requests', 'observers', 'indexBytes')), 'cleanup leak')
            check(sample['timers'] == 1, 'cleanup timer leak')
        check([f['enabled'] for f in r['frames']] == [False, True], 'off/on segments missing')
        for f in r['frames']:
            check(f['after']['time'] - f['before']['time'] >= 600, 'less than ten minutes playback')
            check(f['after']['total'] - f['before']['total'] > 10000, 'insufficient frames')
            check(len(f['progress']) == 10, 'minute progress missing')
            check(f['after']['indexBytes'] <= 2 * 1024 * 1024, 'index memory budget')
        ratios = [(f['after']['dropped'] - f['before']['dropped']) /
                  (f['after']['total'] - f['before']['total']) for f in r['frames']]
        check(ratios[1] - ratios[0] <= .01, 'dropped-frame increase exceeds 1 percentage point')
        if r['maximum']:
            check(r['frames'][1]['after']['peakActive'] == 600, 'maximum did not reach 600')
            check(r['sustainedHighLoadSeconds'] >= 30, 'maximum load not sustained')
        if group['milestone'] == 'm2':
            check(r['viewport'] == group['viewport'] and r['maximum'] == group['maximum'] and r['speed'] == group['speed'], 'M2 configuration mismatch')
            check(r['source']['segments'] == 50 and r['source']['candidateCount'] == 1000000, 'input was reduced')
            if group['maximum']:
                check(r['source'].get('profileVersion') == 'm2-pressure-v3'
                      and r['source'].get('denseSourceCount') == 19920
                      and r['source'].get('tailSourceCount') == 80
                      and r['source'].get('denseDurationMs') == 60000
                      and r['source'].get('burstIntervalMs') == 4000, 'maximum stress profile differs')
                bursts = r['source'].get('selectedDenseBursts', [])
                check(len(bursts) == 15 and sum(bursts) == 16000 and all(n >= 600 for n in bursts), 'stress bursts under-filled after sampling')
            check(len(r['backend']) >= 21 and all(x['candidateCount'] == 1000000 and x['scannedCandidates'] == 2000000 for x in r['backend']), 'backend scan evidence missing')


def run_group(group, preflight=False):
    from m2 import M2Instance
    package = ROOT / 'artifacts/jellyfin-plugin-danmuku-0.0.2.zip'
    instance = M2Instance(group['baseUrl']) if group['milestone'] == 'm2' else Instance(group['baseUrl'])
    instance.data = ROOT / 'artifacts/m2/performance' / (group['id'] + '-' + instance.project.rsplit('-', 1)[-1])
    instance.command = ['docker', 'compose', '-f', str(instance.data / 'compose.json')]
    result = {'group': group, 'packageSha256': hashlib.sha256(package.read_bytes()).hexdigest(),
              'status': 'failed', 'startedUtc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}
    try:
        with instance:
            print('Performance fixture:', instance.data, flush=True)
            (instance.data / 'performance-group.json').write_text(json.dumps(group))
            if group['milestone'] == 'm2':
                script = 'm2-performance.cjs'
            else:
                instance.http_tests()
                instance.deployment_tests()
                script = 'm1.cjs'
            run('node', str(ROOT / 'tests/browser' / script), str(instance.data), 'chromium',
                '--performance', *(['--preflight'] if preflight else []))
            name = 'chromium-m2-performance.json' if group['milestone'] == 'm2' else 'chromium-performance.json'
            if preflight and group['milestone'] == 'm1':
                name = 'chromium-preflight.json'
            raw = json.loads((instance.data / name).read_text())
            result.update(measurements=raw['performance'], status='preflight' if preflight else 'passed')
            if not preflight:
                validate(result, group)
    except BaseException as e:
        result.update(status='failed', error=type(e).__name__ + ': ' + str(e))
        raise
    finally:
        instance.data.mkdir(parents=True, exist_ok=True)
        (instance.data / 'group-performance.json').write_text(json.dumps(result, indent=2) + '\n')
    return result


def summarize(directory, output):
    found = {}
    errors = []
    for file in directory.rglob('group-performance.json'):
        try:
            result = json.loads(file.read_text())
            gid = result['group']['id']
            group = next(g for g in GROUPS if g['id'] == gid)
            validate(result, group)
            if gid in found:
                raise AssertionError('duplicate group; select one evidence run explicitly')
            found[gid] = result
        except (ValueError, KeyError, TypeError, AttributeError, IndexError, AssertionError, StopIteration) as e:
            errors.append(f'{file.relative_to(directory)}: {e}')
    missing = sorted({g['id'] for g in GROUPS} - found.keys())
    hashes = {r['packageSha256'] for r in found.values()}
    if len(hashes) != 1:
        errors.append('missing or inconsistent package hashes')
    summary = dict(status='passed' if not missing and not errors else 'failed',
                   required=26, passed=len(found), missing=missing, errors=errors,
                   packageSha256=next(iter(hashes)) if len(hashes) == 1 else None)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(summary, indent=2) + '\n')
    print(json.dumps(summary, indent=2))
    return 0 if summary['status'] == 'passed' else 1


def main():
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(143))
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--group', choices=[g['id'] for g in GROUPS])
    parser.add_argument('--matrix', action='store_true')
    parser.add_argument('--summarize', type=Path)
    parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/m2/performance-summary.json')
    parser.add_argument('--preflight', action='store_true')
    args = parser.parse_args()
    if args.matrix:
        print(json.dumps({'include': [{'group': g['id']} for g in GROUPS]}))
    elif args.summarize:
        return summarize(args.summarize, args.output)
    elif args.group:
        run_group(next(g for g in GROUPS if g['id'] == args.group), args.preflight)
    else:
        parser.error('choose --group, --matrix or --summarize')
    return 0


if __name__ == '__main__':
    sys.exit(main())
