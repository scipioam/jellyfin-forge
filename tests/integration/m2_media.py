"""MB fixtures and HTTP acceptance; generated media, no production or online metadata."""
import hashlib
import json
import os
from pathlib import Path
import urllib.parse
import uuid

from m1 import check, run

FIXTURE_VERSION = 'mb-v1'


def scaffold(instance):
    root = instance.data / 'mb-media'
    root.mkdir(parents=True, exist_ok=True)
    clip = root / 'source.mkv'
    if not clip.exists():
        run('ffmpeg', '-y', '-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i',
            'testsrc2=size=320x180:rate=24', '-t', '12', '-c:v', 'libx264', '-threads', '1',
            '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', str(clip))

    mp4 = root / 'source.mp4'
    if not mp4.exists():
        run('ffmpeg', '-y', '-hide_banner', '-loglevel', 'error', '-i', str(clip), '-c', 'copy', '-movflags', '+faststart', str(mp4))

    def video(relative, metadata=None):
        file = root / relative
        file.parent.mkdir(parents=True, exist_ok=True)
        if not file.exists():
            os.link(mp4 if file.suffix == '.mp4' else clip, file)
        if metadata:
            file.with_suffix('.nfo').write_text(metadata)

    for folder in ('one', 'two'):
        video(f'{folder}/相同目录/1.mp4', '<movie><title>ZMB 独立视频</title></movie>')
    video('one/' + '/'.join(['长目录中文-' * 6] * 3) + '/long.mkv', '<movie><title>ZMB &lt;视频 &amp; 标题&gt;</title></movie>')
    for n in range(54):
        video(f'one/分页/ZMB-page-{n:02}.mkv')
    for name in ('甲剧', '乙剧'):
        series = root / 'tv' / name
        series.mkdir(parents=True, exist_ok=True)
        (series / 'tvshow.nfo').write_text(f'<tvshow><title>ZMB {name}</title></tvshow>')
        for season in ([0, 1, 2] if name == '甲剧' else [1]):
            video(f'tv/{name}/Season {season:02}/1.mkv',
                  f'<episodedetails><title>同名集</title><season>{season}</season><episode>1</episode></episodedetails>')
    for year in (2020, 2021):
        video(f'movies/ZMB 电影 ({year})/1.mkv', f'<movie><title>ZMB 电影</title><year>{year}</year></movie>')
    video('movies/ZMB 无年份电影/1.mkv', '<movie><title>ZMB 无年份电影</title></movie>')
    video('movies/ZMB 电影 (2020)/behind the scenes/extra.mkv')
    video('movies/ZMB 电影 (2020)/trailers/trailer.mkv')
    video('music/ZMB 音乐视频.mkv')
    instance.additional_volumes = [f'{root}:/mb-media:ro']
    (root / 'manifest.json').write_text(json.dumps({'version': FIXTURE_VERSION,
        'sampleSha256': hashlib.sha256(clip.read_bytes()).hexdigest(),
        'browserSampleSha256': hashlib.sha256(mp4.read_bytes()).hexdigest()}))


