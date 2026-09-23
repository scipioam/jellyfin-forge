const { test } = require('node:test');
const assert = require('node:assert/strict');
const layout = require('../../src/Jellyfin.Plugin.Danmuku/Web/danmuku-layout.js');
function make(items, changes = {}, size = [40, 20]) {
    const l = layout.create(items, { width: 100, height: 100, area: 100, mode: 'high', limit: 600, ...changes });
    items.forEach((_, i) => l.measure(i, ...size));
    return l;
}
function finish(l, time) { while (!l.advance(time, () => 0)) {} return l.snapshot(time); }
const item = (timeMs, mode = 1) => ({ timeMs, mode });
test('low/medium reserve the full box at entry, reject without consuming, release at end', () => {
    for (const [mode, accepted] of [['low', 2], ['medium', 3], ['high', 5]]) {
        const l = make(Array.from({ length: 6 }, () => item(0, 5)).concat(item(4000, 5)), { mode }, [100, 20]);
        assert.equal(finish(l, 0).indices.length, accepted);
        assert.equal(l.snapshot(0).area, accepted * 2000);
        assert.deepEqual(finish(l, 4000).indices, [6]);
    }
});
test('mixed fixed modes never overlap, overlap admits scrolling across fixed', () => {
    const items = [item(0, 5), item(0, 4), item(0, 1)];
    const l = make(items, { mode: 'overlap', height: 20 });
    assert.deepEqual(finish(l, 0).indices, [0, 2]);
    assert.equal(l.ys[0], l.ys[2]);
    assert.equal(finish(make(items, { height: 20 }), 0).indices.length, 1);
});
test('full-lifetime collision prevents a faster long comment catching a short one', () => {
    const l = make([item(0), item(2000)], { height: 20 });
    l.measure(1, 500, 20);
    assert.deepEqual(finish(l, 2000).indices, [0]);
});
test('oversize widths use clipped area but full trajectory; invalid/oversize heights rejected', () => {
    const l = make([item(0)], { mode: 'low' }, [1000, 20]);
    assert.equal(finish(l, 0).area, 2000);
    assert.equal(l.x(0, 4000), -450);
    for (const size of [[10, 101], [NaN, 20], [10, Infinity], [0, 20]]) assert.equal(finish(make([item(0)], {}, size), 0).indices.length, 0);
});
test('600 cap applies to all modes, overlap reuse deterministic and bounded', () => {
    const items = Array.from({ length: 1000 }, (_, i) => item(i));
    const l = make(items, { mode: 'overlap' });
    assert.equal(finish(l, 1000).indices.length, 600);
    assert.equal(l.dropped.count, 400);
    assert.ok(new Set(l.ys.slice(0, 600)).size > 1);
});
test('continuous, jumps, reverse seeks and cold reconstruction have identical decisions and occupancy', () => {
    const items = Array.from({ length: 20000 }, (_, i) => item(i * 13, i % 13 === 0 ? 4 : i % 17 === 0 ? 5 : 1));
    for (const mode of ['low', 'medium', 'high', 'overlap']) {
        const continuous = make(items, { mode }), cold = make(items, { mode });
        for (let t = 0; t <= 270000; t += 137) finish(continuous, t);
        finish(cold, 270000);
        assert.deepEqual(continuous.decisions, cold.decisions);
        assert.deepEqual(continuous.ys, cold.ys);
        for (const t of [245000, 10000, 0, 259980]) assert.deepEqual(continuous.snapshot(t), cold.snapshot(t));
        assert.ok(cold.bytes <= 2 * 1024 * 1024);
    }
});
test('batches stop at 256 events or four milliseconds without publishing partial target', () => {
    const l = make(Array.from({ length: 1000 }, () => item(0)), { mode: 'overlap' });
    assert.equal(l.advance(0, () => 0), false); assert.equal(l.computed, 256);
    let clock = 0;
    assert.equal(l.advance(0, () => clock++), false); assert.equal(l.computed, 260);
});
test('independent trajectory sampling finds no intersections in any non-overlap tier', () => {
    const items = Array.from({ length: 120 }, (_, i) => item(i * 97, [1, 5, 1, 4][i % 4]));
    for (const mode of ['low', 'medium', 'high']) {
        const l = make(items, { width: 600, height: 250, mode });
        items.forEach((_, i) => l.measure(i, 40 + i % 9 * 23, 20 + i % 3 * 5));
        finish(l, 20000);
        for (let t = 0; t < 16000; t += 29) {
            const indices = l.snapshot(t).indices;
            const boxes = indices.map(i => {
                const w = l.widths[i], x = items[i].mode === 1 ? 600 - (t - items[i].timeMs) / 8000 * (600 + w) : (600 - w) / 2;
                return { x, y: l.ys[i], w, h: l.heights[i] };
            });
            for (let a = 0; a < boxes.length; a++) for (let b = 0; b < a; b++) {
                const x = boxes[a], y = boxes[b];
                assert(x.y + x.h <= y.y || y.y + y.h <= x.y || x.x + x.w <= y.x || y.x + y.w <= x.x);
            }
        }
    }
});
test('a cached bootstrap hands a future resource version to that player instead of pinning old dependencies', async () => {
    const vm = require('node:vm'), fs = require('node:fs');
    const source = fs.readFileSync(require('node:path').join(__dirname, '../../src/Jellyfin.Plugin.Danmuku/Web/bootstrap.js'), 'utf8');
    for (const prefix of ['', '/jellyfin']) {
        const nodes = [];
        const location = { origin: 'http://localhost', href: 'http://localhost' + prefix + '/web/' };
        const context = { location, URL, AbortController, setTimeout, clearTimeout,
            fetch: async () => ({ ok: true, json: async () => ({ enabled: true, resourceVersion: 'm1-v6' }) }),
            document: { currentScript: { src: location.origin + prefix + '/Danmuku/Web/Bootstrap.js' }, querySelector: () => null,
                createElement: () => ({ setAttribute() {} }), head: { appendChild: node => nodes.push(node) } } };
        context.window = context;
        vm.runInNewContext(source, context);
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(nodes.filter(n => n.src).length, 1);
        assert.equal(nodes.find(n => n.src).src, location.origin + prefix + '/Danmuku/Web/Danmuku.js?v=m1-v6');
    }
});
test('the minimum cap counts fixed and scrolling together and admits again after expiry', () => {
    const l = make([item(0, 5), item(1000), item(4000)], { mode: 'overlap', limit: 1 });
    assert.deepEqual(finish(l, 1000).indices, [0]);
    assert.deepEqual(finish(l, 4000).indices, [2]);
    assert.equal(l.dropped.count, 1);
});
