'use strict';
const assert = require('node:assert/strict');
module.exports = async function ({ page, dir }) {
    const { play, leave } = require('./m2-support.cjs')(dir);
    const result = {};
    result.cancellation = await page.evaluate(async () => {
        const events = [], input = document.querySelector('input[aria-label="滚动速度"]');
        const watch = e => events.push(e.detail);
        window.addEventListener('danmuku-operation', watch);
        input.value = '5'; input.dispatchEvent(new Event('input'));
        const old = DanmukuM1.operation;
        const pending = measureDensityOperation('scrollSpeed', () => { input.value = '20'; input.dispatchEvent(new Event('input')); });
        const stale = { ...old, phase: 'committed', committedAt: performance.now() };
        window.dispatchEvent(new CustomEvent('danmuku-operation', { detail: stale }));
        const current = await pending;
        window.removeEventListener('danmuku-operation', watch);
        return { old, current, cancelled: events.some(e => e.operationId === old.operationId && e.phase === 'cancelled') };
    });
    assert(result.cancellation.cancelled);
    assert.notEqual(result.cancellation.current.operationId, result.cancellation.old.operationId);
    assert(result.cancellation.current.layoutKey.includes('|2|'));
    await leave(page); await play(page);
    assert.equal(await page.locator('input[aria-label="滚动速度"]').inputValue(), '20');
    result.savedSpeed = 2;
    result.invalid = [];
    for (const speed of [-1, '2', null, 2.01]) {
        await leave(page);
        await page.evaluate(speed => {
            const key = 'danmuku:m1:' + ApiClient.getUrl('Danmuku') + ':' + ApiClient.getCurrentUserId();
            const prefs = JSON.parse(localStorage.getItem(key)); prefs.scrollSpeed = speed; prefs.area = 50;
            localStorage.setItem(key, JSON.stringify(prefs));
        }, speed);
        await play(page);
        assert.equal(await page.locator('input[aria-label="滚动速度"]').inputValue(), '10');
        assert.equal(await page.locator('select[aria-label="区域"]').inputValue(), '50');
        result.invalid.push({ stored: speed, recovered: 1, retainedArea: 50 });
    }
    return result;
};
