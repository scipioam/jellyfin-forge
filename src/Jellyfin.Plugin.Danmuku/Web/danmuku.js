/*
 * Danmuku player adapter (M1-P1).
 *
 * Responsibility: wait for Jellyfin Web SPA readiness, detect the video page,
 * bind the real video element / OSD, resolve the current media id, and render
 * a hardcoded danmaku set from video.currentTime. This is a contract spike,
 * not the P7 Canvas renderer or settings panel.
 *
 * P1 verification checklist:
 * 1. Poll until window.ApiClient and window.Events exist; never block playback.
 * 2. Route: Events.on(document, 'HISTORY_UPDATE') using state.location.pathname;
 *    hash is #/video with no itemId. Fallback: bubbling viewshow/viewhide when
 *    target.dataset.type === 'video-osd' or target.id === 'videoOsdPage'. [实测]
 * 3. Video: .videoPlayerContainer video or .htmlvideoplayer; no shadow DOM.
 * 4. Overlay sits on the video picture, pointer-events: none, OSD stays usable.
 * 5. Clock is video.currentTime only (pause / ratechange / seek). No wall-clock
 *    accumulation. Seek clears and repositions; no catch-up of skipped items.
 * 6. Media id via ApiClient.getJSON(Sessions) matching this DeviceId.
 *    DeviceId getter: ApiClient.deviceId(). [实测]
 * 7. Auth via ApiClient.getJSON/fetch only. Never send X-Emby-Token.
 * 8. OSD toggle on .osdControls: off clears immediately; on resumes at now.
 * 9. Leave page / switch media / logout (accessToken() null or /login): remove
 *    listeners, timers, and DOM. Re-entry must not duplicate them.
 * 10. Fullscreen element is document.documentElement on desktop. [实测]
 * 11. Picture-in-picture: do not create a PIP layer; pause this overlay.
 * 12. Danmaku text is textContent only (no HTML). Modes 1 / 4 / 5.
 */

