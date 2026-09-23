/* Deterministic event-time layout. No DOM, text copies, frame history or random state. */
(function (root) {
    'use strict';
    function create(items, options) {
        var n = items.length, W = options.width, H = options.height * options.area / 100;
        var speed = options.scrollSpeed;
        if (typeof speed !== 'number' || !Number.isFinite(speed) || speed < .5 || speed > 2 || Math.abs(speed * 10 - Math.round(speed * 10)) > 1e-8) speed = 1;
        var scrollDurationMs = 8000 / speed, lookback = Math.max(scrollDurationMs, 4000);
        if (n > 20000 || !Number.isFinite(W) || !Number.isFinite(H) || W < 0 || H < 0) throw Error('Invalid layout geometry');
        var widths = new Float64Array(n), heights = new Float64Array(n), ys = new Float64Array(n);
        var decisions = new Uint8Array(n), active = [], next = 0, accepted = 0, used = 0, reuse = -1;
        var budget = options.mode === 'low' ? W * H * .40 : options.mode === 'medium' ? W * H * .65 : Infinity;
        var cap = options.limit, overlap = options.mode === 'overlap';
        if (!Number.isInteger(cap) || cap < 1 || cap > 600) throw Error('Invalid render limit');
        var counts = new Int16Array(Math.floor(H / 8) + 3);
        var occupancy = new Int16Array(counts.length), occupancyHeight = 0;
        var bytes = occupancy.byteLength + widths.byteLength + heights.byteLength + ys.byteLength + decisions.byteLength + counts.byteLength;
        if (bytes > 2 * 1024 * 1024) throw Error('Layout buffer budget exceeded');
        var dropped = { budget: 0, count: 0, collision: 0, dimensions: 0 };
        function end(i) { return items[i].timeMs + (items[i].mode === 1 ? scrollDurationMs : 4000); }
        function x(i, time) { return items[i].mode === 1 ? W - (time - items[i].timeMs) / scrollDurationMs * (W + widths[i]) : (W - widths[i]) / 2; }
        function area(i) { return Math.min(widths[i], W) * heights[i]; }
        function horizontal(a, b, time) {
            if (items[a].mode === 1 && items[b].mode === 1) {
                var elapsed = time - items[b].timeMs;
                var wide = Math.max(widths[a], widths[b]);
                return elapsed * (W + wide) < scrollDurationMs * (wide + 12);
            }
            var until = Math.min(end(a), end(b));
            var d0 = x(a, time) - x(b, time), d1 = x(a, until) - x(b, until);
            return !(Math.max(d0, d1) <= -widths[a] - 12 || Math.min(d0, d1) >= widths[b] + 12);
        }
        function updateOccupancy(i, delta) {
            if (!occupancyHeight || items[i].mode !== 1) return;
            var step = Math.max(8, occupancyHeight), slots = Math.floor((H - occupancyHeight) / step) + 1;
            var first = Math.max(0, Math.floor((ys[i] - occupancyHeight) / step) + 1);
            var last = Math.min(slots - 1, Math.ceil((ys[i] + heights[i]) / step) - 1);
            if (first <= last) { occupancy[first] += delta; occupancy[last + 1] -= delta; }
        }
        function event(i) {
            var t = items[i].timeMs, h = heights[i], w = widths[i], size = area(i);
            // End-time min heap: expire in O(log cap), without rescanning 600
            // survivors for every arrival. Heap order never determines placement.
            while (active.length && end(active[0]) <= t) {
                used -= area(active[0]); updateOccupancy(active[0], -1);
                var tail = active.pop();
                if (active.length) {
                    var at = 0;
                    while (at * 2 + 1 < active.length) {
                        var child = at * 2 + 1;
                        if (child + 1 < active.length && end(active[child + 1]) < end(active[child])) child++;
                        if (end(tail) <= end(active[child])) break;
                        active[at] = active[child]; at = child;
                    }
                    active[at] = tail;
                }
            }
            var k, old;
            if (!(w > 0 && h > 0 && Number.isFinite(w) && Number.isFinite(h)) || h > H || W <= 0) { dropped.dimensions++; return; }
            if (active.length >= cap) { dropped.count++; return; }
            if (used + size > budget + 1e-7) { dropped.budget++; return; }
            var step = Math.max(8, h), slots = Math.floor((H - h) / step) + 1;
            var bottom = items[i].mode === 4, origin = bottom ? H - h : 0;
            // Difference arrays mark candidate ranges. Each active item is examined once,
            // independent of the number of candidates; no per-event heap allocations.
            counts.fill(0, 0, slots + 1);
            var blocked = 0, all = slots <= 30 ? (1 << slots) - 1 : 0;
            function range(j) {
                var lo = bottom ? (origin - ys[j] - heights[j]) / step : (ys[j] - h) / step;
                var hi = bottom ? (origin + h - ys[j]) / step : (ys[j] + heights[j]) / step;
                var first = Math.max(0, Math.floor(lo) + 1), last = Math.min(slots - 1, Math.ceil(hi) - 1);
                if (first <= last) { counts[first]++; counts[last + 1]--; if (all) blocked |= ((1 << (last - first + 1)) - 1) << first; }
            }
            for (k = 0; k < active.length; k++) {
                old = active[k];
                if (overlap && items[i].mode !== 1 && items[old].mode === 1) continue;
                if (horizontal(i, old, t)) range(old);
                if (all && blocked === all) break;
            }
            var sum = 0, chosen = -1;
            for (k = 0; k < slots; k++) { sum += counts[k]; if (sum === 0) { chosen = k; break; } }
            if (chosen < 0 && overlap && items[i].mode === 1) {
                if (occupancyHeight !== h) {
                    occupancyHeight = h; occupancy.fill(0);
                    for (k = 0; k < active.length; k++) updateOccupancy(active[k], 1);
                }
                sum = 0; var least = Infinity;
                for (k = 0; k < slots; k++) { sum += occupancy[k]; counts[k] = sum; least = Math.min(least, sum); }
                for (k = 1; k <= slots; k++) { var candidate = (reuse + k) % slots; if (counts[candidate] === least) { chosen = candidate; break; } }
                reuse = chosen;
            }
            if (chosen < 0) { dropped.collision++; return; }
            ys[i] = bottom ? origin - chosen * step : chosen * step;
            decisions[i] = 1; accepted++;
            var insert = active.length; active.push(i);
            while (insert > 0) {
                var parent = (insert - 1) >>> 1;
                if (end(active[parent]) <= end(i)) break;
                active[insert] = active[parent]; insert = parent;
            }
            active[insert] = i; used += size; updateOccupancy(i, 1);
        }
        return {
            widths: widths, heights: heights, ys: ys, decisions: decisions, bytes: bytes, dropped: dropped,
            measure: function (i, w, h) { widths[i] = w; heights[i] = h; },
            advance: function (time, clock, measure) {
                var start = clock(), count = 0;
                while (next < n && items[next].timeMs <= time) {
                    if (measure) measure(next);
                    event(next++);
                    if (++count >= 256 || clock() - start >= 4) break;
                }
                return next >= n || items[next].timeMs > time;
            },
            snapshot: function (time) {
                var lo = 0, hi = next;
                while (lo < hi) { var mid = (lo + hi) >>> 1; if (items[mid].timeMs <= time - lookback) lo = mid + 1; else hi = mid; }
                var result = [], occupancy = 0;
                for (var i = lo; i < next && items[i].timeMs <= time; i++) if (decisions[i] && end(i) > time) { result.push(i); occupancy += area(i); }
                return { indices: result, area: occupancy, budget: budget, cap: cap };
            },
            x: x,
            get accepted() { return accepted; },
            get computed() { return next; }
        };
    }
    var api = Object.freeze({ resourceVersion: 'm2-v1', renderVersion: 'm2-speed-v1', create: create });
    if (typeof module === 'object' && module.exports) module.exports = api;
    else root.DanmukuLayout = api;
})(typeof window === 'object' ? window : globalThis);