def provision(x):
    def idle():
        return not any(t.get('Key') == 'RefreshLibrary' and t.get('State') in ('Running', 'Cancelling')
                       for t in x.request('/ScheduledTasks', token=x.admin))
    x.wait(idle)
    libraries = [('ZMB 视频库', 'homevideos', ['two', 'one']), ('ZMB 剧集库', 'tvshows', ['tv']),
                 ('ZMB 电影库', 'movies', ['movies']), ('ZMB 音乐视频库', 'musicvideos', ['music'])]
    existing = x.request('/Library/VirtualFolders', token=x.admin)
    for name, kind, roots in libraries:
        if any(lib['Name'] == name for lib in existing):
            continue
        query = urllib.parse.urlencode([('name', name), ('collectionType', kind), ('refreshLibrary', 'false')]
                                      + [('paths', '/mb-media/' + root) for root in roots])
        options = {'EnableRealtimeMonitor': False, 'EnableInternetProviders': False,
                   'TypeOptions': [{'Type': t, 'MetadataFetchers': [], 'ImageFetchers': []}
                                   for t in ('Movie', 'Series', 'Season', 'Episode', 'Video', 'MusicVideo')]}
        x.request('/Library/VirtualFolders?' + query, {'LibraryOptions': options}, x.admin, expected=204)
        x.wait(idle)
    x.scan_library()
    x.wait(idle)
    items = x.request('/Items?Recursive=true&Fields=Path,ExtraType,ProductionYear,SeriesName,ParentIndexNumber,IndexNumber', token=x.admin)['Items']
    video = [i for i in items if i.get('Type') == 'Video' and '/相同目录/' in i.get('Path', '')]
    check(len(video) == 2, 'fixture must contain two real ordinary Video items')
    episodes = [i for i in items if i.get('Type') == 'Episode' and i.get('Path', '').startswith('/mb-media/')]
    check(len(episodes) == 4, 'expected four real episodes including season zero')
    films = [i for i in items if i.get('Type') == 'Movie' and i.get('Path', '').startswith('/mb-media/')]
    music = [i for i in items if i.get('Type') == 'MusicVideo']
    check(len(films) == 3 and music, 'movie/music-video fixture types')
    videos = sorted(video, key=lambda i: i['Path'])
    data = {'version': FIXTURE_VERSION, 'rootLabels': ['目录 2', '目录 1'], 'videos': [i['Id'] for i in videos], 'episodes': [i['Id'] for i in episodes],
            'movies': [i['Id'] for i in films], 'music': music[0]['Id']}
    extra = []
    for film in films:
        extra += x.request('/Items/' + film['Id'] + '/SpecialFeatures', token=x.admin)
        extra += x.request('/Items/' + film['Id'] + '/LocalTrailers', token=x.admin)
    data['extras'] = [i['Id'] for i in extra]
    data['long'] = next(i['Id'] for i in items if i.get('Path', '').endswith('/long.mkv'))
    path = x.data / 'credentials.json'
    credentials = json.loads(path.read_text()); credentials['mediaFixtures'] = data
    path.write_text(json.dumps(credentials))
    (x.data / 'mb-fixtures.json').write_text(json.dumps(data, indent=2))
    return data