(function () {
    'use strict';

    if (document.documentElement.getAttribute('data-danmuku-p1') === '1') {
        return;
    }
    document.documentElement.setAttribute('data-danmuku-p1', '1');

    var SCROLL_DURATION = 8;
    var FIXED_DURATION = 4;
    var SPA_POLL_MS = 50;
    var SPA_GIVE_UP_MS = 60000;
    var WATCH_MS = 400;
    var MEDIA_POLL_TICKS = 5;
    var FALLBACK_DURATION = 120;

    var MODE_SCROLL = 1;
    var MODE_BOTTOM = 4;
    var MODE_TOP = 5;

    var routeBound = false;
    var toggleDelegateBound = false;
    var playback = null;

    var SAMPLE_TEXTS = [
        'P1-01 scroll start',
        'P1-02 top',
        'P1-03 bottom',
        'P1-04 small',
        'P1-05 large',
        'P1-06 <b>not html</b>',
        'P1-07 <script>plain',
        'P1-08 &amp; entities',
        'P1-09 mid scroll',
        'P1-10 top 2',
        'P1-11 bottom 2',
        'P1-12 mixed size',
        'P1-13 later scroll',
        'P1-14 top 3',
        'P1-15 bottom 3',
        'P1-16 quarter',
        'P1-17 scroll',
        'P1-18 top',
        'P1-19 bottom',
        'P1-20 half',
        'P1-21 scroll',
        'P1-22 top',
        'P1-23 bottom',
        'P1-24 three-quarter',
        'P1-25 scroll',
        'P1-26 top',
        'P1-27 bottom',
        'P1-28 late scroll',
        'P1-29 late top',
        'P1-30 end'
    ];

    function apiClient() {
        return window.ApiClient || null;
    }

    function eventsApi() {
        return window.Events || null;
    }

    function isSpaReady() {
        var api = apiClient();
        var events = eventsApi();
        return !!(api && events && typeof events.on === 'function' && typeof events.off === 'function');
    }

    function waitForSpa(done) {
        if (isSpaReady()) {
            done(true);
            return;
        }

        var started = Date.now();
        var timer = window.setInterval(function () {
            if (isSpaReady()) {
                window.clearInterval(timer);
                done(true);
                return;
            }
            if (Date.now() - started >= SPA_GIVE_UP_MS) {
                window.clearInterval(timer);
                done(false);
            }
        }, SPA_POLL_MS);
    }

    function hashPath() {
        var hash = window.location.hash || '';
        if (hash.charAt(0) === '#') {
            hash = hash.slice(1);
        }
        if (hash.charAt(0) !== '/') {
            hash = '/' + hash;
        }
        var query = hash.indexOf('?');
        if (query >= 0) {
            hash = hash.slice(0, query);
        }
        return hash;
    }

    function pathFromState(state) {
        if (state && state.location && typeof state.location.pathname === 'string') {
            return state.location.pathname;
        }
        return hashPath();
    }

    function isVideoPath(path) {
        return path === '/video' || path.indexOf('/video/') === 0;
    }

    function isLoginPath(path) {
        return path === '/login' || path.indexOf('/login/') === 0;
    }

    function isLoggedOut() {
        var api = apiClient();
        if (!api) {
            return true;
        }
        if (typeof api.isLoggedIn === 'function' && !api.isLoggedIn()) {
            return true;
        }
        if (typeof api.accessToken === 'function' && !api.accessToken()) {
            return true;
        }
        return isLoginPath(hashPath());
    }

    function findVideo() {
        return document.querySelector('.videoPlayerContainer video')
            || document.querySelector('video.htmlvideoplayer')
            || document.querySelector('.htmlvideoplayer');
    }

    function findHost(video) {
        return document.querySelector('.videoPlayerContainer')
            || (video && video.parentElement)
            || null;
    }

    function findOsd() {
        return document.querySelector('.osdControls');
    }

    function newPlaybackId() {
        return 'p1-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2, 8);
    }

    function durationOf(item) {
        return item.mode === MODE_SCROLL ? SCROLL_DURATION : FIXED_DURATION;
    }

    function buildComments(duration) {
        var total = duration && isFinite(duration) && duration > 2 ? duration : FALLBACK_DURATION;
        var earlyEnd = Math.min(12, Math.max(4, total * 0.25));
        var comments = [];
        var i;
        var modeCycle = [MODE_SCROLL, MODE_TOP, MODE_BOTTOM, MODE_SCROLL, MODE_SCROLL];
        var sizeCycle = [25, 22, 22, 18, 36, 25, 28, 18, 32, 22];

        for (i = 0; i < 30; i++) {
            var time;
            if (i < 12) {
                time = 0.35 + (earlyEnd - 0.35) * (i / 11);
            } else {
                time = earlyEnd + (Math.max(total - earlyEnd - 0.4, 1) * ((i - 11) / 18));
            }
            comments.push({
                id: 'p1-' + i,
                time: time,
                mode: modeCycle[i % modeCycle.length],
                size: sizeCycle[i % sizeCycle.length],
                lane: i % 4,
                text: SAMPLE_TEXTS[i]
            });
        }

        return comments;
    }

    function deviceIdOf(api) {
        // [实测] Prefer ApiClient.deviceId(); keep short fallbacks for the spike.
        if (api && typeof api.deviceId === 'function') {
            return api.deviceId();
        }
        if (api && typeof api.deviceId === 'string') {
            return api.deviceId;
        }
        if (api && typeof api.getDeviceId === 'function') {
            return api.getDeviceId();
        }
        return '';
    }

    function readSessions(api) {
        if (!api) {
            return Promise.resolve([]);
        }
        if (typeof api.getJSON === 'function' && typeof api.getUrl === 'function') {
            return api.getJSON(api.getUrl('Sessions'));
        }
        if (typeof api.getSessions === 'function') {
            return api.getSessions();
        }
        return Promise.resolve([]);
    }

    function nowPlayingIdFromSessions(sessions, deviceId) {
        var list = sessions;
        if (!Array.isArray(list)) {
            list = sessions && Array.isArray(sessions.Items) ? sessions.Items : [];
        }

        var i;
        var session;
        var sessionDeviceId;
        var item;
        for (i = 0; i < list.length; i++) {
            session = list[i];
            sessionDeviceId = session && (session.DeviceId || session.deviceId);
            if (deviceId && sessionDeviceId === deviceId) {
                item = session.NowPlayingItem || session.nowPlayingItem;
                return item && (item.Id || item.id) ? (item.Id || item.id) : '';
            }
        }

        return '';
    }

    function refreshMediaId(session) {
        var api = apiClient();
        var generation = session.id;
        readSessions(api).then(function (sessions) {
            if (!playback || playback.id !== generation) {
                return;
            }
            var mediaId = nowPlayingIdFromSessions(sessions, deviceIdOf(api));
            if (!mediaId) {
                return;
            }
            if (playback.mediaId && playback.mediaId !== mediaId) {
                onMediaSwitch(mediaId);
                return;
            }
            playback.mediaId = mediaId;
            if (playback.overlay) {
                playback.overlay.setAttribute('data-danmuku-media-id', mediaId);
            }
        }).catch(function () {
            return null;
        });
    }

    function ensureHostPosition(host) {
        if (!host) {
            return false;
        }
        var style = window.getComputedStyle(host);
        if (style.position === 'static') {
            host.setAttribute('data-danmuku-rel', '1');
            host.style.position = 'relative';
            return true;
        }
        return false;
    }

    function restoreHostPosition(host) {
        if (host && host.getAttribute('data-danmuku-rel') === '1') {
            host.style.position = '';
            host.removeAttribute('data-danmuku-rel');
        }
    }

    function syncOverlayBox(session) {
        if (!session.overlay || !session.video || !session.host) {
            return;
        }
        var videoRect = session.video.getBoundingClientRect();
        var hostRect = session.host.getBoundingClientRect();
        session.overlay.style.left = (videoRect.left - hostRect.left) + 'px';
        session.overlay.style.top = (videoRect.top - hostRect.top) + 'px';
        session.overlay.style.width = Math.max(0, videoRect.width) + 'px';
        session.overlay.style.height = Math.max(0, videoRect.height) + 'px';
    }

    function clearNodes(session) {
        var id;
        for (id in session.nodes) {
            if (Object.prototype.hasOwnProperty.call(session.nodes, id) && session.nodes[id].parentNode) {
                session.nodes[id].parentNode.removeChild(session.nodes[id]);
            }
        }
        session.nodes = {};
    }

    function ensureItemNode(session, item) {
        var node = session.nodes[item.id];
        if (node) {
            return node;
        }
        node = document.createElement('span');
        node.className = 'danmuku-item';
        node.setAttribute('data-danmuku-id', item.id);
        node.textContent = item.text;
        node.style.fontSize = item.size + 'px';
        session.overlay.appendChild(node);
        node.setAttribute('data-width', String(node.offsetWidth));
        session.nodes[item.id] = node;
        return node;
    }

    function placeItem(session, item, time) {
        var span = durationOf(item);
        var progress = (time - item.time) / span;
        if (progress < 0 || progress >= 1) {
            return false;
        }

        var width = session.overlay.clientWidth;
        var height = session.overlay.clientHeight;
        if (width < 8 || height < 8) {
            return false;
        }

        var node = ensureItemNode(session, item);
        var textWidth = parseFloat(node.getAttribute('data-width')) || node.offsetWidth;
        var x;
        var y;
        var line = item.size + 8;

        if (item.mode === MODE_SCROLL) {
            x = width - progress * (width + textWidth);
            y = 6 + item.lane * line;
        } else if (item.mode === MODE_TOP) {
            x = (width - textWidth) / 2;
            y = 6 + item.lane * line;
        } else {
            x = (width - textWidth) / 2;
            y = height - (item.lane + 1) * line - 6;
        }

        if (y < 0 || y + item.size > height) {
            if (node.parentNode) {
                node.parentNode.removeChild(node);
            }
            delete session.nodes[item.id];
            return false;
        }

        node.style.transform = 'translate(' + x + 'px,' + y + 'px)';
        return true;
    }

    function renderAt(session, time) {
        if (!session.overlay || session.pip) {
            return;
        }
        if (!session.enabled) {
            clearNodes(session);
            return;
        }

        syncOverlayBox(session);

        var visible = {};
        var i;
        var item;
        for (i = 0; i < session.comments.length; i++) {
            item = session.comments[i];
            if (placeItem(session, item, time)) {
                visible[item.id] = true;
            }
        }

        var id;
        for (id in session.nodes) {
            if (Object.prototype.hasOwnProperty.call(session.nodes, id) && !visible[id]) {
                if (session.nodes[id].parentNode) {
                    session.nodes[id].parentNode.removeChild(session.nodes[id]);
                }
                delete session.nodes[id];
            }
        }
    }

    function stopLoop(session) {
        if (session.raf) {
            window.cancelAnimationFrame(session.raf);
            session.raf = 0;
        }
    }

    function loop(session) {
        if (!playback || playback !== session) {
            return;
        }
        if (!session.video || session.video.paused || session.pip || session.seeking) {
            session.raf = 0;
            return;
        }
        renderAt(session, session.video.currentTime);
        session.raf = window.requestAnimationFrame(function () {
            loop(session);
        });
    }

    function startLoop(session) {
        if (!session.enabled || !session.video || session.video.paused || session.pip || session.seeking) {
            return;
        }
        if (!session.raf) {
            session.raf = window.requestAnimationFrame(function () {
                loop(session);
            });
        }
    }

    function onPlaying() {
        if (!playback) {
            return;
        }
        playback.seeking = false;
        startLoop(playback);
    }

    function onPause() {
        if (!playback || !playback.video) {
            return;
        }
        stopLoop(playback);
        renderAt(playback, playback.video.currentTime);
    }

    function onSeeking() {
        if (!playback) {
            return;
        }
        playback.seeking = true;
        stopLoop(playback);
        clearNodes(playback);
    }

    function onSeeked() {
        if (!playback || !playback.video) {
            return;
        }
        playback.seeking = false;
        clearNodes(playback);
        renderAt(playback, playback.video.currentTime);
        startLoop(playback);
    }

    function onRateChange() {
        if (!playback || !playback.video) {
            return;
        }
        renderAt(playback, playback.video.currentTime);
    }

    function onLoadedMetadata() {
        if (!playback || !playback.video) {
            return;
        }
        playback.comments = buildComments(playback.video.duration);
        clearNodes(playback);
        renderAt(playback, playback.video.currentTime);
    }

    function onEnterPip() {
        if (!playback) {
            return;
        }
        playback.pip = true;
        stopLoop(playback);
        clearNodes(playback);
        if (playback.overlay) {
            playback.overlay.style.visibility = 'hidden';
        }
    }

    function onLeavePip() {
        if (!playback || !playback.video) {
            return;
        }
        playback.pip = false;
        if (playback.overlay) {
            playback.overlay.style.visibility = '';
        }
        renderAt(playback, playback.video.currentTime);
        startLoop(playback);
    }

    function unbindVideo(session) {
        var video = session.video;
        if (!video) {
            return;
        }
        video.removeEventListener('playing', onPlaying);
        video.removeEventListener('pause', onPause);
        video.removeEventListener('seeking', onSeeking);
        video.removeEventListener('seeked', onSeeked);
        video.removeEventListener('ratechange', onRateChange);
        video.removeEventListener('loadedmetadata', onLoadedMetadata);
        video.removeEventListener('enterpictureinpicture', onEnterPip);
        video.removeEventListener('leavepictureinpicture', onLeavePip);
        session.video = null;
    }

    function bindVideo(session, video) {
        if (session.video === video) {
            return;
        }
        unbindVideo(session);
        session.video = video;
        if (!video) {
            return;
        }
        video.addEventListener('playing', onPlaying);
        video.addEventListener('pause', onPause);
        video.addEventListener('seeking', onSeeking);
        video.addEventListener('seeked', onSeeked);
        video.addEventListener('ratechange', onRateChange);
        video.addEventListener('loadedmetadata', onLoadedMetadata);
        video.addEventListener('enterpictureinpicture', onEnterPip);
        video.addEventListener('leavepictureinpicture', onLeavePip);
        session.comments = buildComments(video.duration);
        session.pip = !!(document.pictureInPictureElement && document.pictureInPictureElement === video);
        if (!video.paused) {
            startLoop(session);
        } else {
            renderAt(session, video.currentTime);
        }
    }

    function ensureOverlay(session) {
        var video = findVideo();
        var host = findHost(video);
        if (!video || !host) {
            return;
        }

        if (session.host && session.host !== host) {
            restoreHostPosition(session.host);
            if (session.overlay && session.overlay.parentNode) {
                session.overlay.parentNode.removeChild(session.overlay);
            }
            session.overlay = null;
            session.hostTouched = false;
        }

        session.host = host;
        session.hostTouched = ensureHostPosition(host) || session.hostTouched;

        if (!session.overlay || !session.overlay.parentNode) {
            session.overlay = document.createElement('div');
            session.overlay.className = 'danmuku-layer';
            session.overlay.setAttribute('aria-hidden', 'true');
            session.overlay.setAttribute('data-danmuku-playback', session.id);
            if (session.mediaId) {
                session.overlay.setAttribute('data-danmuku-media-id', session.mediaId);
            }
            host.appendChild(session.overlay);
            session.nodes = {};
        }

        bindVideo(session, video);
        syncOverlayBox(session);
    }

    function setToggleState(button, enabled) {
        button.setAttribute('aria-pressed', enabled ? 'true' : 'false');
        button.title = enabled ? 'Danmuku on' : 'Danmuku off';
    }

    function toggleButtonFromEvent(event) {
        if (!event || !event.target || !event.target.closest) {
            return playback && playback.button ? playback.button : null;
        }
        return event.target.closest('button.danmuku-toggle');
    }

    function onToggleClick(event) {
        if (event) {
            event.preventDefault();
            event.stopPropagation();
        }
        var button = toggleButtonFromEvent(event);
        if (!button || !playback) {
            return;
        }
        playback.enabled = !playback.enabled;
        setToggleState(button, playback.enabled);
        if (!playback.enabled) {
            stopLoop(playback);
            clearNodes(playback);
            return;
        }
        if (playback.video) {
            renderAt(playback, playback.video.currentTime);
            startLoop(playback);
        }
    }

    function onDocumentToggleClick(event) {
        if (toggleButtonFromEvent(event)) {
            onToggleClick(event);
        }
    }

    function ensureToggle(session) {
        var osd = findOsd();
        if (!osd) {
            return;
        }
        if (session.button && session.button.parentNode === osd) {
            return;
        }
        if (session.button) {
            session.button.removeEventListener('click', onToggleClick);
            if (session.button.parentNode) {
                session.button.parentNode.removeChild(session.button);
            }
        }

        var button = document.createElement('button');
        button.type = 'button';
        button.className = 'paper-icon-button-light danmuku-toggle autoSize';
        button.setAttribute('aria-label', 'Danmuku');
        button.textContent = '弹';
        setToggleState(button, session.enabled);
        osd.appendChild(button);
        session.button = button;
        if (!toggleDelegateBound) {
            document.addEventListener('click', onDocumentToggleClick, true);
            toggleDelegateBound = true;
        }
    }

    function stopWatch(session) {
        if (session.watch) {
            window.clearInterval(session.watch);
            session.watch = 0;
        }
    }

    function watchTick(session) {
        if (!playback || playback !== session) {
            return;
        }
        if (isLoggedOut()) {
            leavePlayback();
            return;
        }
        if (!isVideoPath(hashPath())) {
            leavePlayback();
            return;
        }

        ensureOverlay(session);
        ensureToggle(session);
        session.ticks += 1;
        if (session.ticks % MEDIA_POLL_TICKS === 0) {
            refreshMediaId(session);
        }
        if (session.video && session.video.paused && !session.seeking) {
            renderAt(session, session.video.currentTime);
        }
    }

    function onMediaSwitch(mediaId) {
        if (!playback) {
            return;
        }
        stopLoop(playback);
        clearNodes(playback);
        playback.mediaId = mediaId;
        playback.id = newPlaybackId();
        if (playback.overlay) {
            playback.overlay.setAttribute('data-danmuku-playback', playback.id);
            playback.overlay.setAttribute('data-danmuku-media-id', mediaId);
        }
        if (playback.video) {
            playback.comments = buildComments(playback.video.duration);
            renderAt(playback, playback.video.currentTime);
            startLoop(playback);
        }
        refreshMediaId(playback);
    }

    function leavePlayback() {
        if (!playback) {
            return;
        }
        var session = playback;
        playback = null;
        stopLoop(session);
        stopWatch(session);
        unbindVideo(session);
        clearNodes(session);
        if (session.overlay && session.overlay.parentNode) {
            session.overlay.parentNode.removeChild(session.overlay);
        }
        if (session.button) {
            session.button.removeEventListener('click', onToggleClick);
            if (session.button.parentNode) {
                session.button.parentNode.removeChild(session.button);
            }
        }
        if (session.hostTouched) {
            restoreHostPosition(session.host);
        }
    }

    function enterPlayback() {
        if (isLoggedOut()) {
            leavePlayback();
            return;
        }
        if (playback) {
            return;
        }

        playback = {
            id: newPlaybackId(),
            mediaId: '',
            video: null,
            host: null,
            overlay: null,
            button: null,
            comments: buildComments(FALLBACK_DURATION),
            nodes: {},
            enabled: true,
            raf: 0,
            watch: 0,
            ticks: 0,
            hostTouched: false,
            pip: false,
            seeking: false
        };

        ensureOverlay(playback);
        ensureToggle(playback);
        refreshMediaId(playback);
        playback.watch = window.setInterval(function () {
            watchTick(playback);
        }, WATCH_MS);
    }

    function onHistoryUpdate(event, state) {
        var path = pathFromState(state);
        if (isLoginPath(path) || isLoggedOut()) {
            leavePlayback();
            return;
        }
        if (isVideoPath(path)) {
            enterPlayback();
            return;
        }
        leavePlayback();
    }

    function isVideoOsdTarget(target) {
        if (!target || target.nodeType !== 1) {
            return false;
        }
        return target.id === 'videoOsdPage' || (target.dataset && target.dataset.type === 'video-osd');
    }

    function onViewShow(event) {
        if (isVideoOsdTarget(event.target)) {
            enterPlayback();
        }
    }

    function onViewHide(event) {
        if (isVideoOsdTarget(event.target)) {
            leavePlayback();
        }
    }

    function onHashChange() {
        onHistoryUpdate(null, null);
    }

    function bindRouteWatchers() {
        if (routeBound) {
            return;
        }
        routeBound = true;
        eventsApi().on(document, 'HISTORY_UPDATE', onHistoryUpdate);
        document.addEventListener('viewshow', onViewShow, false);
        document.addEventListener('viewhide', onViewHide, false);
        window.addEventListener('hashchange', onHashChange, false);
        window.addEventListener('pagehide', leavePlayback, false);
    }

    waitForSpa(function (ready) {
        if (!ready) {
            return;
        }
        bindRouteWatchers();
        onHistoryUpdate(null, null);
    });
})();
