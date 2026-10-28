/* Read-only P8 checks against an existing dev account in a fresh browser profile. */
'use strict';
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const { chromium } = require('playwright');
const dir = process.argv[2], expected = process.argv[3] || 'm2-v2';
const credentials = JSON.parse(fs.readFileSync(path.join(dir, 'deployment-auth.json')));
const redact = value => String(value).replaceAll(credentials.token, '[redacted]');
(async () => {
    const result = { status: 'failed', resourceVersion: expected, checks: [] };
    const browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ locale: 'en-US', viewport: { width: 1440, height: 900 } });
    const errors = [];
    page.on('pageerror', error => errors.push(redact(error.stack)));
    try {
        await page.goto(credentials.base + '/web/#/login');
        await page.waitForFunction(() => window.ApiClient?.serverId());
        // Use the pinned Jellyfin native credential provider. This only
        // populates this test browser's storage; no account or key is created.
        await page.evaluate(({ token, userId }) => {
            let webpackRequire;
            const chunks = Object.keys(window).find(k => k.startsWith('webpackChunk'));
            if (!chunks) throw Error('native module registry unavailable');
            window[chunks].push([['forge-deployment-auth'], {}, r => { webpackRequire = r; }]);
            const manager = webpackRequire(68309).A, provider = manager.credentialProvider();
            const state = provider.credentials();
            const server = state.Servers.find(s => s.Id === ApiClient.serverId());
            if (!server) throw Error('native server credential record unavailable');
            server.AccessToken = token; server.UserId = userId; server.DateLastAccessed = Date.now();
            provider.credentials(state);
        }, credentials);
        await page.reload({ waitUntil: 'networkidle' });
        await page.waitForFunction(id => ApiClient.getCurrentUserId()?.replaceAll('-', '') === id.replaceAll('-', ''), credentials.userId);
        await page.evaluate(() => { location.hash = '/configurationpage?name=Danmuku'; });
        await page.locator('#DanmukuConfigPage').waitFor();
        await page.locator('[data-tab="files"]').click();
        for (const count of credentials.commentCounts) await page.locator('#dm-files-list').getByText(String(count), { exact: true }).first().waitFor();
        await page.waitForFunction(version => window.DanmukuLayout?.resourceVersion === version && window.DanmukuM1?.metrics.timers === 1, expected);
        assert.equal(await page.locator('script[data-danmuku-resource="js"]').count(), 1);
        assert.equal(await page.evaluate(() => DanmukuM1.metrics.timers), 1);
        await page.screenshot({ path: path.join(dir, 'development-files.png'), fullPage: true });
        result.checks.push('existing account auto-login, administrator file page and preserved per-file comment counts; current resource chain initializes once');
        await page.locator('[data-tab="media"]').click();
        const media = await page.evaluate(async () => {
            const response = await fetch(ApiClient.getUrl('Danmuku/Media?limit=50'), {
                headers: { Authorization: 'MediaBrowser Token=' + JSON.stringify(ApiClient.accessToken()) }
            });
            if (!response.ok) throw Error('Media presentation request failed: ' + response.status);
            return response.json();
        });
        const item = media.items.find(row => row.canBind);
        if (item) {
            const row = page.locator('#dm-media-list tr').filter({ has: page.locator('button[data-media-id="' + item.id + '"]') });
            await row.locator('.dm-media-title').filter({ hasText: item.displayTitle }).waitFor();
            assert.equal(await row.locator('.dm-media-location').innerText(), item.locationText);
            // Opening details refreshes persistent CheckStatus/CheckedAt fields.
            // Exercise that flow in disposable fixtures; this deployment probe
            // preserves every existing business row exactly.
            result.checks.push('existing media list title/location and action target ID agree with the API; no detail/check-state writes');
        } else {
            result.checks.push('existing media presentation API and page reachable; no bindable item in first page');
        }
        await page.screenshot({ path: path.join(dir, 'development-media.png'), fullPage: true });
        const variants = [
            ['old-bootstrap', 'Bootstrap.js', 'legacy-bootstrap-m2-v1.js', false],
            ['old-layout', 'Danmuku-layout.js', 'legacy-layout-m1-v5.js', true]
        ];
        for (const [name, resource, fixture, fails] of variants) {
            const pattern = '**/Danmuku/Web/' + resource + '*';
            await page.route(pattern, route => route.fulfill({ contentType: 'text/javascript', body: fs.readFileSync(path.join(__dirname, 'fixtures', fixture)) }));
            try {
                await page.reload({ waitUntil: 'networkidle' });
                if (fails) await page.waitForFunction(() => window.__danmukuLoadFailure === true);
                else await page.waitForFunction(version => window.DanmukuLayout?.resourceVersion === version && window.DanmukuM1?.metrics.timers === 1, expected);
                await page.locator('#DanmukuConfigPage').waitFor();
            } finally { await page.unroute(pattern); }
            await page.reload({ waitUntil: 'networkidle' });
            await page.waitForFunction(version => window.DanmukuLayout?.resourceVersion === version && window.DanmukuM1?.metrics.timers === 1, expected);
            assert.equal(await page.evaluate(() => DanmukuM1.metrics.timers), 1);
            result.checks.push(name + ': expected gate behavior and ordinary refresh recovery; admin page remains reachable');
        }
        assert.deepEqual(errors, []);
        result.status = 'passed'; result.browser = browser.version();
    } catch (error) {
        result.error = redact(error.stack); result.errors = errors;
        await page.screenshot({ path: path.join(dir, 'development-failure.png') }).catch(() => {});
        throw error;
    } finally {
        fs.writeFileSync(path.join(dir, 'development-browser.json'), JSON.stringify(result, null, 2));
        await browser.close();
    }
})().catch(error => { console.error(redact(error.stack)); process.exitCode = 1; });
