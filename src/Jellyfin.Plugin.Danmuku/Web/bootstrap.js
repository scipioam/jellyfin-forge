/*
 * Danmuku web bootstrap (M1-P1).
 *
 * Responsibility: read the anonymous Web support status and, only when enabled,
 * inject the player adapter script and stylesheet from the same origin. This
 * file must never block Jellyfin Web playback, show UI, or write console noise.
 *
 * P1 checklist:
 * - Derive {base} from document.currentScript.src (absolute path including a
 *   non-root Base URL). [实测]
 * - GET {base}/Danmuku/Web/Status; quiet exit on disable / HTTP error / timeout.
 * - When enabled, inject Danmuku.css and Danmuku.js with ?v=resourceVersion.
 * - Quiet exit on 404 of those plugin resources.
 * - Do not send X-Emby-Token; status is anonymous.
 */

(function () {
    'use strict';

    // Kept identical in both entry points: old cached bootstrap can load this player alone.
    function ensureLayout(base) {
        var version = 'm2-v1', contract = 'm2-speed-v1';
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

    var STATUS_TIMEOUT_MS = 4000;
    var SCRIPT_MARKER = '/Danmuku/Web/Bootstrap.js';

    function quietCatch() {
        return null;
    }

    function scriptSrc() {
        if (document.currentScript && document.currentScript.src) {
            return document.currentScript.src;
        }

        var nodes = document.getElementsByTagName('script');
        var i;
        var src;
        for (i = 0; i < nodes.length; i++) {
            src = nodes[i].src || '';
            if (src.toLowerCase().indexOf('/danmuku/web/bootstrap.js') >= 0) {
                return src;
            }
        }

        return '';
    }

    function deriveBase(src) {
        if (!src) {
            return null;
        }

        try {
            var url = new URL(src, window.location.href);
            var path = url.pathname || '';
            var index = path.lastIndexOf(SCRIPT_MARKER);
            if (index < 0) {
                index = path.toLowerCase().lastIndexOf(SCRIPT_MARKER.toLowerCase());
            }
            if (index < 0) {
                return null;
            }

            if (url.origin !== location.origin) return null;
            return url.origin + path.slice(0, index);
        } catch (error) {
            return null;
        }
    }

    function versionQuery(value) {
        if (typeof value !== 'string' || !/^[A-Za-z0-9._-]{1,64}$/.test(value)) {
            return 'm1-p1';
        }

        return value;
    }

    function alreadyInjected(selector) {
        return document.querySelector(selector) !== null;
    }

    function injectStylesheet(href) {
        if (alreadyInjected('link[data-danmuku-resource="css"]')) {
            return;
        }

        var link = document.createElement('link');
        link.rel = 'stylesheet';
        link.href = href;
        link.setAttribute('data-danmuku-resource', 'css');
        link.onerror = quietCatch;
        document.head.appendChild(link);
    }

    function injectScript(src) {
        if (alreadyInjected('script[data-danmuku-resource="js"]')) {
            return;
        }

        var script = document.createElement('script');
        script.src = src;
        script.defer = true;
        script.setAttribute('data-danmuku-resource', 'js');
        script.onerror = quietCatch;
        document.head.appendChild(script);
    }

    function readStatus(url) {
        var controller = typeof AbortController === 'function' ? new AbortController() : null;
        var timer = window.setTimeout(function () {
            if (controller) {
                controller.abort();
            }
        }, STATUS_TIMEOUT_MS);

        var options = { credentials: 'same-origin' };
        if (controller) {
            options.signal = controller.signal;
        }

        return fetch(url, options).then(function (response) {
            window.clearTimeout(timer);
            if (!response.ok) {
                return null;
            }

            return response.json().catch(quietCatch);
        }).catch(function () {
            window.clearTimeout(timer);
            return null;
        });
    }

    var base = deriveBase(scriptSrc());
    if (base === null) {
        return;
    }

    var statusUrl = base + '/Danmuku/Web/Status';

    readStatus(statusUrl).then(function (status) {
        if (!status || status.enabled !== true) {
            return;
        }

        var version = versionQuery(status.resourceVersion);
        injectStylesheet(base + '/Danmuku/Web/Danmuku.css?v=' + encodeURIComponent(version));
        // This bootstrap may itself remain cached during a later upgrade. An
        // unknown resource version belongs to its own player/dependency gate;
        // do not pin that newer player to this bootstrap's older layout contract.
        if (version !== 'm2-v1') {
            injectScript(base + '/Danmuku/Web/Danmuku.js?v=' + encodeURIComponent(version));
            return;
        }
        ensureLayout(base).then(function () {
            injectScript(base + '/Danmuku/Web/Danmuku.js?v=' + encodeURIComponent(version));
        }).catch(layoutFailure);
    }).catch(quietCatch);
})();
