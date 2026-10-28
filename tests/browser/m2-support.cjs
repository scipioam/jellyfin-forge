'use strict';
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
module.exports = function support(dir) {
    const credentials = JSON.parse(fs.readFileSync(path.join(dir, 'credentials.json')));
    const base = credentials.base, item = credentials.item;
    const plugin = '6f79690c-c1d0-4738-b241-09aaa2c570e7';
    const normalize = v => Array.isArray(v) ? v.map(normalize) : v && typeof v === 'object'
        ? Object.fromEntries(Object.entries(v).map(([k, x]) => [k[0].toLowerCase() + k.slice(1), normalize(x)])) : v;
    const auth = token => 'MediaBrowser Client="M2", Device="Test", DeviceId="forge-m2-test", Version="0.0.2", Token=' + JSON.stringify(token);
    const redact = value => {
        let text = String(value);
        for (const who of [credentials.admin, credentials.viewer])
            for (const key of ['password', 'token']) if (who[key]) text = text.replaceAll(who[key], '[redacted]');
        return text.replace(/\b(Token|api_key|access_token)=("[^"]*"|[^\s&"'<>]+)/gi, '$1=[redacted]');
    };
    async function api(route, body, method = body === undefined ? 'GET' : 'POST', expected = 200) {
        const r = await fetch(base + route, { method, headers: { Authorization: auth(credentials.admin.token),
            'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
        const text = await r.text();
        assert.equal(r.status, expected, route + ': ' + redact(text.slice(0, 250)));
        return text ? normalize(JSON.parse(text)) : null;
    }
    async function login(page, who = credentials.admin) {
        await page.goto(base + '/web/#/login', { waitUntil: 'domcontentloaded' });
        await page.locator('#txtManualName').fill(who.username);
        await page.locator('#txtManualPassword').fill(who.password);
        await page.locator('button[type=submit]').first().click();
        await page.waitForURL('**/#/home');
        await page.locator('.homePage .card[data-id]').first().waitFor();
        await page.waitForFunction(() => window.ApiClient?.getCurrentUserId());
        await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    }
    async function play(page, media = item) {
        if (new URL(page.url()).hash.startsWith('#/home')) {
            await page.locator('.homePage .card[data-id]').first().waitFor();
            await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
        }
        const response = page.waitForResponse(r => r.status() === 200 && r.url().includes('/Danmuku/Playback/' + media + '?'), { timeout: 60000 });
        await page.evaluate(id => { location.hash = '/details?id=' + id; }, media);
        await page.locator('button.btnPlay:visible, button[title="Play"]:visible, button[title="Resume"]:visible').first().click();
        const playbackId = new URL((await response).url()).searchParams.get('playbackId');
        await page.waitForFunction(id => DanmukuM1.operation?.playbackId === id && document.querySelector('video')?.readyState >= 2 &&
            !document.querySelector('video').paused && window.DanmukuM1?.metrics.selected > 0 &&
            DanmukuM1.operation?.phase === 'committed', playbackId, { timeout: 60000 });
    }
    async function leave(page) {
        await page.evaluate(() => { document.querySelector('video')?.pause(); location.hash = '/home'; });
        await page.waitForFunction(() => ['canvases', 'listeners', 'frames', 'selected', 'requests', 'observers', 'indexBytes']
            .every(k => DanmukuM1.metrics[k] === 0) && DanmukuM1.metrics.timers === 1);
    }
    async function memory(page) {
        const sample = await page.evaluate(() => {
            if (window.gc) gc();
            return { ...DanmukuM1.metrics, heap: performance.memory?.usedJSHeapSize ?? null };
        });
        const processes = execFileSync('ps', ['-eo', 'pid=,ppid=,rss='], { encoding: 'utf8' }).trim().split('\n')
            .map(line => { const [pid, parent, rss] = line.trim().split(/\s+/).map(Number); return { pid, parent, rss }; });
        const owned = new Set([process.pid]);
        let changed = true;
        while (changed) { changed = false; for (const p of processes) if (owned.has(p.parent) && !owned.has(p.pid)) { owned.add(p.pid); changed = true; } }
        sample.browserProcessRssKiB = processes.filter(p => p.pid !== process.pid && owned.has(p.pid)).reduce((n, p) => n + p.rss, 0);
        return sample;
    }
    return { credentials, base, item, plugin, api, auth, normalize, redact, login, play, leave, memory };
};
