/* Browser plugin not available; pinned Playwright; serial complete performance group. */
'use strict';
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto'), assert = require('node:assert/strict');
const { chromium } = require('playwright');
const { installObserver, assertLatency, p95 } = require('./density-observer.cjs');
const dir = process.argv[2], preflight = process.argv.includes('--preflight');
const group = JSON.parse(fs.readFileSync(path.join(dir, 'performance-group.json')));
const { base, item, plugin, api, auth, credentials, normalize, redact, login, play, leave, memory } = require('./m2-support.cjs')(dir);
const sleep = ms => new Promise(r => setTimeout(r, ms));
function sustainedLoad(samples) {
    let start = null, sustained = 0;
    for (const s of samples) {
        if (s.active >= 570) { if (start === null) start = s.time; sustained = Math.max(sustained, s.time - start); } else start = null;
    }
    return sustained;
}
async function seed() {
    const filename = path.join(dir, 'combine-million.json');
    const fd = fs.openSync(filename, 'w'), hash = crypto.createHash('sha256');
    function write(text) { fs.writeSync(fd, text); hash.update(text); }
    write('[');
    // Default/high mode needs sustained arrivals throughout the ten-minute
    // sample. Maximum/overlap reserves 4,000 selected tail instances (80
    // source rows x 50 segments), leaving 16,000 in the dense minute. Giving
    // both minutes >=10,000 candidates splits the quota equally and can leave
    // periodic admission gaps below 570, so it is not a reliable stress input.
    // Fifteen bursts spaced four seconds apart align with the tested 4/8/16s
    // lifetimes. Each selected burst must independently contain >=600 rows;
    // uniform random arrivals can under-fill the cap at expiry even at 16k/min.
    const denseDurationMs = group.maximum ? 60000 : 600000;
    const denseSourceCount = group.maximum ? 19920 : 16000;
    const tailSourceCount = 20000 - denseSourceCount;
    for (let i = 0; i < 20000; i++) write((i ? ',' : '') + JSON.stringify({
        progress: i < denseSourceCount ? 120000 + (group.maximum ? Math.floor(i * 15 / denseSourceCount) * 4000 : Math.floor(i * denseDurationMs / denseSourceCount)) : 3660000 + Math.floor((i - denseSourceCount) * 39000 / tailSourceCount),
        content: '合并压力 ' + i, color: 16777215, fontsize: 25,
        mode: i < denseSourceCount ? 1 : [1, 4, 5][i % 3]
    }));
    write(']'); fs.closeSync(fd);
    const batch = await api('/Danmuku/ImportBatches', { batchId: crypto.randomUUID(), operation: 'import', fileNames: ['combine-million.json'] }, 'POST');
    const r = await fetch(base + '/Danmuku/ImportBatches/' + batch.batchId + '/Files/0', {
        method: 'POST', headers: { Authorization: auth(credentials.admin.token), 'Content-Type': 'application/octet-stream',
            'Content-Length': String(fs.statSync(filename).size) }, body: fs.createReadStream(filename), duplex: 'half' });
    assert.equal(r.status, 202);
    const task = normalize(await r.json()); let done;
    for (let i = 0; i < 240; i++) {
        done = await api('/Danmuku/Imports/' + task.taskId);
        if (done.status === 'Completed') break;
        assert(!['Failed', 'Interrupted'].includes(done.status), 'seed import failed');
        await sleep(1000);
    }
    assert.equal(done.status, 'Completed');
    // Fifty different ranges, all preserving the source time: repeated source
    // instances count toward the million-candidate limit, with no deduplication.
    const segments = Array.from({ length: 50 }, (_, i) => ({ fileId: done.fileId, sourceStartMs: i, sourceEndMs: null, targetStartMs: i }));
    const media = '/Danmuku/Media/' + item, state = await api(media + '/Bindings');
    const preview = await api(media + '/CombinePlans/Preview', { segments }, 'POST');
    assert.equal(preview.totalCount, 1000000);
    await api(media + '/CombinePlans', { planId: crypto.randomUUID(), name: 'performance-million', segments,
        activate: true, expectedMediaVersion: state.version }, 'POST');
    return { fileId: done.fileId, segments: 50, candidateCount: preview.totalCount, denseDurationMs,
        profileVersion: group.maximum ? 'm2-pressure-v3' : 'm2-default-v1', denseSourceCount, tailSourceCount,
        burstIntervalMs: group.maximum ? 4000 : null,
        sha256: hash.digest('hex'), bytes: fs.statSync(filename).size };
}
(async () => {
    const result = { status: 'failed', group, fallback: 'Browser plugin not available', performance: [] };
    const record = { maximum: group.maximum, viewport: group.viewport, speed: group.speed, load: [], settings: [], seek: [], backend: [], frames: [], cleanup: [] };
    result.inProgress = record;
    const browser = await chromium.launch({ headless: true, args: ['--disable-dev-shm-usage', '--disable-gpu', '--js-flags=--expose-gc', '--enable-precise-memory-info'] });
    const context = await browser.newContext({ locale: 'en-US', viewport: group.viewport });
    const page = await context.newPage(), errors = [];
    const save = () => fs.writeFileSync(path.join(dir, 'performance-progress.json'), JSON.stringify(result, null, 2));
    page.on('pageerror', e => errors.push(redact(e.stack)));
    const pendingResponses = [];
    page.on('response', response => {
        if (!response.url().includes('/Danmuku/Playback/') || response.status() !== 200) return;
        pendingResponses.push(response.json().then(body => {
            if (body.sourceKind === 'plan') record.backend.push({ candidateCount: body.candidateCount,
                scannedCandidates: body.scannedCandidates, selectedCount: body.selectedCount, selectionElapsedMs: body.selectionElapsedMs });
            if (group.maximum && body.sourceKind === 'plan' && !record.source.selectedDenseBursts) {
                const counts = Array(15).fill(0);
                for (const row of body.items) {
                    const index = (row.timeMs - 120000) / 4000;
                    if (Number.isInteger(index) && index >= 0 && index < counts.length) counts[index]++;
                }
                record.source.selectedDenseBursts = counts;
            }
        }).catch(e => { throw Error('playback metadata: ' + redact(e)); }));
    });
    try {
        result.browser = browser.version();
        record.source = await seed();
        const cfg = await api('/Plugins/' + plugin + '/Configuration');
        const body = Object.fromEntries(Object.entries(cfg).map(([k, v]) => [k[0].toUpperCase() + k.slice(1), v]));
        Object.assign(body, { LongLoadLimit: group.maximum ? 20000 : 10000, MediumRenderLimit: 200, HighRenderLimit: 400, OverlapRenderLimit: 600 });
        await api('/Plugins/' + plugin + '/Configuration', body, 'POST', 204);
        await page.addInitScript(installObserver);
        await login(page);
        await page.evaluate(g => {
            localStorage.setItem('danmuku:m1:' + ApiClient.getUrl('Danmuku') + ':' + ApiClient.getCurrentUserId(), JSON.stringify({
                enabled: true, densityModeV2: g.maximum ? 'overlap' : 'high', area: 75, scale: 100, opacity: 75, scrollSpeed: g.speed }));
        }, group);
        for (let i = 0; i < 21; i++) {
            await page.evaluate(() => { densitySamples.length = 0; });
            await play(page);
            const sample = await page.evaluate(() => {
                const s = densitySamples.at(-1);
                if (!s || s.playbackId !== DanmukuM1.operation.playbackId) throw Error('no matching load completion');
                return { ...s, elapsed: s.committedAt - s.startedAt, count: DanmukuM1.metrics.selected };
            });
            assert.equal(sample.count, group.maximum ? 20000 : 10000);
            if (group.maximum && i === 0) {
                await Promise.all(pendingResponses);
                assert.equal(record.source.selectedDenseBursts.reduce((a, b) => a + b, 0), 16000);
                assert(record.source.selectedDenseBursts.every(count => count >= 600), 'stress burst under-filled after sampling');
            }
            if (i === 0) record.firstMeasured = sample; else record.load.push(sample);
            await leave(page); save();
        }
        assertLatency(record.load, 2000); record.loadP95 = p95(record.load);
        await play(page);
        await page.locator('input[aria-label="滚动速度"]').waitFor({ state: 'attached' });
        await page.locator('video').first().evaluate(v => v.pause());
        const profiler = process.env.FORGE_PERF_PROFILE ? await context.newCDPSession(page) : null;
        if (profiler) { await profiler.send('Profiler.enable'); await profiler.send('Profiler.start'); }
        for (let i = 0; i < 20; i++) {
            record.seek.push(await page.evaluate(i => measureDensityOperation('seek', () => {
                document.querySelector('video').currentTime = i % 2 === 0 ? 3690 - i : 120 + i;
            }), i));
            record.settings.push(await page.evaluate(({ i, speed }) => measureDensityOperation('scrollSpeed', () => {
                const input = document.querySelector('input[aria-label="滚动速度"]');
                input.value = String((i % 2 === 0 ? (speed === .5 ? 2 : .5) : speed) * 10);
                input.dispatchEvent(new Event('input'));
            }), { i, speed: group.speed }));
        }
        if (profiler) {
            fs.writeFileSync(path.join(dir, 'diagnostic.cpuprofile'), JSON.stringify((await profiler.send('Profiler.stop')).profile));
            await profiler.detach();
        }
        assertLatency(record.settings, 200); assertLatency(record.seek, 200);
        record.settingsP95 = p95(record.settings); record.seekP95 = p95(record.seek);
        await leave(page);
        for (let i = 0; i < (preflight ? 1 : 20); i++) { await play(page); await leave(page); record.cleanup.push(await memory(page)); save(); }
        await play(page);
        // The native player can finish loading before the periodic settings
        // control attachment. This is setup, outside all measured operations.
        await page.locator('.danmuku-panel input[role="switch"]').waitFor({ state: 'attached' });
        await page.locator('video').first().evaluate(v => v.pause());
        for (const enabled of [false, true]) {
            await page.evaluate(async enabled => {
                const input = document.querySelector('.danmuku-panel input[role="switch"]');
                if (input.checked !== enabled) await measureDensityOperation('enabled', () => { input.checked = enabled; input.dispatchEvent(new Event('change')); });
                await measureDensityOperation('seek', () => { document.querySelector('video').currentTime = enabled ? 120.001 : 120; });
            }, enabled);
            const before = await page.evaluate(async () => {
                const v = document.querySelector('video');
                v.playbackRate = 1;
                DanmukuM1.metrics.peakActive = 0;
                window.performanceActive = [];
                window.performanceTimer = setInterval(() => performanceActive.push({ time: v.currentTime, active: DanmukuM1.metrics.active,
                    heap: performance.memory?.usedJSHeapSize ?? null }), 1000);
                const q = v.getVideoPlaybackQuality();
                await v.play();
                return { time: v.currentTime, total: q.totalVideoFrames, dropped: q.droppedVideoFrames, rendered: DanmukuM1.metrics.rendered };
            });
            const progress = [];
            record.activeSegment = { enabled, before, progress };
            for (let minute = 0; minute < (preflight ? 1 : 10); minute++) {
                const sampleMs = preflight ? (group.maximum ? 75000 : 20000) : 60000;
                await sleep(sampleMs);
                progress.push(await page.evaluate(() => ({ time: document.querySelector('video').currentTime, paused: document.querySelector('video').paused,
                    active: DanmukuM1.metrics.active, indexBytes: DanmukuM1.metrics.indexBytes })));
                assert(!progress.at(-1).paused && progress.at(-1).time - before.time >= (preflight ? sampleMs / 1000 - 1 : (minute + 1) * 59), 'playback stalled');
                const sampled = await memory(page);
                record.peakSampledBrowserRssKiB = Math.max(record.peakSampledBrowserRssKiB || 0, sampled.browserProcessRssKiB);
                record.peakSampledHeap = Math.max(record.peakSampledHeap || 0, sampled.heap || 0); save();
                console.log(JSON.stringify({ group: group.id, enabled, minute: minute + 1 }));
                if (!preflight && group.maximum && enabled && minute === 1) {
                    record.pressureCoverage = await page.evaluate(() => ({ peakActive: DanmukuM1.metrics.peakActive, samples: performanceActive }));
                    save();
                    // The complete pressure window has ended by this point.
                    // Fail early on inadequate coverage, never pass early.
                    assert.equal(record.pressureCoverage.peakActive, 600);
                    assert(sustainedLoad(record.pressureCoverage.samples) >= 30, 'maximum must sustain >=570 for 30 seconds');
                }
            }
            const after = await page.evaluate(() => {
                const v = document.querySelector('video'), q = v.getVideoPlaybackQuality(); v.pause(); clearInterval(performanceTimer);
                return { time: v.currentTime, total: q.totalVideoFrames, dropped: q.droppedVideoFrames,
                    rendered: DanmukuM1.metrics.rendered, peakActive: DanmukuM1.metrics.peakActive,
                    activeSamples: performanceActive, indexBytes: DanmukuM1.metrics.indexBytes };
            });
            // Retain the actual samples even when a coverage/latency gate fails.
            record.frames.push({ enabled, before, after, progress, ratio: (after.dropped - before.dropped) / (after.total - before.total) });
            assert(after.indexBytes <= 2 * 1024 * 1024);
            if (!preflight) { assert(after.time - before.time >= 600); assert(after.total - before.total > 10000); }
            if (enabled) assert(after.rendered - before.rendered > (preflight ? 0 : 500), 'insufficient actual rendering');
            if (group.maximum && enabled) {
                assert.equal(after.peakActive, 600, 'maximum load must actually reach 600');
                record.sustainedHighLoadSeconds = sustainedLoad(after.activeSamples);
                assert(record.sustainedHighLoadSeconds >= 30, 'maximum must sustain >=570 for 30 seconds');
            }
        }
        assert(record.frames[1].ratio - record.frames[0].ratio <= .01, 'dropped frame delta');
        await leave(page); await Promise.all(pendingResponses);
        assert(record.backend.every(b => b.candidateCount === 1000000 && b.scannedCandidates === 2000000 && b.selectionElapsedMs >= 0));
        result.nativeNavigationCancellations = errors.filter(e => e.includes('CancelledError') && e.includes('tanstack.query-core.bundle.js') && !e.includes('/Danmuku/'));
        assert.deepEqual(errors.filter(e => !result.nativeNavigationCancellations.includes(e)), []);
        delete record.activeSegment; delete result.inProgress;
        result.performance.push(record); result.status = preflight ? 'preflight' : 'passed';
    } catch (e) {
        result.error = redact(e.stack);
        await page.screenshot({ path: path.join(dir, 'chromium-m2-performance-failure.png') }).catch(() => {});
        throw e;
    } finally {
        fs.writeFileSync(path.join(dir, 'chromium-m2-performance.json'), JSON.stringify(result, null, 2));
        await browser.close();
    }
})().catch(e => { console.error(redact(e.stack)); process.exitCode = 1; });
