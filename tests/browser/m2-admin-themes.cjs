'use strict';
const assert = require('node:assert/strict'), path = require('node:path'), crypto = require('node:crypto');
module.exports = async function ({ page, dir, engine, item }) {
    const { api, base, auth, credentials, normalize } = require('./m2-support.cjs')(dir);
    const checks = [], editor = page.locator('#dm-plan-editor');
    const open = async () => {
        await page.evaluate(() => { location.hash = '/configurationpage?name=Danmuku'; });
        await page.locator('#dm-media-list button[data-media-id="' + item + '"]').click();
        await page.getByRole('button', { name: '新建合并方案', exact: true }).click();
    };
    for (const theme of ['light', 'dark']) {
        // The native settings screen changes both the stylesheet and the MUI
        // palette. Merely appending theme.css leaves the old CSS variables active.
        await page.evaluate(() => { location.hash = '/mypreferencesdisplay?userId=' + ApiClient.getCurrentUserId(); });
        await page.locator('[role="combobox"][aria-labelledby~="display-settings-dashboard-theme-label"]').click();
        await page.getByRole('option', { name: theme === 'light' ? 'Light' : 'Dark', exact: true }).click();
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        await page.waitForLoadState('networkidle');
        await open();
        await editor.getByRole('searchbox', { name: '来源文件搜索', exact: true }).fill('m2-source');
        await editor.getByRole('button', { name: '查找来源文件', exact: true }).click();
        await editor.getByRole('button', { name: '添加片段', exact: true }).first().click();
        await page.waitForFunction(() => !document.querySelector('#dm-plan-editor button.dm-primary').disabled);
        await page.waitForFunction(light => {
            const value = getComputedStyle(document.documentElement).getPropertyValue('--jf-palette-background-default');
            const canvas = document.createElement('canvas'), ctx = canvas.getContext('2d');
            ctx.fillStyle = value.trim() || getComputedStyle(document.documentElement).backgroundColor;
            ctx.fillRect(0, 0, 1, 1); const rgb = ctx.getImageData(0, 0, 1, 1).data;
            return light ? rgb[0] > 200 : rgb[0] < 80;
        }, theme === 'light');
        const tail = editor.getByRole('checkbox', { name: '到文件末尾', exact: true });
        for (const width of [1440, 390]) {
            await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
            await tail.focus();
            assert(await tail.isChecked());
            assert(await editor.getByRole('textbox', { name: '片段 1 源结束', exact: true }).isDisabled());
            await page.keyboard.press('Space');
            await editor.getByRole('textbox', { name: '片段 1 源结束', exact: true }).fill('20:00');
            await tail.focus();
            await page.keyboard.press('Tab');
            await page.keyboard.press('Shift+Tab');
            await page.waitForFunction(() => !document.querySelector('#dm-plan-editor button.dm-primary').disabled);
            assert(await tail.evaluate(e => e === document.activeElement));
            assert.equal(await tail.evaluate(e => getComputedStyle(e).outlineWidth), '2px');
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 2));
            await page.getByText(/^Settings saved\.?$/).waitFor({ state: 'hidden' });
            await page.evaluate(() => window.scrollTo(0, 0));
            await page.screenshot({ path: path.join(dir, engine + '-m2-native-' + theme + '-' + width + '.png'), fullPage: true });
            await page.keyboard.press('Space');
        }
        await editor.getByRole('button', { name: '取消编辑', exact: true }).click();
        await page.setViewportSize({ width: 1440, height: 900 });
    }
    checks.push('native dashboard light/dark palettes; 1440/390 viewport; checked/disabled/focused checkbox with keyboard');
    const files = [], prefix = 'paging-' + engine + '-';
    try {
        for (let i = 0; i < 51; i++) {
            const batch = await api('/Danmuku/ImportBatches', { batchId: crypto.randomUUID(), operation: 'import', fileNames: [prefix + i + '.json'] }, 'POST');
            const response = await fetch(base + '/Danmuku/ImportBatches/' + batch.batchId + '/Files/0', {
                method: 'POST', headers: { Authorization: auth(credentials.admin.token), 'Content-Type': 'application/octet-stream' },
                body: JSON.stringify([{ progress: i, content: prefix + i }]) });
            assert.equal(response.status, 202);
            const task = normalize(await response.json());
            let done;
            for (let n = 0; n < 120; n++) {
                done = await api('/Danmuku/Imports/' + task.taskId);
                if (done.status === 'Completed') break;
                assert(!['Failed', 'Interrupted'].includes(done.status));
                await new Promise(resolve => setTimeout(resolve, 100));
            }
            assert.equal(done.status, 'Completed'); files.push(done.fileId);
        }
        await open();
        await editor.getByRole('searchbox', { name: '来源文件搜索', exact: true }).fill(prefix);
        await editor.getByRole('button', { name: '查找来源文件', exact: true }).click();
        await editor.getByText('共 51 项 · 第 1 页', { exact: true }).waitFor();
        assert.equal(await editor.getByRole('button', { name: '添加片段', exact: true }).count(), 50);
        await editor.getByRole('button', { name: '下一页', exact: true }).click();
        await editor.getByText('共 51 项 · 第 2 页', { exact: true }).waitFor();
        assert.equal(await editor.getByRole('button', { name: '添加片段', exact: true }).count(), 1);
        assert(await editor.getByRole('button', { name: '下一页', exact: true }).isDisabled());
        await editor.getByRole('button', { name: '添加片段', exact: true }).click();
        await page.waitForFunction(() => !document.querySelector('#dm-plan-editor button.dm-primary').disabled);
        await editor.getByRole('button', { name: '上一页', exact: true }).click();
        await editor.getByText('共 51 项 · 第 1 页', { exact: true }).waitFor();
        await editor.getByRole('button', { name: '取消编辑', exact: true }).click();
        checks.push('51 real imported sources: search, 50/1 pagination, page two selection and previous page');
    } finally {
        for (const file of files) await api('/Danmuku/Files/' + file, undefined, 'DELETE');
    }
    return checks;
};
