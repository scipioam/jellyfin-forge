"""Remaining M2 transport/ownership/race checks, against the isolated server."""
from concurrent.futures import ThreadPoolExecutor
import json
import uuid

from m1 import check


def stable(value):
    if isinstance(value, dict):
        return {key: stable(v) for key, v in value.items() if key != 'checkedatutcms'}
    if isinstance(value, list):
        return [stable(v) for v in value]
    return value


def check_contracts(x):
    media = '/Danmuku/Media/' + x.item
    other = '/Danmuku/Media/' + x.second
    plans = media + '/CombinePlans'
    source = x.api('/Danmuku/Files')['items'][0]['fileid']
    segment = {'fileId': source, 'sourceStartMs': 0, 'sourceEndMs': None, 'targetStartMs': 0}
    before_other = x.api(other + '/CombinePlans')
    state = x.api(media + '/Bindings')
    p = x.api(plans, {'planId': uuid.uuid4().hex, 'name': 'http-P', 'segments': [segment], 'activate': True, 'expectedMediaVersion': state['version']})
    state = x.api(media + '/Bindings')
    q = x.api(plans, {'planId': uuid.uuid4().hex, 'name': 'http-Q', 'segments': [segment]})
    check(stable(x.api(media + '/Bindings')) == stable(state), 'save only changed media selection')
    # Concurrent requests use the same version; one wins, one reports conflict.
    def activate(plan):
        try:
            return x.request(media + '/Selection', {'kind': 'plan', 'id': plan['planid'], 'expectedPlanVersion': plan['version'],
                             'expectedVersion': state['version']}, x.admin, method='PUT')
        except AssertionError as e:
            check('got 409' in str(e), 'unexpected concurrent response')
            return None
    with ThreadPoolExecutor(max_workers=2) as pool:
        outcomes = list(pool.map(activate, [p, q]))
    check(sum(v is not None for v in outcomes) == 1, 'exactly one competing activation must commit')
    check(x.api(plans + '/' + p['planid'])['plan'] == p, 'activation altered P content')
    check(x.api(plans + '/' + q['planid'])['plan'] == q, 'activation altered Q content')
    current = x.api(media + '/Bindings')
    # A stale save-and-activate may not commit its content first.
    x.api(plans + '/' + q['planid'], {'name': 'must-not-save', 'segments': [dict(segment, targetStartMs=123)],
          'expectedPlanVersion': q['version'], 'activate': True, 'expectedMediaVersion': state['version']}, 'PUT', 409)
    check(x.api(plans + '/' + q['planid'])['plan'] == q, 'stale activation partially committed content')
    check(stable(x.api(other + '/CombinePlans')) == stable(before_other), 'other media changed')
    x.api(other + '/Selection', {'kind': 'plan', 'id': p['planid'], 'expectedPlanVersion': p['version'],
          'expectedVersion': before_other['state']['version']}, 'PUT', 404)
    imported_files = []
    for operation in ('append', 'replace'):
        batch = x.batch(['pending-' + operation + '.json'], operation, imported_files[-1] if imported_files else None)
        latest = x.api(media + '/Bindings')
        x.api(media + '/Selection', {'kind': 'plan', 'id': p['planid'], 'expectedPlanVersion': p['version'],
              'expectedVersion': latest['version']}, 'PUT')
        task = x.task(batch, 0, json.dumps([{'progress': 1, 'content': operation + '-conflict'}]).encode())
        pending = x.terminal(task['TaskId'])
        check(pending['Status'] == 'AwaitingConflictResolution', 'in-flight import must conflict with new plan selection')
        latest = x.api(media + '/Bindings')
        x.api('/Danmuku/Imports/' + task['TaskId'] + '/Resume', {'expectedVersion': latest['version']})
        complete = x.terminal(task['TaskId'])
        check(complete['Status'] == 'Completed', 'Resume did not publish')
        imported_files.append(complete['FileId'])
        check(x.api(media + '/Bindings')['activeplanid'] == p['planid'], 'import stole plan activation')
        check(x.api(plans + '/' + p['planid'])['plan'] == p, 'replacement changed plan sources')
    latest = x.api(media + '/Bindings')
    x.api(media + '/Bindings', {'expectedVersion': latest['version'], 'fileIds': [], 'selectionIntent': 'preserve'}, 'PUT')
    for file in imported_files:
        x.api('/Danmuku/Files/' + file, method='DELETE')
    x.results.append('A08 real pending append/replace conflict, explicit Resume and plan/source preservation')
    for body in [dict(segment, sourceStartMs=-1), dict(segment, sourceEndMs=0), dict(segment, sourceStartMs=0.5),
                 dict(segment, targetStartMs=9007199254740991), dict(segment, sourceEndMs=9007199254740992)]:
        # The plugin maps both JSON binding and semantic validation to 422.
        x.api(plans + '/Preview', {'segments': [body]}, expected=422)
    x.api(plans + '/Preview', {'segments': [segment, segment]}, expected=422)
    x.api(plans, {'planId': uuid.uuid4().hex, 'name': 'zero', 'segments': [dict(segment, sourceStartMs=99999999)]}, expected=422)
    x.api(plans, {'planId': uuid.uuid4().hex, 'name': '😀' * 101, 'segments': [segment]}, expected=422)
    upper = x.api(plans, {'planId': uuid.uuid4().hex, 'name': '  😀' * 1 + '😀' * 99 + '  ', 'segments': [segment]})
    check(len(upper['name']) == 100, 'Unicode/trimming boundary')
    for i in range(3, 20):
        x.api(plans, {'planId': uuid.uuid4().hex, 'name': f'cap-{i}', 'segments': [segment]})
    x.api(plans, {'planId': uuid.uuid4().hex, 'name': 'cap-21', 'segments': [segment]}, expected=422)
    # A frozen collection survives deletion of plans; missing cache expires rather
    # than silently generating a different collection (service tests cover capacity).
    playback_id = uuid.uuid4().hex
    playback = '/Danmuku/Playback/' + x.item + '?renderVersion=m2-speed-v1&playbackId=' + playback_id
    frozen = x.api(playback, token=x.viewer)
    check(frozen['sourcekind'] == 'plan', 'plan playback missing')
    for plan in x.api(plans)['items']:
        state = x.api(media + '/Bindings')
        body = {'expectedPlanVersion': plan['version'], 'expectedMediaVersion': state['version']}
        if state.get('activeplanid') == plan['planid']:
            body['replacement'] = {'kind': 'disabled'}
        x.api(plans + '/' + plan['planid'] + '/Delete', body, expected=204)
    check(x.api(playback, token=x.viewer) == frozen, 'successful playback retry was not frozen')
    x.api('/Danmuku/Playback/' + x.second + '?renderVersion=m2-speed-v1&playbackId=' + playback_id, token=x.viewer, expected=409)
    x.results.append('A02/A03/A05/A07/A09/A13 HTTP limits, real competing activation, atomic stale save, cross-media rejection and immutable retry')


