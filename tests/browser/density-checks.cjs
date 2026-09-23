const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { assertLatency } = require('./density-observer.cjs');
async function checks({ page, play, leave, dir, engine, api, plugin }) {
    const result = { cacheMode: 'route simulation executing the exact legacy m1-v4 Bootstrap bytes', operations: [] };
    try {
    const cfg = await api('/Plugins/' + plugin + '/Configuration');
    const body = Object.fromEntries(Object.entries(cfg).map(([k, v]) => [k[0].toUpperCase() + k.slice(1), v]));
    body.LongLoadLimit = 20000;
    await api('/Plugins/' + plugin + '/Configuration', body, 'POST');
    await page.evaluate(() => {
        const key = 'danmuku:m1:' + ApiClient.getUrl('Danmuku') + ':' + ApiClient.getCurrentUserId();
        const prefs = JSON.parse(localStorage.getItem(key) || '{}');
        delete prefs.densityModeV2; prefs.density = 'low'; prefs.enabled = true;
        localStorage.setItem(key, JSON.stringify(prefs));
    });
    await play(page);
    assert.equal(await page.locator('select[aria-label="密度"]').inputValue(), 'high');
    await page.locator('video').first().evaluate(v => v.pause());
    result.speed = [];
    for (const speed of [.5, 1, 1.3, 2, .5, 1]) {
        const state = await page.evaluate(speed => measureDensityOperation('scrollSpeed', () => {
            const input = document.querySelector('input[aria-label="滚动速度"]');
            input.value = String(Math.round(speed * 10)); input.dispatchEvent(new Event('input'));
        }), speed);
        assert.equal(state.phase, 'committed');
        assert(state.layoutKey.includes('|' + speed + '|'));
        assert.equal(await page.locator('input[aria-label="滚动速度"]').getAttribute('aria-valuetext'), speed.toFixed(1) + '×');
        result.speed.push(state);
    }
    // Native subtitles remain separate from Canvas; browser cue state plus screenshots.
    result.subtitles = await page.evaluate(async () => {
        const v = document.querySelector('video');
        const track = document.createElement('track'); track.kind = 'subtitles'; track.label = 'Density coexistence'; track.srclang = 'en';
        track.src = URL.createObjectURL(new Blob(['WEBVTT\n\n00:00:00.000 --> 01:01:39.000\nNative subtitle — 字幕与弹幕并存\n'], { type: 'text/vtt' }));
        v.append(track); track.track.mode = 'showing';
        await new Promise((resolve, reject) => { track.onload = resolve; track.onerror = reject; });
        return { mode: track.track.mode, cues: track.track.cues.length, active: track.track.activeCues.length };
    });
    assert.equal(result.subtitles.active, 1);
    await page.screenshot({ path: path.join(dir, engine + '-density-subtitles.png') });
    result.subtitleToggle = await page.evaluate(async () => {
        const v = document.querySelector('video'), t = v.textTracks[v.textTracks.length - 1];
        const input = document.querySelector('.danmuku-panel input[role="switch"]');
        await measureDensityOperation('enabled', () => { input.checked = false; input.dispatchEvent(new Event('change')); });
        const off = { active: t.activeCues.length, mode: t.mode, danmuku: DanmukuM1.metrics.active };
        t.mode = 'disabled'; t.mode = 'showing';
        await measureDensityOperation('enabled', () => { input.checked = true; input.dispatchEvent(new Event('change')); });
        return { off, on: { active: t.activeCues.length, mode: t.mode } };
    });
    assert.equal(result.subtitleToggle.off.active, 1); assert.equal(result.subtitleToggle.off.danmuku, 0);
    assert.equal(result.subtitleToggle.on.mode, 'showing');
    await page.evaluate(() => {
        const button = document.createElement('button'); button.id = 'density-fullscreen-test'; button.textContent = 'Test fullscreen';
        button.style.cssText = 'position:fixed;top:0;left:0;z-index:999999';
        button.onclick = () => document.querySelector('video').parentElement.requestFullscreen().catch(() => {});
        document.body.append(button);
    });
    await page.locator('#density-fullscreen-test').click();
    result.fullscreen = await page.evaluate(async () => {
        await new Promise(r => setTimeout(r, 250));
        const entered = !!document.fullscreenElement;
        if (entered) await document.exitFullscreen();
        document.querySelector('#density-fullscreen-test').remove();
        return { entered, exited: !document.fullscreenElement, subtitleMode: document.querySelector('video').textTracks[0].mode };
    });
    assert(result.fullscreen.entered && result.fullscreen.exited, 'native fullscreen round trip');
    for (const mode of ['low', 'medium', 'high', 'overlap']) {
        const state = await page.evaluate(mode => measureDensityOperation('densityModeV2', () => {
            const input = document.querySelector('select[aria-label="密度"]'); input.value = mode; input.dispatchEvent(new Event('change'));
        }), mode);
        result.operations.push(state);
        assert.equal(state.phase, 'committed');
    }
    assert(await page.evaluate(() => DanmukuM1.metrics.indexBytes <= 2 * 1024 * 1024));
    result.seek = await page.evaluate(() => measureDensityOperation('seek', () => { document.querySelector('video').currentTime = 3600; }));
    result.maximumLoad = await page.evaluate(() => ({ selected: DanmukuM1.metrics.selected, bytes: DanmukuM1.metrics.indexBytes }));
    result.highLoad = await page.evaluate(async () => {
        const state = await measureDensityOperation('seek', () => { document.querySelector('video').currentTime = 150; });
        return { state, active: DanmukuM1.metrics.active };
    });
    assert.equal(result.maximumLoad.selected, 20000);
    await page.locator('video').first().evaluate(v => v.play());
    await page.waitForFunction(() => DanmukuM1.metrics.peakActive === 600, undefined, { timeout: 10000 });
    await page.locator('video').first().evaluate(v => v.pause());
    result.highLoad.peak = await page.evaluate(() => DanmukuM1.metrics.peakActive);
    assert.equal(result.highLoad.peak, 600);
    await page.evaluate(() => measureDensityOperation('seek', () => { document.querySelector('video').currentTime = 3600; }));
    // Delay the final queued batch, not merely the first RAF. Test-owned injection,
    // using the very same identity matcher and threshold as the official matrix.
    await page.evaluate(() => {
        window.savedDensityPost = MessagePort.prototype.postMessage;
        MessagePort.prototype.postMessage = function (...args) {
            if (args[0] === 0 && window.densityDelay) {
                const port = this, original = savedDensityPost;
                if (++window.densityPosts === 2) { setTimeout(() => original.apply(port, args), 250); return; }
            }
            return savedDensityPost.apply(this, args);
        };
    });
    const delayed = [];
    result.expectedNegative = delayed;
    try {
        for (let i = 0; i < 20; i++) delayed.push(await page.evaluate(async i => {
            window.densityDelay = true; window.densityPosts = 0;
            const pending = measureDensityOperation('scale', () => {
                const s = document.querySelector('select[aria-label="字号"]'); s.value = i % 2 ? '100' : '110'; s.dispatchEvent(new Event('change'));
            });
            await new Promise(requestAnimationFrame);
            if (DanmukuM1.operation.phase !== 'computing') throw Error('First RAF incorrectly completed a delayed multibatch operation');
            return pending;
        }, i));
        assert(delayed.every(s => s.elapsed >= 250), 'final batch delay missing from measured completion');
        assert.throws(() => assertLatency(delayed, 200), /latency rejected/);
    } finally {
        await page.evaluate(() => { window.densityDelay = false; MessagePort.prototype.postMessage = savedDensityPost; delete window.savedDensityPost; });
    }
    result.expectedNegative = delayed;
    result.cancellation = await page.evaluate(async () => {
        const events = [];
        const observer = e => events.push(e.detail);
        window.addEventListener('danmuku-operation', observer);
        const input = document.querySelector('select[aria-label="区域"]');
        input.value = '25'; input.dispatchEvent(new Event('change'));
        const a = DanmukuM1.operation;
        const b = await measureDensityOperation('area', () => { input.value = '75'; input.dispatchEvent(new Event('change')); });
        window.removeEventListener('danmuku-operation', observer);
        return { a, b, events };
    });
    assert(result.cancellation.events.some(s => s.operationId === result.cancellation.a.operationId && s.phase === 'cancelled'));
    assert(!result.cancellation.events.some(s => s.operationId === result.cancellation.a.operationId && s.phase === 'committed'));
    await leave(page);
    result.metricFallbacks = [];
    for (const fallback of ['font', 'proportions']) {
        await page.evaluate(fallback => {
            window.originalDensityMeasure = CanvasRenderingContext2D.prototype.measureText;
            CanvasRenderingContext2D.prototype.measureText = function (text) {
                const m = originalDensityMeasure.call(this, text);
                if (!this.canvas.classList.contains('danmuku-canvas')) return m;
                return fallback === 'font'
                    ? { width: m.width, fontBoundingBoxAscent: Number.isFinite(m.fontBoundingBoxAscent) ? m.fontBoundingBoxAscent : parseFloat(this.font), fontBoundingBoxDescent: Number.isFinite(m.fontBoundingBoxDescent) ? m.fontBoundingBoxDescent : parseFloat(this.font) * .25 }
                    : { width: m.width };
            };
        }, fallback);
        try {
            await play(page);
            await page.locator('video').first().evaluate(v => v.pause());
            await page.evaluate(() => measureDensityOperation('seek', () => { document.querySelector('video').currentTime = 150; }));
            const pixels = await page.locator('.danmuku-canvas').evaluate(c => {
                const rgba = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
                let visible = 0, outside = 0;
                for (let y = 0; y < c.height; y++) for (let x = 0; x < c.width; x++) {
                    if (rgba[(y * c.width + x) * 4 + 3]) { visible++; if (y > c.height * .75 + 1) outside++; }
                }
                return { visible, outside, bytes: DanmukuM1.metrics.indexBytes };
            });
            assert(pixels.visible > 0); assert.equal(pixels.outside, 0);
            result.metricFallbacks.push({ fallback, ...pixels });
            await leave(page);
        } finally {
            await page.evaluate(() => { CanvasRenderingContext2D.prototype.measureText = originalDensityMeasure; delete window.originalDensityMeasure; });
        }
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await play(page);
    await page.locator('video').first().evaluate(v => v.pause());
    // Firefox replaces native controls after the narrow player pauses. Wait
    // for that native render and the adapter's retained controls to reconnect.
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    await page.locator('select[aria-label="区域"]').waitFor({ state: 'attached' });
    result.emptyFrame = await page.evaluate(async () => {
        for (const [label, reason, value] of [['区域', 'area', '25'], ['字号', 'scale', '200'], ['密度', 'densityModeV2', 'low']]) {
            await measureDensityOperation(reason, () => {
                const input = document.querySelector('select[aria-label="' + label + '"]');
                input.value = value; input.dispatchEvent(new Event('change'));
            });
        }
        return { state: DanmukuM1.operation, active: DanmukuM1.metrics.active };
    });
    assert.equal(result.emptyFrame.state.phase, 'committed'); assert.equal(result.emptyFrame.active, 0);
    await leave(page);
    await page.setViewportSize({ width: 1440, height: 900 });
    const legacy = fs.readFileSync(path.join(__dirname, 'fixtures/legacy-bootstrap-m1-v4.js'));
    const bootstrap = '**/Danmuku/Web/Bootstrap.js*', layout = '**/Danmuku/Web/Danmuku-layout.js*';
    await page.route(bootstrap, route => route.fulfill({ body: legacy, contentType: 'text/javascript', headers: { 'Cache-Control': 'public, max-age=3600' } }));
    result.legacy = [];
    for (const failure of ['none', 'network', 'version', 'timeout']) {
        let requests = 0;
        await page.route(layout, async route => {
            requests++;
            if (failure === 'network') return route.abort();
            if (failure === 'version') return route.fulfill({ body: 'window.DanmukuLayout={resourceVersion:"old",renderVersion:"old"};', contentType: 'text/javascript' });
            if (failure === 'timeout') { await new Promise(r => setTimeout(r, 11000)); return route.abort().catch(() => {}); }
            return route.continue();
        });
        await page.reload({ waitUntil: 'networkidle' });
        await play(page, failure === 'none');
        if (failure !== 'none') {
            await page.waitForFunction(() => window.__danmukuLoadFailure === true, undefined, { timeout: 15000 });
            assert.equal(await page.locator('.danmuku-canvas').count(), 0);
            assert.equal(await page.evaluate(() => !!window.DanmukuM1), false);
            assert.equal(await page.evaluate(() => Object.keys(window.__danmukuDependencies).length), 0);
        }
        assert.equal(requests, 1, 'layout dependency must be deduplicated');
        if (failure === 'none') {
            const src = await page.locator('script[data-danmuku-resource="js"]').getAttribute('src');
            await Promise.all([page.addScriptTag({ url: src }), page.addScriptTag({ url: src })]);
            assert.equal(await page.locator('.danmuku-canvas').count(), 1);
            assert.equal(requests, 1);
        }
        result.legacy.push({ failure, requests });
        await page.locator('video').first().evaluate(v => v.pause());
        await page.unroute(layout);
        await page.reload({ waitUntil: 'networkidle' });
        await page.evaluate(() => { location.hash = '/home'; });
        await play(page);
        await leave(page);
    }
    await page.unroute(bootstrap);
    // A failed response from the earlier load cannot overwrite the already
    // committed operation that turned danmuku off while that request waited.
    let releaseLoad;
    const loadGate = new Promise(resolve => { releaseLoad = resolve; });
    const delayedRoute = '**/Danmuku/Playback/**';
    await page.route(delayedRoute, async route => {
        await loadGate;
        await route.fulfill({ status: 503, contentType: 'application/json', body: '{}' });
    });
    try {
        await play(page, false);
        await page.waitForFunction(() => window.DanmukuM1?.operation?.reason === 'load' && DanmukuM1.operation.phase === 'computing');
        await page.locator('.danmuku-panel input[role="switch"]').waitFor({ state: 'attached' });
        const disabled = await page.evaluate(async () => {
            await measureDensityOperation('enabled', () => {
                const input = document.querySelector('.danmuku-panel input[role="switch"]'); input.checked = false; input.dispatchEvent(new Event('change'));
            });
            return DanmukuM1.operation;
        });
        releaseLoad();
        await page.waitForFunction(() => document.querySelector('.danmuku-retry')?.hidden === false);
        const after = await page.evaluate(() => DanmukuM1.operation);
        assert.deepEqual(after, disabled);
        assert.equal(after.phase, 'committed');
        result.lateLoadFailure = { disabled, after };
        await leave(page);
    } finally {
        releaseLoad();
        await page.unroute(delayedRoute);
    }
    result.passed = true;
    return result;
    } finally {
        fs.writeFileSync(path.join(dir, engine + '-density-results.json'), JSON.stringify(result, null, 2));
    }
}
module.exports = { checks };