def contracts(x, fixture):
    raw = x.request('/Danmuku/Media?limit=1', token=x.admin)
    check(set(raw['items'][0]) == {'id', 'name', 'type', 'canBind', 'displayTitle', 'locationText', 'canNavigate'},
          'media descriptor casing preserves the existing public contract')
    first = x.api('/Danmuku/Media?limit=50')
    second = x.api('/Danmuku/Media?startIndex=50&limit=50')
    check(first['totalcount'] == second['totalcount'] and first['totalcount'] > 50, 'stable paging totals')
    check(len({i['id'] for i in first['items'] + second['items']}) == len(first['items']) + len(second['items']), 'no duplicate page rows')
    all_rows = first['items'] + second['items']
    for media in fixture['videos'] + fixture['movies'] + fixture['episodes']:
        row = next(i for i in all_rows if i['id'] == media)
        detail = x.api('/Danmuku/Media/' + media + '/CombinePlans')['media']
        check(row == detail and row['canbind'] and not row['cannavigate'], 'list/detail and eligibility consistent')
        check('/mb-media' not in row['locationtext'], 'absolute location leak')
    positions = [x.api('/Danmuku/Media/' + v + '/CombinePlans')['media']['locationtext'] for v in fixture['videos']]
    check('目录 2' in positions[0] and '目录 1' in positions[1], 'configured root labels distinguish duplicate names')
    check(any('S00E01' in r['displaytitle'] for r in all_rows), 'season zero presentation')
    check(any('（2020）' in r['displaytitle'] for r in all_rows), 'film year presentation')
    check(any(r['displaytitle'] == 'ZMB 无年份电影' for r in all_rows), 'missing film year is not invented')
    search = x.api('/Danmuku/Media?search=ZMB-page&limit=50')
    check(search['totalcount'] == 54 and len(search['items']) == 50, 'search paging unchanged')
    x.api('/Danmuku/Media?limit=101', expected=422)
    for token, status in [(None, 401), (x.viewer, 403)]:
        x.api('/Danmuku/Media', token=token, expected=status)

    file_batch = x.api('/Danmuku/ImportBatches', {'batchId': uuid.uuid4().hex, 'operation': 'import', 'fileNames': ['mb-independent.json']})
    upload = x.api('/Danmuku/ImportBatches/' + file_batch['batchid'] + '/Files/0', json.dumps([
        {'progress': 0, 'content': 'MB ordinary video'}, {'progress': 3000, 'content': 'MB second comment'}]).encode(), expected=202)
    done = x.terminal(upload['taskid']); check(done['Status'] == 'Completed', 'independent source import')
    file = done['FileId']; media = '/Danmuku/Media/' + fixture['videos'][0]
    state = x.api(media + '/Bindings')
    state = x.api(media + '/Bindings', {'expectedVersion': state['version'], 'fileIds': [file], 'selectionIntent': 'set', 'selection': {'kind': 'file', 'id': file}}, 'PUT')
    def playback(expected=200, token=None):
        return x.api('/Danmuku/Playback/' + fixture['videos'][0] + '?renderVersion=m2-speed-v1&playbackId=' + uuid.uuid4().hex,
                     token=token or x.viewer, expected=expected)
    check(playback()['items'], 'ordinary video single-file playback')
    playback(403, x.restricted)
    plan = x.api(media + '/CombinePlans', {'planId': uuid.uuid4().hex, 'name': 'MB-video-plan', 'segments': [{'fileId': file}],
                                        'activate': True, 'expectedMediaVersion': state['version']})
    check(playback()['sourcekind'] == 'plan', 'ordinary video plan playback')
    x.api('/Danmuku/Files/' + file, method='DELETE', expected=409)
    state = x.api(media + '/Bindings')
    x.api(media + '/Selection', {'expectedVersion': state['version'], 'kind': 'disabled'}, 'PUT')
    check(not playback()['items'], 'disabled video playback')
    state = x.api(media + '/Bindings')
    x.api(media + '/CombinePlans/' + plan['planid'] + '/Delete', {'expectedMediaVersion': state['version'], 'expectedPlanVersion': plan['version']}, expected=204)
    state = x.api(media + '/Bindings')
    x.api(media + '/Bindings', {'expectedVersion': state['version'], 'fileIds': [], 'selectionIntent': 'preserve'}, 'PUT')
    uploaded = None
    for operation in ('append', 'replace'):
        state = x.api(media + '/Bindings')
        body = {'batchId': uuid.uuid4().hex, 'operation': operation, 'mediaId': fixture['videos'][0],
                'expectedVersion': state['version'], 'fileNames': ['mb-' + operation + '.json']}
        if uploaded:
            body['replaceFileId'] = uploaded
        batch = x.api('/Danmuku/ImportBatches', body)
        task = x.api('/Danmuku/ImportBatches/' + batch['batchid'] + '/Files/0',
                     json.dumps([{'progress': 1000, 'content': 'MB ' + operation}]).encode(), expected=202)
        done = x.terminal(task['taskid']); check(done['Status'] == 'Completed', 'ordinary video ' + operation)
        current = x.api(media + '/Bindings')
        check(done['FileId'] in current['fileids'], 'uploaded file not bound to ordinary video')
        if uploaded:
            check(uploaded not in current['fileids'], 'replace retained old binding')
        uploaded = done['FileId']
    state = x.api(media + '/Bindings')
    x.api(media + '/Bindings', {'expectedVersion': state['version'], 'fileIds': [], 'selectionIntent': 'set', 'selection': {'kind': 'disabled'}}, 'PUT')
    # Leave one isolated single-file binding for the browser to exercise real Web playback.
    state = x.api(media + '/Bindings')
    x.api(media + '/Bindings', {'expectedVersion': state['version'], 'fileIds': [file], 'selectionIntent': 'set', 'selection': {'kind': 'file', 'id': file}}, 'PUT')
    for excluded in [fixture['music'], *fixture['extras']]:
        route = '/Danmuku/Media/' + excluded
        detail = x.api(route + '/CombinePlans')
        check(not detail['media']['canbind'], 'excluded type descriptor')
        x.api(route + '/Bindings', {'expectedVersion': detail['state']['version'], 'fileIds': [file], 'selectionIntent': 'preserve'}, 'PUT', 404)
        x.api(route + '/Selection', {'expectedVersion': detail['state']['version'], 'kind': 'file', 'id': file}, 'PUT', 404)
        x.api(route + '/CombinePlans', {'planId': uuid.uuid4().hex, 'name': 'rejected', 'segments': [{'fileId': file}]}, expected=404)
        x.api('/Danmuku/ImportBatches', {'batchId': uuid.uuid4().hex, 'mediaId': excluded, 'expectedVersion': detail['state']['version'],
                                      'operation': 'append', 'fileNames': ['rejected.json']}, expected=404)
        x.api('/Danmuku/Playback/' + excluded + '?renderVersion=m2-speed-v1&playbackId=' + uuid.uuid4().hex, token=x.viewer, expected=403)
    x.results.append('MB-A01/02/03/04/05/06: real Video/episode/movie identity, roots, pagination, single/plan lifecycle and excluded music/extras HTTP')


