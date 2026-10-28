'use strict';
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const engines = require('playwright'), dir = process.argv[2], engine = process.argv[3];
const { base, credentials, api, redact } = require('./m2-support.cjs')(dir);
(async () => {
    const result = { status: 'failed', engine }, browser = await engines[engine].launch({ headless: true });
    const page = await browser.newPage({ locale: 'en-US', viewport: { width: 1440, height: 900 } });
    page.on('dialog', dialog => dialog.accept());
    try {
        await page.goto(base + '/web/#/login');
        await page.locator('#txtManualName').fill(credentials.admin.username);
        await page.locator('#txtManualPassword').fill(credentials.admin.password);
        await page.locator('button[type=submit]').first().click();
        await page.waitForURL('**/#/home');
        await page.waitForLoadState('networkidle');
        await page.evaluate(() => { location.hash = '/configurationpage?name=Danmuku'; });
        await page.locator('#dm-abnormal').click();
        await page.locator('#dm-media-list button[data-media-id="' + credentials.secondItem + '"]').click();
        await page.locator('#dm-binding').getByText('媒体检查：Missing', { exact: true }).waitFor();
        assert(await page.getByRole('button', { name: '新建合并方案', exact: true }).isDisabled());
        const row = page.locator('#dm-binding tr').filter({ hasText: 'orphan-' + engine });
        assert(await row.getByRole('button', { name: '编辑方案', exact: true }).isDisabled());
        assert(await row.getByRole('button', { name: '启用方案', exact: true }).isDisabled());
        await row.locator('summary').click();
        await row.getByRole('button', { name: '删除方案', exact: true }).click();
        await page.screenshot({ path: path.join(dir, engine + '-m2-missing.png'), fullPage: true });
        await row.getByRole('button', { name: '确认删除方案', exact: true }).click();
        await row.waitFor({ state: 'detached' });
        const remaining = await api('/Danmuku/Media/' + credentials.secondItem + '/CombinePlans');
        assert(!remaining.items.some(p => p.name === 'orphan-' + engine));
        result.status = 'passed'; result.browser = browser.version();
    } catch (error) {
        result.error = redact(error.stack);
        await page.screenshot({ path: path.join(dir, engine + '-m2-missing-failure.png') }).catch(() => {});
        throw error;
    } finally {
        fs.writeFileSync(path.join(dir, engine + '-m2-missing-browser.json'), JSON.stringify(result, null, 2));
        await browser.close();
    }
})().catch(error => { console.error(redact(error.stack)); process.exitCode = 1; });
