/* Jellyfin 12.1 adapter and bounded Canvas renderer. No normal-comment management list. */
(function () {
    "use strict";
    // Kept identical in both entry points: old cached bootstrap can load this player alone.
    function ensureLayout(base) {
        var version = 'm1-v5', contract = 'm1-density-v1';
        var registry = window.__danmukuDependencies || (window.__danmukuDependencies = Object.create(null));
        var key = base + '|' + version + '|' + contract;
        function valid() { return window.DanmukuLayout && window.DanmukuLayout.resourceVersion === version && window.DanmukuLayout.renderVersion === contract; }
        if (registry[key]) return registry[key];
        if (valid()) return Promise.resolve(window.DanmukuLayout);
        registry[key] = new Promise(function (resolve, reject) {
            var script = document.createElement('script'), done = false;
            var timer = setTimeout(function () { finish(Error('Layout timeout')); }, 10000);
            function finish(error) {
                if (done) return;
                done = true; clearTimeout(timer); script.onload = script.onerror = null;
                if (error) { script.remove(); delete registry[key]; reject(error); }
                else resolve(window.DanmukuLayout);
            }
            script.src = base + '/Danmuku/Web/Danmuku-layout.js?v=' + version;
            script.dataset.danmukuResource = 'layout';
            script.onload = function () { finish(valid() ? null : Error('Layout version mismatch')); };
            script.onerror = function () { finish(Error('Layout unavailable')); };
            document.head.appendChild(script);
        });
        return registry[key];
    }
    function layoutFailure() {
        if (window.__danmukuLoadFailure) return;
        window.__danmukuLoadFailure = true;
        var prompt = document.createElement('div');
        prompt.setAttribute('role', 'status'); prompt.className = 'danmuku-message';
        prompt.textContent = '弹幕组件加载失败，请刷新页面重试';
        (document.body || document.documentElement).appendChild(prompt);
        setTimeout(function () { prompt.remove(); }, 6000);
    }

    var source = document.currentScript && document.currentScript.src;
    if (!source) return;
    var resource = new URL(source, location.href);
    if (resource.origin !== location.origin || !/\/Danmuku\/Web\/(?:Assets\/)?Danmuku\.js$/i.test(resource.pathname)) return;
    var base = resource.origin + resource.pathname.slice(0, resource.pathname.toLowerCase().lastIndexOf('/danmuku/web/'));
    ensureLayout(base).then(initialize).catch(layoutFailure);
    function initialize() {
    if (window.DanmukuM1) return;
    var current = null,
        resolving = false,
        lookupAbort = null,
        generation = 0,
        waitCount = 0;
    var metrics = {
        canvases: 0,
        listeners: 0,
        frames: 0,
        timers: 0,
        observers: 0,
        requests: 0,
        rendered: 0,
        active: 0,
        selected: 0,
        peakActive: 0,
        renderedModes: { 1: 0, 4: 0, 5: 0 },
    };
    var observedOperation = null;
    window.DanmukuM1 = Object.freeze({ metrics: metrics, get operation() { return observedOperation; } });
    function api() {
        return window.ApiClient;
    }
    function path() {
        return location.hash.replace(/^#/, "").split("?")[0];
    }
    function user() {
        return api() && api().getCurrentUserId && api().getCurrentUserId();
    }
    function loggedIn() {
        return api() && api().accessToken && api().accessToken() && user();
    }
    function request(route, signal) {
        metrics.requests++;
        return fetch(api().getUrl(route), {
            signal: signal,
            headers: {
                Authorization:
                    'MediaBrowser Client="Danmuku", Device="Web", DeviceId=' +
                    JSON.stringify(api().deviceId()) +
                    ', Version="0.0.1", Token=' +
                    JSON.stringify(api().accessToken()),
            },
        })
            .then(async function (r) {
                if (!r.ok) {
                    var e = new Error("HTTP " + r.status);
                    e.status = r.status;
                    throw e;
                }
                return r.json();
            })
            .finally(function () {
                metrics.requests--;
            });
    }
    function uuid() {
        return crypto.randomUUID();
    }
    function lower(value) {
        // Jellyfin management JSON is PascalCase; playback payload is explicitly camelCase.
        if (Array.isArray(value)) return value.map(lower);
        if (value && typeof value === "object") {
            var result = {};
            Object.keys(value).forEach(function (k) {
                result[k[0].toLowerCase() + k.slice(1)] = lower(value[k]);
            });
            return result;
        }
        return value;
    }
    function teardown() {
        generation++;
        if (lookupAbort) lookupAbort.abort();
        if (current) current.dispose();
        current = null;
    }
    function inspect() {
        if (!loggedIn() || !/^\/?video(?:\/|$)/.test(path())) {
            teardown();
            return;
        }
        var video = document.querySelector(
            ".videoPlayerContainer video, video.htmlvideoplayer",
        );
        var controls = document.querySelector(".osdControls");
        if (!video) {
            if (current) teardown();
            return;
        }
        if (current && (current.video !== video || current.user !== user()))
            teardown();
        if (!controls) return;
        if (current && (current.controls !== controls || !current.connected()))
            current.mount(controls);
        if (resolving) return;
        resolving = true;
        var ticket = generation;
        var lookup = new AbortController();
        lookupAbort = lookup;
        request("Sessions", lookup.signal)
            .then(function (items) {
                if (
                    ticket !== generation ||
                    !loggedIn() ||
                    !/^\/?video(?:\/|$)/.test(path())
                )
                    return;
                var session = lower(
                    Array.isArray(items)
                        ? items
                        : items.Items || items.items || [],
                ).find(function (s) {
                    return (
                        s.deviceId === api().deviceId() &&
                        s.nowPlayingItem &&
                        s.nowPlayingItem.id
                    );
                });
                var id =
                    session &&
                    session.nowPlayingItem &&
                    session.nowPlayingItem.id;
                if (!id) {
                    metrics.diagnostic = "NoPlayingSession";
                    return;
                }
                if (current && current.media !== id) teardown();
                if (!current) {
                    if (video.readyState < 2 || video.seeking) return;
                    current = new Player(video, controls, id);
                    metrics.diagnostic = "";
                }
            })
            .catch(function (error) {
                if (error.name !== "AbortError")
                    metrics.diagnostic = String(error);
            })
            .finally(function () {
                if (lookupAbort === lookup) lookupAbort = null;
                resolving = false;
            });
    }
    function Player(video, controls, media) {
        var self = this,
            disposed = false,
            abort = null,
            raf = 0,
            timer = 0,
            items = [],
            width = 0,
            height = 0,
            ratio = 1;
        var display = { low: 100, medium: 200, high: 400, overlap: 600 },
            pending = false,
            failedOnce = false,
            pip = false,
            loaded = false;
        var key = "danmuku:m1:" + api().getUrl("Danmuku") + ":" + user();
        var prefs = {
            enabled: true,
            densityModeV2: "high",
            area: 75,
            opacity: 75,
            scale: 100,
        };
        try {
            Object.assign(prefs, JSON.parse(localStorage.getItem(key) || "{}"));
        } catch (_) {}
        delete prefs.density;
        if (!["low", "medium", "high", "overlap"].includes(prefs.densityModeV2))
            prefs.densityModeV2 = "high";
        if (![25, 50, 75, 100].includes(prefs.area)) prefs.area = 75;
        prefs.opacity = Math.max(
            10,
            Math.min(100, Math.round((+prefs.opacity || 75) / 5) * 5),
        );
        prefs.scale = Math.max(
            50,
            Math.min(200, Math.round((+prefs.scale || 100) / 10) * 10),
        );
        prefs.enabled = prefs.enabled !== false;
        this.video = video;
        this.controls = controls;
        this.connected = function () {
            return (
                canvas.isConnected && root.isConnected && message.isConnected
            );
        };
        this.media = media;
        this.user = user();
        this.id = uuid();
        var host =
            video.closest(".videoPlayerContainer") || video.parentElement;
        var canvas = document.createElement("canvas");
        canvas.className = "danmuku-canvas";
        canvas.setAttribute("aria-hidden", "true");
        host.appendChild(canvas);
        metrics.canvases++;
        var ctx = canvas.getContext("2d");
        var root = document.createElement("div");
        root.className = "danmuku-controls";
        var toggle = document.createElement("button");
        toggle.type = "button";
        toggle.className = "autoSize paper-icon-button-light danmuku-toggle";
        toggle.title = "弹幕设置";
        toggle.setAttribute("aria-expanded", "false");
        var icon = document.createElementNS(
            "http://www.w3.org/2000/svg",
            "svg",
        );
        icon.setAttribute("viewBox", "0 0 24 24");
        icon.setAttribute("class", "xlargePaperIconButton");
        icon.setAttribute("aria-hidden", "true");
        icon.setAttribute("focusable", "false");
        var stroke = document.createElementNS(
            "http://www.w3.org/2000/svg",
            "path",
        );
        stroke.setAttribute(
            "d",
            "M4 5h16v12H9l-5 3V5Z M7 9h6 M16 9h1 M7 13h2 M12 13h5",
        );
        stroke.setAttribute("fill", "none");
        stroke.setAttribute("stroke", "currentColor");
        stroke.setAttribute("stroke-width", "1.8");
        stroke.setAttribute("stroke-linejoin", "round");
        stroke.setAttribute("stroke-linecap", "round");
        icon.appendChild(stroke);
        toggle.appendChild(icon);
        toggle.setAttribute("aria-label", "弹幕设置");
        var panel = document.createElement("div");
        panel.className = "danmuku-panel";
        panel.hidden = true;
        root.append(toggle, panel);
        placeControl(controls);
        var message = document.createElement("div");
        message.className = "danmuku-message";
        message.setAttribute("role", "status");
        message.hidden = true;
        host.appendChild(message);
        var retry = document.createElement("button");
        retry.type = "button";
        retry.className = "danmuku-retry";
        retry.textContent = "重试弹幕";
        retry.hidden = true;
        root.appendChild(retry);
        var subscriptions = [];
        function listen(target, name, fn) {
            target.addEventListener(name, fn);
            subscriptions.push([target, name, fn]);
            metrics.listeners++;
        }
        function save(property) {
            try {
                localStorage.setItem(key, JSON.stringify(prefs));
            } catch (_) {}
            rebuild(property || "enabled");
        }
        function select(label, property, choices) {
            var row = document.createElement("label");
            row.textContent = label + " ";
            var input = document.createElement("select");
            input.setAttribute("aria-label", label);
            choices.forEach(function (pair) {
                var option = document.createElement("option");
                option.value = pair[0];
                option.textContent = pair[1];
                input.appendChild(option);
            });
            input.value = String(prefs[property]);
            row.appendChild(input);
            panel.appendChild(row);
            listen(input, "change", function () {
                prefs[property] =
                    property === "densityModeV2" ? input.value : +input.value;
                save(property);
            });
        }
        var enabledRow = document.createElement("label");
        enabledRow.textContent = "显示弹幕";
        var switchWrapper = document.createElement("span");
        switchWrapper.className = "danmuku-switch";
        var enabledInput = document.createElement("input");
        enabledInput.type = "checkbox";
        enabledInput.setAttribute("role", "switch");
        enabledInput.setAttribute("aria-label", "显示弹幕");
        enabledInput.checked = prefs.enabled;
        var switchTrack = document.createElement("span");
        switchTrack.className = "danmuku-switch-track";
        switchTrack.setAttribute("aria-hidden", "true");
        switchWrapper.append(enabledInput, switchTrack);
        enabledRow.appendChild(switchWrapper);
        panel.appendChild(enabledRow);
        ["keydown", "keyup"].forEach(function (name) {
            listen(enabledInput, name, function (event) {
                if (event.key !== " ") return;
                event.preventDefault();
                event.stopPropagation();
                if (name === "keydown" && !event.repeat) {
                    enabledInput.checked = !enabledInput.checked;
                    prefs.enabled = enabledInput.checked;
                    save();
                }
            });
        });
        listen(enabledInput, "change", function () {
            prefs.enabled = enabledInput.checked;
            save();
        });
        select("密度", "densityModeV2", [
            ["low", "低"],
            ["medium", "中"],
            ["high", "高"],
            ["overlap", "重叠"],
        ]);
        select(
            "区域",
            "area",
            [25, 50, 75, 100].map(function (v) {
                return [v, v + "%"];
            }),
        );
        select(
            "不透明度",
            "opacity",
            Array.from({ length: 19 }, function (_, i) {
                var v = 10 + i * 5;
                return [v, v + "%"];
            }),
        );
        select(
            "字号",
            "scale",
            Array.from({ length: 16 }, function (_, i) {
                var v = 50 + i * 10;
                return [v, v + "%"];
            }),
        );
        function placeControl(controls) {
            var favorite = controls.querySelector(".btnUserRating");
            var row = controls.querySelector(".buttons");
            if (favorite) favorite.before(root);
            else if (row) row.appendChild(root);
        }
        function placePanel() {
            if (panel.hidden) return;
            var box = root.getBoundingClientRect();
            var panelWidth = panel.getBoundingClientRect().width;
            panel.style.left =
                Math.max(
                    8 - box.left,
                    Math.min(
                        box.width - panelWidth,
                        window.innerWidth - 8 - box.left - panelWidth,
                    ),
                ) + "px";
        }
        listen(toggle, "click", function () {
            panel.hidden = !panel.hidden;
            toggle.setAttribute("aria-expanded", String(!panel.hidden));
            placePanel();
        });
        function notify(text) {
            message.textContent = text;
            message.hidden = false;
            if (!timer) metrics.timers++;
            clearTimeout(timer);
            timer = setTimeout(function () {
                message.hidden = true;
                timer = 0;
                metrics.timers--;
            }, 4000);
        }
        async function load(manual) {
            if (pending || disposed) return;
            pending = true;
            retry.disabled = true;
            abort = new AbortController();
            begin("load");
            try {
                var response = lower(
                    await request(
                        "Danmuku/Playback/" +
                            encodeURIComponent(media) +
                            "?playbackId=" +
                            encodeURIComponent(self.id) + "&renderVersion=m1-density-v1",
                        abort.signal,
                    ),
                );
                if (disposed || current !== self) return;
                items = response.items || [];
                if (response.status !== 'Disabled') {
                    var contract = response.display, limits = contract && contract.limits;
                    var values = limits && ['low', 'medium', 'high', 'overlap'].map(function (name) { return limits[name]; });
                    if (!contract || contract.renderVersion !== 'm1-density-v1' || !values ||
                        values.some(function (value, i) { return !Number.isInteger(value) || value < 1 || value > 600 || (i > 0 && value < values[i - 1]); }))
                        throw Error('RenderContractMismatch');
                    display = limits;
                }
                loaded = true;
                retry.hidden = true;
                metrics.selected = items.length;
                if (response.status === "Disabled") {
                    root.hidden = true;
                    items = [];
                }
                layout = null; layoutKey = ''; sizes = null; painted = new Uint8Array(items.length);
                enqueue();
                if (manual)
                    notify(items.length ? "弹幕加载成功" : "当前无弹幕");
            } catch (error) {
                if (disposed || error.name === "AbortError") return;
                publish("failed");
                retry.hidden = false;
                if (manual || !failedOnce)
                    notify(
                        error.status === 410
                            ? "弹幕请求已过期，请退出并重新进入播放"
                            : "弹幕加载失败，可点击重试",
                    );
                failedOnce = true;
            } finally {
                pending = false;
                retry.disabled = false;
                abort = null;
            }
        }
        listen(retry, "click", function () {
            load(true);
        });
        function geometry() {
            var box = video.getBoundingClientRect(),
                outer = host.getBoundingClientRect();
            var aspect =
                video.videoWidth && video.videoHeight
                    ? video.videoWidth / video.videoHeight
                    : box.width / Math.max(1, box.height);
            width = Math.min(box.width, box.height * aspect);
            height = Math.min(box.height, box.width / aspect);
            ratio = Math.min(3, window.devicePixelRatio || 1);
            canvas.style.left =
                box.left - outer.left + (box.width - width) / 2 + "px";
            canvas.style.top =
                box.top - outer.top + (box.height - height) / 2 + "px";
            canvas.style.width = width + "px";
            canvas.style.height = height + "px";
            var pixelWidth = Math.ceil(width * ratio), pixelHeight = Math.ceil(height * ratio);
            if (canvas.width !== pixelWidth) canvas.width = pixelWidth;
            if (canvas.height !== pixelHeight) canvas.height = pixelHeight;
            ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
            placePanel();
        }
        var layout = null, layoutKey = '', measured = 0;
        var sizes = null, painted = null, operation = null, serial = 0, taskGeneration = 0;
        var channel = new MessageChannel(), queued = false;
        channel.port1.onmessage = function () { queued = false; work(); };
        function publish(phase, committedTime) {
            // Terminal states are immutable: a late failed load must not turn a
            // newer, already committed disable operation into a failed one.
            if (!operation || operation.phase !== 'computing') return;
            operation = Object.freeze(Object.assign({}, operation, {
                phase: phase, committedTimeMs: committedTime == null ? null : committedTime,
                committedAt: phase === 'committed' ? performance.now() : null
            }));
            observedOperation = operation;
            window.dispatchEvent(new CustomEvent('danmuku-operation', { detail: operation }));
            if (phase === 'committed') metrics.lastRebuildMs = operation.committedAt - operation.startedAt;
        }
        function cancel() {
            taskGeneration++;
            if (operation && operation.phase === 'computing') publish('cancelled');
        }
        function begin(reason) {
            cancel();
            operation = Object.freeze({ playbackId: self.id, operationId: ++serial,
                generation: taskGeneration, layoutKey: layoutKey, reason: reason,
                targetTimeMs: video.currentTime * 1000, committedTimeMs: null,
                phase: 'computing', startedAt: performance.now(), committedAt: null });
            observedOperation = operation;
            window.dispatchEvent(new CustomEvent('danmuku-operation', { detail: operation }));
            return operation;
        }
        function makeLayout() {
            var nextKey = [self.id, width, height, prefs.area, prefs.scale, prefs.densityModeV2, JSON.stringify(display)].join('|');
            if (nextKey === layoutKey && layout) return;
            layoutKey = nextKey;
            layout = window.DanmukuLayout.create(items, { width: width, height: height, area: prefs.area,
                mode: prefs.densityModeV2, limit: display[prefs.densityModeV2] });
            if (!sizes || sizes.length !== items.length * 4) {
                sizes = new Float64Array(items.length * 4); measured = 0;
            }
            metrics.indexBytes = layout.bytes + sizes.byteLength + painted.byteLength;
            if (metrics.indexBytes > 2 * 1024 * 1024) throw Error('Measurement buffer budget exceeded');
        }
        function measure(i) {
            var offset = i * 4;
            if (i >= measured) {
                var size = items[i].fontSize;
                ctx.font = size + 'px sans-serif'; ctx.textBaseline = 'alphabetic';
                var m = ctx.measureText(items[i].text);
                var left = Number.isFinite(m.actualBoundingBoxLeft) ? m.actualBoundingBoxLeft : 0;
                var right = Number.isFinite(m.actualBoundingBoxRight) ? m.actualBoundingBoxRight : m.width;
                var ascent = Number.isFinite(m.actualBoundingBoxAscent) ? m.actualBoundingBoxAscent : Number.isFinite(m.fontBoundingBoxAscent) ? m.fontBoundingBoxAscent : size;
                var descent = Number.isFinite(m.actualBoundingBoxDescent) ? m.actualBoundingBoxDescent : Number.isFinite(m.fontBoundingBoxDescent) ? m.fontBoundingBoxDescent : size * .25;
                // Two CSS pixel stroke expands each side by one pixel. Advance and ink
                // extents both participate, including left bearings and emoji fallback.
                sizes[offset] = Math.max(m.width, right) + Math.max(0, left);
                sizes[offset + 1] = ascent + descent;
                sizes[offset + 2] = Math.max(0, left);
                sizes[offset + 3] = ascent;
                measured = i + 1;
            }
            var factor = prefs.scale / 100;
            layout.measure(i, sizes[offset] * factor + 2,
                Math.max(Math.ceil(items[i].fontSize * factor * 1.25), sizes[offset + 1] * factor + 2));
        }
        function draw(now) {
            ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
            ctx.clearRect(0, 0, width, height);
            metrics.active = 0;
            if (!prefs.enabled || pip || !loaded || !layout) return;
            var snapshot = layout.snapshot(now);
            ctx.globalAlpha = prefs.opacity / 100;
            var factor = prefs.scale / 100;
            ctx.setTransform(ratio * factor, 0, 0, ratio * factor, 0, 0);
            ctx.textBaseline = 'alphabetic'; ctx.lineWidth = 2 / factor;
            snapshot.indices.forEach(function (i) {
                var item = items[i];
                ctx.font = item.fontSize + 'px sans-serif';
                ctx.strokeStyle = '#000'; ctx.fillStyle = '#' + item.color.toString(16).padStart(6, '0');
                var x = layout.x(i, now) / factor + sizes[i * 4 + 2] + 1 / factor, y = layout.ys[i] / factor + sizes[i * 4 + 3] + 1 / factor;
                ctx.strokeText(item.text, x, y); ctx.fillText(item.text, x, y);
                if (!painted[i]) {
                    painted[i] = 1;
                    metrics.rendered++;
                    metrics.renderedModes[item.mode]++;
                }
            });
            metrics.active = snapshot.indices.length;
            metrics.areaUsed = snapshot.area; metrics.areaBudget = snapshot.budget;
            metrics.renderLimit = snapshot.cap; metrics.dropped = Object.assign({}, layout.dropped);
            metrics.peakActive = Math.max(metrics.peakActive, metrics.active);
        }
        function enqueue() {
            if (!queued && !disposed) { queued = true; channel.port2.postMessage(0); }
        }
        function work() {
            if (disposed || video.seeking || !loaded) return;
            try {
                var now = video.currentTime * 1000;
                makeLayout();
                // Measure the frozen collection once during load (within its 2s
                // budget), in bounded batches. Rendering scales that same font
                // coordinate system, so future seeks and size changes reuse exact
                // ink metrics without an unbounded text/bitmap cache.
                var measureStart = performance.now(), batch = 0;
                while (measured < items.length) {
                    measure(measured);
                    if (++batch >= 256 || performance.now() - measureStart >= 4) { enqueue(); return; }
                }
                if (operation && operation.phase === 'computing' && operation.layoutKey !== layoutKey) {
                    operation = Object.freeze(Object.assign({}, operation, { layoutKey: layoutKey }));
                    observedOperation = operation;
                }
                if (prefs.enabled && !pip) {
                    if (!layout.advance(now, function () { return performance.now(); }, measure)) { enqueue(); return; }
                    // The video clock may advance during a batch. Submit only a complete prefix.
                    now = video.currentTime * 1000;
                    if (!layout.advance(now, function () { return performance.now(); }, measure)) { enqueue(); return; }
                }
                draw(now);
                if (operation && operation.phase === 'computing') publish('committed', now);
                if (!video.paused && prefs.enabled && !pip) schedule();
            } catch (error) {
                publish('failed'); notify('弹幕布局失败，请退出并重新进入播放');
                metrics.diagnostic = String(error);
            }
        }
        function frame() {
            raf = 0; metrics.frames = 0;
            if (!disposed && !queued) work();
        }
        function schedule() {
            if (!raf && !disposed && !pip && prefs.enabled && !video.seeking) {
                raf = requestAnimationFrame(frame); metrics.frames = 1;
            }
        }
        function rebuild(reason) {
            if (disposed) return;
            // Native controls/geometry can settle after the response arrives.
            // They refine the initial load target, rather than completing or
            // replacing its request-to-first-draw operation prematurely.
            if (operation && operation.phase === 'computing' && operation.reason === 'load' &&
                ['geometry', 'mount', 'rate'].includes(reason)) { enqueue(); return; }
            begin(typeof reason === 'string' ? reason : 'settings');
            if (raf) cancelAnimationFrame(raf);
            raf = 0; metrics.frames = 0;
            if (!prefs.enabled || pip) {
                draw(video.currentTime * 1000); publish('committed', video.currentTime * 1000);
            } else enqueue();
        }
        listen(video, 'play', schedule);
        listen(video, 'pause', function () {
            if (raf) cancelAnimationFrame(raf);
            raf = 0; metrics.frames = 0;
            if (!video.seeking) enqueue();
        });
        listen(video, 'seeking', cancel);
        listen(video, 'seeked', function () { rebuild('seek'); });
        listen(video, 'ratechange', function () { rebuild('rate'); });
        listen(video, 'loadedmetadata', function () { geometry(); if (loaded) rebuild('geometry'); });
        listen(video, 'enterpictureinpicture', function () { pip = true; rebuild('pip'); });
        listen(video, 'leavepictureinpicture', function () { pip = false; rebuild('pip'); });
        var observer = new ResizeObserver(function () {
            var previous = width + "|" + height;
            geometry();
            if (loaded && previous !== width + "|" + height) rebuild("geometry");
        });
        observer.observe(video);
        metrics.observers++;
        geometry();
        Promise.resolve().then(function () {
            load(false);
        });
        this.mount = function (controls) {
            // Jellyfin may rebuild its controls without starting a new playback.
            // Move the existing nodes and retain the frozen collection and identifier.
            host =
                video.closest(".videoPlayerContainer") || video.parentElement;
            host.append(canvas, message);
            placeControl(controls);
            self.controls = controls;
            var previousGeometry = width + '|' + height + '|' + ratio;
            geometry();
            if (previousGeometry !== width + '|' + height + '|' + ratio) rebuild('geometry');
            // Reparenting unchanged controls must not cancel an in-flight seek
            // or settings operation. The Canvas backing store is retained.
            else if (operation && operation.phase === 'computing') enqueue();
        };
        this.dispose = function () {
            cancel();
            disposed = true;
            channel.port1.close(); channel.port2.close();
            layout = null; sizes = null; painted = null; metrics.indexBytes = 0;
            if (abort) abort.abort();
            if (raf) cancelAnimationFrame(raf);
            if (timer) {
                clearTimeout(timer);
                metrics.timers--;
            }
            observer.disconnect();
            metrics.observers--;
            subscriptions.forEach(function (s) {
                s[0].removeEventListener(s[1], s[2]);
                metrics.listeners--;
            });
            canvas.remove();
            root.remove();
            message.remove();
            items = [];
            metrics.canvases--;
            metrics.frames = metrics.active = metrics.selected = 0;
        };
    }
    function start() {
        if (!api() || !window.Events) {
            if (++waitCount < 600) setTimeout(start, 100);
            return;
        }
        window.Events.on(document, "HISTORY_UPDATE", inspect);
        document.addEventListener("viewhide", function () {
            if (current && !/^\/?video(?:\/|$)/.test(path())) teardown();
        });
        window.addEventListener("hashchange", inspect);
        // Adapter only: detects media/user changes in the target SPA. No Web-enabled status polling.
        setInterval(inspect, 1000);
        metrics.timers++;
        inspect();
    }
    start();
    }
})();