def missing_media(x, browser=False):
    """Last scenario: remove only this disposable fixture's entire media library."""
    media = '/Danmuku/Media/' + x.second
    source = x.api('/Danmuku/Files')['items'][0]['fileid']
    state = x.api(media + '/Bindings')
    segment = {'fileId': source, 'sourceStartMs': 0, 'sourceEndMs': None, 'targetStartMs': 0}
    plan = x.api(media + '/CombinePlans', {'planId': uuid.uuid4().hex, 'name': 'orphan-only', 'segments': [segment],
                  'activate': True, 'expectedMediaVersion': state['version']})
    if browser:
        for engine in ('chromium', 'firefox'):
            x.api(media + '/CombinePlans', {'planId': uuid.uuid4().hex, 'name': 'orphan-' + engine, 'segments': [segment]})
    folders = x.request('/Library/VirtualFolders', token=x.admin)
    from urllib.parse import quote
    for folder in folders:
        x.request('/Library/VirtualFolders?name=' + quote(folder['Name']) + '&refreshLibrary=true', token=x.admin, method='DELETE', expected=204)
    x.wait(lambda: not x.request('/Items?Recursive=true&IncludeItemTypes=Movie', token=x.admin)['Items'])
    job = x.api('/Danmuku/BindingChecks', {})['taskid']
    x.wait(lambda: x.api('/Danmuku/BindingChecks/' + job)['status'] == 'Completed')
    abnormal = x.api('/Danmuku/Media?abnormal=true')
    check(any(r.get('mediaid') == x.second or r.get('id') == x.second for r in abnormal['items']), 'plan-only missing media not discoverable')
    state = x.api(media + '/Bindings')
    check(state['checkstatus'] == 'Missing' and not state['fileids'], 'orphan state')
    x.api(media + '/CombinePlans/' + plan['planid'], {'name': 'invalid', 'segments': [segment], 'expectedPlanVersion': plan['version']}, 'PUT', 404)
    x.api(media + '/Selection', {'kind': 'plan', 'id': plan['planid'], 'expectedPlanVersion': plan['version'], 'expectedVersion': state['version']}, 'PUT', 404)
    x.api(media + '/CombinePlans/' + plan['planid'] + '/Delete', {'expectedPlanVersion': plan['version'],
          'expectedMediaVersion': state['version'], 'replacement': {'kind': 'disabled'}}, expected=204)
    check(x.api('/Danmuku/Files/' + source)['status'] == 'Published', 'orphan cleanup deleted source')
    check(x.api(media + '/Bindings')['isdeactivated'], 'orphan cleanup did not disable atomically')
    x.results.append('A06 real missing plan-only media remains discoverable; edit/enable rejected; explicit cleanup retains source')
    if browser:
        from m1 import ROOT, run
        for engine in ('chromium', 'firefox'):
            run('node', str(ROOT / 'tests/browser/m2-missing.cjs'), str(x.data), engine)
        check(not x.api(media + '/CombinePlans')['items'], 'browser orphan cleanup incomplete')
        check(x.api('/Danmuku/Files/' + source)['status'] == 'Published', 'browser cleanup deleted source')
        x.results.append('A06 Chromium/Firefox discover plan-only missing media, disabled edit/enable and explicit UI cleanup')
