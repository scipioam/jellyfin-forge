/* Jellyfin 12.1 adapter and bounded Canvas renderer. No normal-comment management list. */
(function () {
    "use strict";
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
    window.DanmukuM1 = { metrics: metrics };
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
                    ', Version="0.2.0", Token=' +
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
            active = [],
            cursor = 0,
            last = -1,
            width = 0,
            height = 0,
            ratio = 1;
        var display = { low: 15, medium: 30, high: 50 },
            pending = false,
            failedOnce = false,
            pip = false,
            loaded = false;
        var key = "danmuku:m1:" + api().getUrl("Danmuku") + ":" + user();
        var prefs = {
            enabled: true,
            density: "medium",
            area: 75,
            opacity: 75,
            scale: 100,
        };
        try {
            Object.assign(prefs, JSON.parse(localStorage.getItem(key) || "{}"));
        } catch (_) {}
        if (!["low", "medium", "high"].includes(prefs.density))
            prefs.density = "medium";
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
        function save() {
            try {
                localStorage.setItem(key, JSON.stringify(prefs));
            } catch (_) {}
            rebuild();
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
                    property === "density" ? input.value : +input.value;
                save();
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
        select("密度", "density", [
            ["low", "低"],
            ["medium", "中"],
            ["high", "高"],
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
            try {
                var response = lower(
                    await request(
                        "Danmuku/Playback/" +
                            encodeURIComponent(media) +
                            "?playbackId=" +
                            encodeURIComponent(self.id),
                        abort.signal,
                    ),
                );
                if (disposed || current !== self) return;
                items = response.items || [];
                display = response.display || display;
                loaded = true;
                retry.hidden = true;
                metrics.selected = items.length;
                if (response.status === "Disabled") {
                    root.hidden = true;
                    items = [];
                }
                rebuild();
                if (manual)
                    notify(items.length ? "弹幕加载成功" : "当前无弹幕");
            } catch (error) {
                if (disposed || error.name === "AbortError") return;
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
            canvas.width = Math.ceil(width * ratio);
            canvas.height = Math.ceil(height * ratio);
            ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
            placePanel();
        }
        function startIndex(time) {
            var lo = 0,
                hi = items.length;
            while (lo < hi) {
                var mid = (lo + hi) >>> 1;
                if (items[mid].timeMs < time * 1000) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }
        function xAt(item, now) {
            return item.mode === 1
                ? width - ((now - item.start) / 8) * (width + item.width)
                : (width - item.width) / 2;
        }
        function admit(item, now) {
            var start = item.timeMs / 1000,
                duration = item.mode === 1 ? 8 : 4;
            if (
                now >= start + duration ||
                active.length >= display[prefs.density]
            )
                return;
            var size = (item.fontSize * prefs.scale) / 100,
                line = Math.ceil(size * 1.25),
                area = (height * prefs.area) / 100;
            if (line > area || size < 1) return;
            ctx.font = size + "px sans-serif";
            var textWidth = ctx.measureText(item.text).width;
            var candidate = {
                mode: item.mode,
                start: start,
                end: start + duration,
                width: textWidth,
                size: size,
                text: item.text,
                color: item.color,
                line: line,
                y: 0,
            };
            for (
                var offset = 0;
                offset + line <= area;
                offset += Math.max(8, line)
            ) {
                candidate.y = item.mode === 4 ? area - offset - line : offset;
                var collision = active.some(function (other) {
                    if (
                        other.y + other.line <= candidate.y ||
                        candidate.y + line <= other.y
                    )
                        return false;
                    if (other.mode !== 1 || candidate.mode !== 1) return true;
                    var t = Math.min(other.end, candidate.end);
                    return (
                        xAt(candidate, now) <
                            xAt(other, now) + other.width + 12 ||
                        xAt(candidate, t) < xAt(other, t) + other.width + 12
                    );
                });
                if (!collision) {
                    active.push(candidate);
                    metrics.rendered++;
                    metrics.renderedModes[item.mode]++;
                    return;
                }
            }
        }
        function draw(now) {
            ctx.clearRect(0, 0, width, height);
            if (!prefs.enabled || pip || !loaded) {
                metrics.active = 0;
                return;
            }
            active = active.filter(function (item) {
                return item.end > now;
            });
            while (cursor < items.length && items[cursor].timeMs <= now * 1000)
                admit(items[cursor++], now);
            ctx.globalAlpha = prefs.opacity / 100;
            ctx.textBaseline = "top";
            ctx.lineWidth = 2;
            active.forEach(function (item) {
                ctx.font = item.size + "px sans-serif";
                ctx.strokeStyle = "#000";
                ctx.fillStyle = "#" + item.color.toString(16).padStart(6, "0");
                var x = xAt(item, now);
                ctx.strokeText(item.text, x, item.y);
                ctx.fillText(item.text, x, item.y);
            });
            metrics.active = active.length;
            metrics.peakActive = Math.max(metrics.peakActive, active.length);
            last = now;
        }
        function frame() {
            raf = 0;
            metrics.frames = 0;
            if (disposed || pip) return;
            var now = video.currentTime;
            if (now < last || Math.abs(now - last) > 1) reset(now);
            draw(now);
            if (!video.paused && prefs.enabled) schedule();
        }
        function schedule() {
            if (!raf && !disposed && !pip) {
                raf = requestAnimationFrame(frame);
                metrics.frames = 1;
            }
        }
        function reset(now) {
            active = [];
            cursor = startIndex(Math.max(0, now - 8));
            last = now;
        }
        function rebuild() {
            var started = performance.now(),
                now = video.currentTime;
            reset(now);
            draw(now);
            schedule();
            metrics.lastRebuildMs = performance.now() - started;
        }
        listen(video, "play", schedule);
        listen(video, "pause", function () {
            if (raf) cancelAnimationFrame(raf);
            raf = 0;
            metrics.frames = 0;
            draw(video.currentTime);
        });
        listen(video, "seeked", rebuild);
        listen(video, "ratechange", rebuild);
        listen(video, "loadedmetadata", function () {
            geometry();
            rebuild();
        });
        listen(video, "enterpictureinpicture", function () {
            pip = true;
            if (raf) cancelAnimationFrame(raf);
            raf = 0;
            metrics.frames = 0;
            ctx.clearRect(0, 0, width, height);
        });
        listen(video, "leavepictureinpicture", function () {
            pip = false;
            rebuild();
        });
        var observer = new ResizeObserver(function () {
            geometry();
            rebuild();
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
            geometry();
            rebuild();
        };
        this.dispose = function () {
            disposed = true;
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
            active = [];
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
})();
