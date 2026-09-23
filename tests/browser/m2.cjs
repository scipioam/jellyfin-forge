/* Browser plugin not available; use the repository's pinned Playwright. */
'use strict';
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const { chromium, firefox } = require('playwright');
const { installObserver } = require('./density-observer.cjs');
const dir = process.argv[2], engine = process.argv[3] || 'chromium';
const credentials = JSON.parse(fs.readFileSync(path.join(dir, 'credentials.json')));
const base = credentials.base, item = credentials.item;
function redact(value) {
    let text = String(value);
    for (const who of [credentials.admin, credentials.viewer])
        for (const key of ['password', 'token']) if (who[key]) text = text.replaceAll(who[key], '[redacted]');
    return text.replace(/\b(Token|api_key|access_token)=("[^"]*"|[^\s&"'<>]+)/gi, '$1=[redacted]');
}
(async () => {
    const result = { engine, status: 'failed', fallback: 'Browser plugin not available', speed: [], externalFonts: [] };
    const browser = await { chromium, firefox }[engine].launch({ headless: true });
    const context = await browser.newContext({ locale: 'en-US', viewport: { width: 1440, height: 900 } });
    // Plugin visuals use local/native fonts. Do not let an unrelated remote
    // Jellyfin font request stall deterministic screenshots; record fallback use.
    await context.route('**/*', route => {
        const request = route.request(), url = new URL(request.url());
        if (request.resourceType() === 'font' && url.origin !== new URL(base).origin) {
            result.externalFonts.push({ origin: url.origin, path: url.pathname });
            return route.abort('failed');
        }
        return route.continue();
    });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', e => errors.push(redact(e.message)));
    try {
        result.browser = browser.version();
        await page.addInitScript(installObserver);
        await page.goto(base + '/web/#/login');
        await page.locator('#txtManualName').fill(credentials.admin.username);
        await page.locator('#txtManualPassword').fill(credentials.admin.password);
        await page.locator('button[type=submit]').first().click();
        await page.waitForURL('**/#/home');
        await page.locator('.homePage .card[data-id]').first().waitFor();
        await page.waitForLoadState('networkidle');
        await require('./m2-admin.cjs')({ page, dir, engine, item, secondItem: credentials.secondItem });
        await page.evaluate(id => { location.hash = '/details?id=' + id; }, item);
        await page.locator('button.btnPlay:not(.hide), button[title="Play"]:not(.hide), button[title="Resume"]:not(.hide)').first().click();
        await page.waitForFunction(() => window.DanmukuM1?.metrics.selected > 0 && DanmukuM1.operation?.phase === 'committed', undefined, { timeout: 60000 });
        await page.locator('video').first().evaluate(v => v.pause());
        await page.locator('button[aria-label="弹幕设置"]').click({ force: true });
        for (const width of [1440, 390]) {
            await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
            await page.evaluate(async () => {
                for (let i = 0; i < 10; i++) await new Promise(requestAnimationFrame);
            });
            await page.waitForFunction(() => DanmukuM1.operation?.phase === 'committed');
            for (const speed of [.5, 1, 1.3, 2, 1]) {
                const state = await page.evaluate(speed => measureDensityOperation('scrollSpeed', () => {
                    const input = document.querySelector('input[aria-label="滚动速度"]');
                    input.value = String(Math.round(speed * 10)); input.dispatchEvent(new Event('input'));
                }), speed);
                assert.equal(state.phase, 'committed');
                assert(state.layoutKey.includes('|' + speed + '|'));
                result.speed.push({ width, speed, state });
            }
            const geometry = await page.locator('.danmuku-speed').evaluate(row => {
                const label = row.querySelector('span').getBoundingClientRect();
                return { labelHeight: label.height, rowHeight: row.getBoundingClientRect().height, overflow: row.scrollWidth > row.clientWidth };
            });
            assert(geometry.labelHeight < 25 && !geometry.overflow, 'speed label and slider fit on one line');
            await page.screenshot({ path: path.join(dir, engine + '-m2-speed-' + width + '.png') });
        }
        assert.equal(errors.length, 0, errors.join('\n'));
        result.title = await page.title(); result.url = new URL(page.url()).pathname;
        assert(result.title.length > 0); result.status = 'passed';
    } catch (e) {
        result.error = redact(e.stack); result.errors = errors;
        result.fonts = await page.evaluate(() => Array.from(document.fonts, f => ({ family: f.family, status: f.status }))).catch(() => []);
        await page.screenshot({ path: path.join(dir, engine + '-m2-failure.png') }).catch(() => {});
        throw e;
    } finally {
        fs.writeFileSync(path.join(dir, engine + '-m2-browser.json'), JSON.stringify(result, null, 2));
        await browser.close();
    }
})().catch(e => { console.error(redact(e.stack)); process.exitCode = 1; });
