'use strict';
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
module.exports = async function ({ page, dir, engine }) {
    const { play, leave, item } = require('./m2-support.cjs')(dir), checks = [];
    const resources = [
        ['old-bootstrap-new-player', 'Bootstrap.js', 'legacy-bootstrap-m1-v4.js', false],
        ['old-bootstrap-v5-new-layout', 'Bootstrap.js', 'legacy-bootstrap-m1-v5.js', false],
        ['old-bootstrap-m2-v1-new-player', 'Bootstrap.js', 'legacy-bootstrap-m2-v1.js', false],
        ['new-bootstrap-old-layout', 'Danmuku-layout.js', 'legacy-layout-m1-v5.js', true],
        ['new-bootstrap-m2-v1-layout', 'Danmuku-layout.js', 'reference-layout-m2-original.cjs', true],
        ['new-bootstrap-old-player', 'Danmuku.js', 'legacy-player-m1-v5.js', true]
    ];
    await leave(page);
    for (const [name, resource, fixture, fails] of resources) {
        const pattern = '**/Danmuku/Web/' + resource + '*';
        await page.route(pattern, route => route.fulfill({ contentType: 'text/javascript',
            body: fs.readFileSync(path.join(__dirname, 'fixtures', fixture)), headers: { 'Cache-Control': 'public, max-age=3600' } }));
        try {
            await page.reload({ waitUntil: 'networkidle' });
            if (fails) {
                await page.waitForFunction(() => window.__danmukuLoadFailure === true, undefined, { timeout: 15000 });
                await page.evaluate(id => { location.hash = '/details?id=' + id; }, item);
                await page.locator('button.btnPlay:visible,button[title="Play"]:visible,button[title="Resume"]:visible').first().click();
                await page.waitForFunction(() => document.querySelector('video')?.currentTime > 0 && !document.querySelector('video').paused);
                assert.equal(await page.locator('.danmuku-canvas').count(), 0);
            } else {
                await play(page);
                assert.equal(await page.locator('.danmuku-canvas').count(), 1);
            }
            await page.screenshot({ path: path.join(dir, engine + '-m2-cache-' + name + '.png') });
        } finally { await page.unroute(pattern); }
        await page.locator('video').first().evaluate(v => v.pause());
        await page.reload({ waitUntil: 'networkidle' });
        await page.evaluate(() => { location.hash = '/home'; });
        await play(page);
        const src = await page.locator('script[data-danmuku-resource="js"]').getAttribute('src');
        await page.addScriptTag({ url: src });
        assert.equal(await page.locator('.danmuku-canvas').count(), 1);
        assert.equal(await page.evaluate(() => DanmukuM1.metrics.timers), 1);
        await leave(page);
        checks.push({ name, expectedFailure: fails, ordinaryRefreshRecovered: true, singleInitialization: true });
    }
    return checks;
};
