/* Browser plugin not available; repository-pinned Playwright validates the MB flow. */
'use strict';
const assert = require('node:assert/strict'), path = require('node:path'), crypto = require('node:crypto');
module.exports = async function ({ page, dir, engine }) {
    const { api, credentials } = require('./m2-support.cjs')(dir);
    const fixture = credentials.mediaFixtures, [a, b] = fixture.videos;
    const [aRoot, bRoot] = fixture.rootLabels || ['目录 1', '目录 2'];
    const checks = [];
    const open = async search => {
        await page.evaluate(() => { location.hash = '/configurationpage?name=Danmuku'; });
        await page.locator('#dm-media-search').fill(search);
        const response = page.waitForResponse(r => r.url().includes('/Danmuku/Media?') && r.url().includes('search='));
        await page.locator('#dm-search-media').click(); await response;
    };
    const select = id => page.locator('#dm-media-list button[data-media-id="' + id + '"]').click();
    await open('ZMB 独立视频');
    await select(a);
    await page.locator('#dm-binding > .dm-media-location').filter({ hasText: aRoot }).waitFor();
    assert.match(await page.locator('#dm-media-list').innerText(), /目录 1/);
    assert.match(await page.locator('#dm-media-list').innerText(), /目录 2/);
    let release, entered;
    const gate = new Promise(r => { release = r; }), pending = new Promise(r => { entered = r; });
    const pattern = new RegExp('/Danmuku/Media/' + a + '/CombinePlans(?:\\?|$)');
    await page.route(pattern, async route => {
        const response = await route.fetch(); entered(); await gate;
        await route.fulfill({ response }).catch(() => {});
    });
    await select(a); await pending; await select(b);
    await page.locator('#dm-binding > .dm-media-location').filter({ hasText: bRoot }).waitFor();
    release(); await page.unroute(pattern);
    await page.getByRole('button', { name: '添加已有文件', exact: true }).click();
    await page.locator('#dm-file-picker .dm-media-location').filter({ hasText: bRoot }).waitFor();
    assert.equal((await api('/Danmuku/Media/' + b + '/Bindings')).fileIds.length, 0);
    await page.screenshot({ path: path.join(dir, engine + '-mb-roots.png'), fullPage: true });
    checks.push('same filenames distinguish configured roots; delayed A detail cannot replace B or its picker target');
    await open('同名集');
    await page.locator('#dm-media-list').getByText('共 4 项 · 第 1 页', { exact: true }).waitFor();
    const episodes = await page.locator('#dm-media-list').innerText();
    assert.match(episodes, /甲剧/); assert.match(episodes, /乙剧/); assert.match(episodes, /S00E01/);
    await page.screenshot({ path: path.join(dir, engine + '-mb-episodes.png'), fullPage: true });

    await open('ZMB-page');
    await page.locator('#dm-media-list').getByText('共 54 项 · 第 1 页', { exact: true }).waitFor();
    await page.locator('#dm-media-list').getByRole('button', { name: '下一页', exact: true }).click();
    await page.locator('#dm-media-list').getByText('共 54 项 · 第 2 页', { exact: true }).waitFor();
    assert.equal(await page.locator('#dm-media-list button[data-media-id]').count(), 4);
    await page.locator('#dm-media-list').getByRole('button', { name: '上一页', exact: true }).click();
    await page.locator('#dm-media-list').getByText('共 54 项 · 第 1 页', { exact: true }).waitFor();
    checks.push('ordinary Video search and next/previous pagination');

    for (const theme of ['light', 'dark']) {
        await page.evaluate(() => { location.hash = '/mypreferencesdisplay?userId=' + ApiClient.getCurrentUserId(); });
        await page.locator('[role="combobox"][aria-labelledby~="display-settings-dashboard-theme-label"]').click();
        await page.getByRole('option', { name: theme === 'light' ? 'Light' : 'Dark', exact: true }).click();
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        await page.waitForLoadState('networkidle');
        await open('ZMB <视频'); await select(fixture.long);
        await page.locator('.dm-binding-title').filter({ hasText: 'ZMB <视频 & 标题>' }).waitFor();
        assert.equal(await page.locator('#dm-binding script, #dm-binding img').count(), 0);
        for (const width of [1440, 390]) {
            await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
            const summary = page.locator('#dm-binding > details summary');
            await summary.focus(); await page.keyboard.press('Enter');
            assert(await summary.evaluate(e => e.parentElement.open));
            assert.match(await page.locator('#dm-binding > details').innerText(), /长目录中文/);
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 2));
            assert(await page.locator('#dm-media-list .dm-table-wrap').evaluate(e => e.scrollWidth <= e.clientWidth + 2),
                'media identity and binding action must fit together on narrow screens');
            await page.getByText(/^Settings saved\.?$/).waitFor({ state: 'hidden' });
            await page.screenshot({ path: path.join(dir, engine + '-mb-' + theme + '-' + width + '.png'), fullPage: true });
            await page.keyboard.press('Space');
            assert(!(await summary.evaluate(e => e.parentElement.open)));
        }
    }
    await page.setViewportSize({ width: 1440, height: 900 });
    checks.push('HTML characters rendered as text; long locations expand by keyboard; light/dark at desktop/narrow widths');

    const state = await api('/Danmuku/Media/' + a + '/Bindings'), file = state.fileIds[0];
    assert(file);
    for (const kind of ['file', 'plan']) {
        const current = await api('/Danmuku/Media/' + a + '/Bindings');
        if (kind === 'plan') {
            await api('/Danmuku/Media/' + a + '/CombinePlans', { planId: crypto.randomUUID(), name: 'browser-' + engine,
                segments: [{ fileId: file }], activate: true, expectedMediaVersion: current.version }, 'POST');
        } else {
            await api('/Danmuku/Media/' + a + '/Selection', { expectedVersion: current.version, kind: 'file', id: file }, 'PUT');
        }
        const response = page.waitForResponse(r => r.status() === 200 && r.url().includes('/Danmuku/Playback/' + a + '?'), { timeout: 60000 });
        await page.evaluate(id => { location.hash = '/details?id=' + id; }, a);
        await page.locator('button.btnPlay:visible, button[title="Play"]:visible, button[title="Resume"]:visible').first().click();
        const received = await response;
        const playbackId = new URL(received.url()).searchParams.get('playbackId');
        await page.waitForFunction(id => DanmukuM1.operation?.playbackId === id && window.DanmukuM1?.metrics.selected > 0 && DanmukuM1.operation?.phase === 'committed', playbackId, { timeout: 60000 });
        await page.waitForFunction(() => DanmukuM1.metrics.active > 0, undefined, { timeout: 15000 });
        // Firefox's protocol cannot always retrieve a consumed streaming response.
        // Read the same immutable playback snapshot in the authenticated page.
        const payload = await page.evaluate(async url => {
            const r = await fetch(url, { headers: { Authorization: 'MediaBrowser Token=' + JSON.stringify(ApiClient.accessToken()) } });
            if (!r.ok) throw Error('Snapshot read failed: ' + r.status);
            return r.json();
        }, received.url());
        assert.equal(payload.mediaId, a); assert.equal(payload.sourceKind, kind);

        await page.evaluate(() => { document.querySelector('video')?.pause(); location.hash = '/home'; });
        await page.locator('.homePage .card[data-id]').first().waitFor();
        await page.waitForFunction(() => window.DanmukuM1.metrics.canvases === 0);
    }
    checks.push('ordinary Video actually renders single-file and combine-plan danmuku in Web player');
    return checks;
};