def missing_video(x, fixture):
    media_id = fixture['videos'][1]
    route = '/Danmuku/Media/' + media_id
    file = x.api('/Danmuku/Media/' + fixture['videos'][0] + '/Bindings')['fileids'][0]
    state = x.api(route + '/Bindings')
    state = x.api(route + '/Bindings', {'expectedVersion': state['version'], 'fileIds': [file], 'selectionIntent': 'preserve'}, 'PUT')
    plan = x.api(route + '/CombinePlans', {'planId': uuid.uuid4().hex, 'name': 'MB missing', 'segments': [{'fileId': file}],
                                        'activate': True, 'expectedMediaVersion': state['version']})
    path = x.data / 'mb-media/two/相同目录/1.mp4'
    path.unlink()  # Only this generated isolated sample; mounted read-only in the server.
    x.scan_library()
    x.wait(lambda: x.api(route + '/Bindings')['checkstatus'] == 'Missing')
    detail = x.api(route + '/CombinePlans')
    check(detail['items'] and not detail['media']['canbind'], 'missing Video retains cleanup descriptor')
    x.api(route + '/Selection', {'expectedVersion': detail['state']['version'], 'kind': 'file', 'id': file}, 'PUT', 404)
    state = x.api(route + '/Selection', {'expectedVersion': detail['state']['version'], 'kind': 'disabled'}, 'PUT')
    x.api(route + '/CombinePlans/' + plan['planid'] + '/Delete', {'expectedMediaVersion': state['version'], 'expectedPlanVersion': plan['version']}, expected=204)
    state = x.api(route + '/Bindings')
    x.api(route + '/Bindings', {'expectedVersion': state['version'], 'fileIds': [], 'selectionIntent': 'preserve'}, 'PUT')
    check(x.api('/Danmuku/Files/' + file)['fileid'] == file, 'cleanup removed shared source')
    check(file in x.api('/Danmuku/Media/' + fixture['videos'][0] + '/Bindings')['fileids'], 'cleanup altered another video')
    x.results.append('MB-A07 ordinary Video removal: Missing detail, forbidden activation, disable/unbind/delete plan, shared source retained')
