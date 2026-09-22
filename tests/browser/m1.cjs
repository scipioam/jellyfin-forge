/* Browser plugin not available; pinned Playwright 1.58.2 is the approved fallback. */
"use strict";
const fs = require("fs"),
    path = require("path"),
    crypto = require("crypto"),
    assert = require("assert/strict"),
    { execFileSync } = require("child_process");
const { chromium, firefox } = require("playwright");
const dir = process.argv[2],
    engine = process.argv[3] || "chromium",
    performanceRun = process.argv.includes("--performance"),
    preflight = process.argv.includes("--preflight");
const credentials = JSON.parse(
        fs.readFileSync(path.join(dir, "credentials.json")),
    ),
    base = credentials.base,
    item = credentials.item;
const plugin = "6f79690c-c1d0-4738-b241-09aaa2c570e7";
const auth = (token) =>
    'MediaBrowser Client="M1 Browser", Device="Test", DeviceId="forge-m1-browser-helper", Version="0.2.0", Token=' +
    JSON.stringify(token);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
function redact(value) {
    let text = String(value);
    for (const who of [credentials.admin, credentials.viewer])
        for (const key of ["password", "token"])
            if (who[key]) text = text.replaceAll(who[key], "[redacted]");
    return text.replace(
        /\b(Token|api_key|access_token)=("[^"]*"|[^\s&"'<>]+)/gi,
        "$1=[redacted]",
    );
}
function normalize(v) {
    if (Array.isArray(v)) return v.map(normalize);
    if (v && typeof v === "object")
        return Object.fromEntries(
            Object.entries(v).map(([k, x]) => [
                k[0].toLowerCase() + k.slice(1),
                normalize(x),
            ]),
        );
    return v;
}
async function api(route, body, method = "GET") {
    const r = await fetch(base + route, {
        method,
        headers: {
            Authorization: auth(credentials.admin.token),
            "Content-Type": "application/json",
        },
        body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await r.text();
    assert(r.ok, route + " HTTP " + r.status + " " + text.slice(0, 150));
    return text ? normalize(JSON.parse(text)) : null;
}
async function sha256File(file) {
    const hash = crypto.createHash("sha256");
    for await (const chunk of fs.createReadStream(file)) hash.update(chunk);
    return hash.digest("hex");
}
async function seed() {
    const binding = await api("/Danmuku/Media/" + item + "/Bindings");
    const batch = await api(
        "/Danmuku/ImportBatches",
        {
            batchId: crypto.randomUUID(),
            mediaId: item,
            expectedVersion: binding.version,
            fileNames: ["synthetic.json"],
        },
        "POST",
    );
    const fixture = path.join(dir, "synthetic.json");
    const fd = fs.openSync(fixture, "w");
    fs.writeSync(fd, "[");
    const count = performanceRun ? 300000 : 4000;
    for (let i = 0; i < count; i++)
        fs.writeSync(
            fd,
            (i ? "," : "") +
                JSON.stringify({
                    progress: Math.floor((i * 3699000) / count),
                    content: "弹幕 synthetic " + i,
                    mode: [1, 1, 1, 4, 5][i % 5],
                    fontsize: [18, 25, 36][i % 3],
                    color: 16777215,
                }),
        );
    fs.writeSync(fd, "]");
    fs.closeSync(fd);
    assert(fs.statSync(fixture).size <= 52428800);
    const r = await fetch(
        base + "/Danmuku/ImportBatches/" + batch.batchId + "/Files/0",
        {
            method: "POST",
            headers: {
                Authorization: auth(credentials.admin.token),
                "Content-Type": "application/octet-stream",
                "Content-Length": String(fs.statSync(fixture).size),
            },
            body: fs.createReadStream(fixture),
            duplex: "half",
        },
    );
    assert.equal(r.status, 202);
    const task = normalize(await r.json());
    let done;
    for (let n = 0; n < 240; n++) {
        done = await api("/Danmuku/Imports/" + task.taskId);
        if (done.status === "Completed") break;
        if (["Failed", "Interrupted"].includes(done.status))
            throw Error(JSON.stringify(done));
        await sleep(1000);
    }
    assert.equal(done.status, "Completed");
    const updated = await api("/Danmuku/Media/" + item + "/Bindings");
    await api(
        "/Danmuku/Media/" + item + "/Bindings",
        {
            expectedVersion: updated.version,
            fileIds: updated.fileIds,
            activeFileId: done.fileId,
        },
        "PUT",
    );
    return {
        fileId: done.fileId,
        count,
        bytes: fs.statSync(fixture).size,
        sha256: await sha256File(fixture),
    };
}
async function login(page, who = credentials.admin) {
    await page.goto(base + "/web/#/login", { waitUntil: "domcontentloaded" });
    await page.locator("#txtManualName").waitFor({ timeout: 60000 });
    await page.locator("#txtManualName").fill(who.username);
    await page.locator("#txtManualPassword").fill(who.password);
    await page.locator("button[type=submit]").first().click();
    await page.waitForFunction(
        () =>
            window.ApiClient &&
            ApiClient.accessToken() &&
            ApiClient.getCurrentUserId(),
        { timeout: 30000 },
    );
    await page.waitForURL("**/#/home", { timeout: 30000 });
    await page.locator(".homePage").first().waitFor({ timeout: 30000 });
}
async function play(page, overlay = true) {
    if (new URL(page.url()).hash.startsWith("#/home")) {
        // Let native home scrollers finish loading and their attached RAF run
        // before detaching the page; rapid navigation otherwise races Jellyfin's
        // emby-scrollbuttons attached/detached callbacks.
        await page.locator(".homePage").first().waitFor();
        await page.waitForLoadState("networkidle");
        await page.evaluate(
            () =>
                new Promise((resolve) =>
                    requestAnimationFrame(() => requestAnimationFrame(resolve)),
                ),
        );
    }
    await page.evaluate((id) => {
        location.hash = "/details?id=" + id;
    }, item);
    const b = page
        .locator(
            'button.btnPlay:not(.hide), button[title="Play"]:not(.hide), button[title="Resume"]:not(.hide)',
        )
        .first();
    await b.waitFor({ state: "visible", timeout: 60000 });
    await b.click();
    await page
        .locator("video")
        .first()
        .waitFor({ state: "attached", timeout: 45000 });
    // The native play button owns loading and autoplay. A second play() here
    // races Jellyfin's source assignment and can abort an otherwise valid start.
    await page.waitForFunction(
        () => {
            const video = document.querySelector("video");
            return (
                video &&
                video.readyState >= 2 &&
                !video.paused &&
                video.currentTime > 0
            );
        },
        undefined,
        { timeout: 30000 },
    );
    if (overlay)
        await page.waitForFunction(
            () => window.DanmukuM1 && DanmukuM1.metrics.selected > 0,
            undefined,
            { timeout: 30000 },
        );
    await page.waitForFunction(
        () => document.querySelector("video").currentTime > 0,
        { timeout: 20000 },
    );
}
async function leave(page) {
    await page.evaluate(() => {
        const v = document.querySelector("video");
        if (v) v.pause();
        location.hash = "/home";
    });
    await page.waitForFunction(
        () =>
            DanmukuM1.metrics.canvases === 0 &&
            DanmukuM1.metrics.listeners === 0 &&
            DanmukuM1.metrics.frames === 0 &&
            DanmukuM1.metrics.selected === 0 &&
            DanmukuM1.metrics.requests === 0 &&
            DanmukuM1.metrics.observers === 0 &&
            DanmukuM1.metrics.timers === 1,
        { timeout: 15000 },
    );
}
async function control(page, label, value) {
    await page.locator("video").first().hover({ force: true });
    await page.mouse.move(120, 120);
    await page
        .locator('.danmuku-controls button[aria-label="弹幕设置"]')
        .click({ force: true });
    await page
        .locator('.danmuku-panel select[aria-label="' + label + '"]')
        .selectOption(String(value));
    await page
        .locator('.danmuku-controls button[aria-label="弹幕设置"]')
        .click({ force: true });
}
async function checkControlPlacement(page) {
    await page.locator("video").first().hover({ force: true });
    const placement = await page
        .locator(".danmuku-controls")
        .evaluate((root) => {
            const favorite = document.querySelector(
                ".osdControls .btnUserRating",
            );
            const toggle = root.querySelector('[aria-label="弹幕设置"]');
            const a = toggle.getBoundingClientRect();
            const b = favorite.getBoundingClientRect();
            return {
                inButtonRow: root.parentElement.classList.contains("buttons"),
                beforeFavorite: root.nextElementSibling === favorite,
                iconOnly:
                    toggle.textContent.trim() === "" &&
                    !!toggle.querySelector("svg"),
                iconWidth: toggle.querySelector("svg").getBoundingClientRect()
                    .width,
                favoriteVisible: b.width > 0 && b.height > 0,
                sameRow: Math.abs(a.y + a.height / 2 - b.y - b.height / 2) < 4,
                toLeft: a.right <= b.left + 1,
            };
        });
    assert(
        placement.inButtonRow && placement.beforeFavorite && placement.iconOnly,
        "icon must occupy the native button row immediately before favorite",
    );
    assert(
        placement.iconWidth >= 20 && placement.iconWidth <= 40,
        "icon must match native control scale",
    );
    if (placement.favoriteVisible)
        assert(
            placement.sameRow && placement.toLeft,
            "danmuku icon must align on the same row to the left of favorite",
        );
}
function p95(values) {
    assert.equal(values.length, 20);
    return [...values].sort((a, b) => a - b)[18];
}
(async () => {
    const result = {
        engine,
        playwright: require("playwright/package.json").version,
        fallback: "Browser plugin not available",
        environment: {
            locale: "en-US",
            headless: true,
            viewports: [
                { width: 1440, height: 900 },
                { width: 390, height: 844 },
            ],
            hardwareAcceleration:
                engine === "chromium"
                    ? "disabled by --disable-gpu"
                    : "browser default",
        },
        source: await seed(),
        checks: [],
        performance: [],
    };
    const browser = await { chromium, firefox }[engine].launch({
        headless: true,
        args:
            engine === "chromium"
                ? [
                      "--disable-dev-shm-usage",
                      "--disable-gpu",
                      "--js-flags=--expose-gc",
                      "--enable-precise-memory-info",
                  ]
                : [],
    });
    const context = await browser.newContext({
            locale: "en-US",
            viewport: { width: 1440, height: 900 },
        }),
        page = await context.newPage(),
        errors = [],
        network = [],
        consoleMessages = [];
    page.on("console", (m) => {
        if (["error", "warning"].includes(m.type()))
            consoleMessages.push({
                level: m.type(),
                text: redact(m.text()),
                location: m.location().url.split("?")[0],
            });
    });
    page.on("pageerror", (e) => {
        errors.push(redact(e.stack || e.message));
        console.log(
            "PAGEERROR",
            page.url().split("#")[1],
            redact(e.stack || e.message),
        );
    });
    page.on("response", (r) => {
        const u = new URL(r.url());
        if (/Danmuku|Sessions/.test(u.pathname))
            network.push({ path: u.pathname, status: r.status() });
    });
    try {
        result.browser = browser.version();
        await login(page);
        assert((await page.title()).length > 0);
        if (performanceRun)
            result.firstSelection = await page.evaluate(async (id) => {
                const start = performance.now();
                const data = await ApiClient.getJSON(
                    ApiClient.getUrl("Danmuku/Playback/" + id, {
                        playbackId: crypto.randomUUID(),
                    }),
                );
                return {
                    elapsed: performance.now() - start,
                    count: data.selectedCount,
                };
            }, item);
        // Delay native configuration reads to reproduce the CI edit/load race.
        let releaseSettings;
        const settingsGate = new Promise((resolve) => {
            releaseSettings = resolve;
        });
        const settingsRoute = /\/Plugins\/[^/]+\/Configuration(?:\?|$)/i;
        const holdSettings = async (route) => {
            if (route.request().method() === "GET") await settingsGate;
            await route.continue();
        };
        await page.route(settingsRoute, holdSettings);
        // Actual registered plugin configuration route, not a standalone mock page.
        await page.goto(base + "/web/#/configurationpage?name=Danmuku");
        await page.locator("#DanmukuConfigPage").waitFor({ timeout: 30000 });
        for (const section of ["media", "files", "tasks", "settings"]) {
            await page.locator('[data-tab="' + section + '"]').click();
            await page
                .locator('[data-section="' + section + '"]:visible')
                .waitFor();
        }
        assert(await page.locator("#dm-ShortLoadLimit").isDisabled());
        assert(
            await page
                .locator("#DanmukuConfigForm button[type=submit]")
                .isDisabled(),
        );
        releaseSettings();
        await page.waitForFunction(
            () => !document.querySelector("#dm-settings-fields").disabled,
        );
        await page.unroute(settingsRoute, holdSettings);
        await page.locator("#dm-ShortLoadLimit").fill("9000");
        await page.locator("#DanmukuConfigForm button[type=submit]").click();
        await page.waitForFunction(() =>
            document
                .querySelector("#DanmukuError")
                .textContent.includes("非递减"),
        );
        assert.equal(
            (await api("/Plugins/" + plugin + "/Configuration")).shortLoadLimit,
            5000,
        );
        await page.locator("#dm-defaults").click();
        assert.equal(
            await page.locator("#dm-ShortLoadLimit").inputValue(),
            "5000",
        );
        await page.locator("#DanmukuInstanceLabel").fill("M1 automated");
        await page.locator("#DanmukuConfigForm button[type=submit]").click();
        await page.waitForFunction(() =>
            document
                .querySelector("#DanmukuError")
                .textContent.includes("已保存"),
        );
        await page.screenshot({
            path: path.join(dir, engine + "-management.png"),
        });
        result.checks.push(
            "page identity, four sections, delayed settings load prevents editing until ready, invalid order rejected, real configuration save, meaningful content",
        );
        if (!performanceRun) {
            page.on("dialog", (dialog) => dialog.accept());
            await page.locator('[data-tab="media"]').click();
            await page.locator("#dm-search-media").click();
            await page
                .locator("#dm-media-list button")
                .filter({ hasText: "管理绑定" })
                .first()
                .click();
            await page
                .locator("#dm-binding button")
                .filter({ hasText: "停用本媒体弹幕" })
                .click();
            for (let n = 0; n < 30; n++) {
                if (
                    (await api("/Danmuku/Media/" + item + "/Bindings"))
                        .activeFileId === null
                )
                    break;
                await sleep(100);
            }
            assert.equal(
                (await api("/Danmuku/Media/" + item + "/Bindings"))
                    .activeFileId,
                null,
            );
            await page
                .locator("#dm-binding .dm-row")
                .filter({ hasText: "synthetic.json" })
                .getByRole("button", { name: "设为生效" })
                .click();
            await page.locator('[data-tab="files"]').click();
            const sourceRow = page
                .locator("#dm-files-list .dm-row")
                .filter({ hasText: "synthetic.json" })
                .first();
            await sourceRow
                .getByRole("button", { name: "详情", exact: true })
                .click();
            await page.waitForFunction(
                (hash) =>
                    document
                        .querySelector("#DanmukuError")
                        .textContent.includes(hash),
                result.source.sha256,
            );
            const downloadPromise = page.waitForEvent("download");
            await sourceRow.getByRole("button", { name: "下载原文件" }).click();
            const download = await downloadPromise;
            const target = path.join(dir, engine + "-download.json");
            await download.saveAs(target);
            assert.equal(await sha256File(target), result.source.sha256);
            await page.locator('[data-tab="media"]').click();
            const chooserPromise = page.waitForEvent("filechooser");
            await page
                .locator("#dm-binding button")
                .filter({ hasText: "上传导入" })
                .click();
            const chooser = await chooserPromise;
            await chooser.setFiles({
                name: "ui-errors.json",
                mimeType: "application/json",
                buffer: Buffer.from(
                    JSON.stringify([
                        { progress: 1000, content: "UI valid" },
                        ...Array.from({ length: 101 }, () => ({
                            content: "missing time",
                        })),
                    ]),
                ),
            });
            await page.waitForFunction(
                () =>
                    document
                        .querySelector("#dm-tasks-list")
                        .textContent.includes("AwaitingConfirmation"),
                { timeout: 60000 },
            );
            await page
                .locator("#dm-tasks-list button")
                .filter({ hasText: "异常明细" })
                .first()
                .click();
            await page
                .locator("#dm-errors button")
                .filter({ hasText: "下一页" })
                .click();
            await page.waitForFunction(() =>
                document
                    .querySelector("#dm-errors")
                    .textContent.includes("第 2 页"),
            );
            await page.locator("#dm-confirm-batch").click();
            await page.waitForFunction(
                () =>
                    document
                        .querySelector("#dm-tasks-list")
                        .textContent.includes("已提交"),
                { timeout: 60000 },
            );
            await page.locator('[data-tab="media"]').click();
            await page.locator("#dm-search-media").click();
            await page
                .locator("#dm-media-list button")
                .filter({ hasText: "管理绑定" })
                .first()
                .click();
            await page
                .locator("#dm-binding .dm-row")
                .filter({ hasText: "ui-errors.json" })
                .getByRole("button", { name: "解除绑定" })
                .click();
            await page.locator('[data-tab="files"]').click();
            await page.locator("#dm-unbound").check();
            await page.locator("#dm-search-files").click();
            await page
                .locator("#dm-files-list .dm-row")
                .filter({ hasText: "ui-errors.json" })
                .getByRole("button", { name: "删除文件本体" })
                .click();
            await page.waitForFunction(
                () =>
                    !document
                        .querySelector("#dm-files-list")
                        .textContent.includes("ui-errors.json"),
            );
            assert.equal(
                (await api("/Danmuku/Media/" + item + "/Bindings"))
                    .activeFileId,
                result.source.fileId,
            );
            result.checks.push(
                "UI bind/deactivate/reactivate, original download hash, upload, second-page batch confirmation, unbind and delete",
            );
        }
        await play(page);
        await page
            .locator("video")
            .first()
            .evaluate((v) => {
                v.currentTime = 30;
            });
        await page.waitForFunction(() => DanmukuM1.metrics.active > 0, {
            timeout: 20000,
        });
        if (!performanceRun) {
            await page.waitForFunction(() =>
                [1, 4, 5].every(
                    (mode) => DanmukuM1.metrics.renderedModes[mode] > 0,
                ),
            );
            const modes = await page.evaluate(
                () => DanmukuM1.metrics.renderedModes,
            );
            for (const mode of [1, 4, 5])
                assert(modes[mode] > 0, "mode " + mode + " must render");
            const requestsBeforeRemount = network.filter((r) =>
                r.path.includes("/Danmuku/Playback/"),
            ).length;
            await page.locator(".danmuku-controls").evaluate((e) => e.remove());
            await page
                .locator(".danmuku-controls")
                .waitFor({ state: "attached" });
            assert.equal(
                network.filter((r) => r.path.includes("/Danmuku/Playback/"))
                    .length,
                requestsBeforeRemount,
            );
            await checkControlPlacement(page);
            result.checks.push(
                "control remount retains the current collection without a new playback request",
            );
            await page
                .locator("video")
                .first()
                .evaluate((v) => v.pause());
            await page.waitForFunction(() => DanmukuM1.metrics.frames === 0);
            const paused = await page
                .locator(".danmuku-canvas")
                .evaluate((c) => c.toDataURL());
            await sleep(250);
            assert.equal(
                await page
                    .locator(".danmuku-canvas")
                    .evaluate((c) => c.toDataURL()),
                paused,
            );
            await page
                .locator("video")
                .first()
                .evaluate((v) => {
                    v.playbackRate = 2;
                    return v.play();
                });
            const beforeRate = await page
                .locator("video")
                .first()
                .evaluate((v) => v.currentTime);
            await sleep(600);
            assert(
                (await page
                    .locator("video")
                    .first()
                    .evaluate((v) => v.currentTime)) -
                    beforeRate >
                    0.7,
            );
            await page
                .locator("video")
                .first()
                .evaluate((v) => {
                    v.playbackRate = 1;
                    v.currentTime = 5;
                });
            await page.waitForFunction(() => DanmukuM1.metrics.active > 0);
            await page
                .locator("video")
                .first()
                .evaluate((v) =>
                    v.dispatchEvent(new Event("enterpictureinpicture")),
                );
            assert.equal(
                await page.evaluate(() => DanmukuM1.metrics.frames),
                0,
            );
            await page
                .locator("video")
                .first()
                .evaluate((v) =>
                    v.dispatchEvent(new Event("leavepictureinpicture")),
                );
            await page.waitForFunction(() => DanmukuM1.metrics.frames === 1);
            result.checks.push(
                "paused Canvas remains stable, 2x playback, backward seek rebuild, simulated PiP enter/leave adapter events",
            );
        }
        await control(page, "显示弹幕", "false");
        assert.equal(await page.evaluate(() => DanmukuM1.metrics.active), 0);
        await control(page, "显示弹幕", "true");
        for (const [label, value] of [
            ["密度", "high"],
            ["区域", 25],
            ["不透明度", 40],
            ["字号", 150],
        ])
            await control(page, label, value);
        await checkControlPlacement(page);
        await page.screenshot({ path: path.join(dir, engine + "-player.png") });
        await page.setViewportSize({ width: 390, height: 844 });
        await checkControlPlacement(page);
        result.checks.push(
            "icon-only control before favorite in the native button row, retained after remount, desktop and narrow viewport",
        );
        await page.screenshot({ path: path.join(dir, engine + "-narrow.png") });
        await page
            .locator('.danmuku-controls button[aria-label="弹幕设置"]')
            .click({ force: true });
        const panelBounds = await page.locator(".danmuku-panel").boundingBox();
        assert(
            panelBounds &&
                panelBounds.x >= 0 &&
                panelBounds.x + panelBounds.width <= 390 &&
                panelBounds.y >= 0 &&
                panelBounds.y + panelBounds.height <= 844,
            "settings panel must fit narrow viewport",
        );
        await page.screenshot({
            path: path.join(dir, engine + "-settings-narrow.png"),
        });
        await page
            .locator('.danmuku-controls button[aria-label="弹幕设置"]')
            .click({ force: true });
        await page.setViewportSize({ width: 1440, height: 900 });
        assert((await page.locator(".danmuku-canvas").count()) === 1);
        result.checks.push(
            "Canvas renders, enable toggle and preferences, desktop/narrow layout",
        );
        if (!performanceRun) {
            // Exiting must also cancel a stalled session lookup, not just the playback payload.
            await leave(page);
            let resolveLookup;
            const stalledLookup = new Promise((resolve) => {
                resolveLookup = resolve;
            });
            await page.route("**/Sessions", (route) => resolveLookup(route));
            await play(page, false);
            const lookupRoute = await stalledLookup;
            await page.evaluate(() => {
                document.querySelector("video").pause();
                location.hash = "/home";
            });
            await page.waitForFunction(
                () => DanmukuM1.metrics.requests === 0,
                undefined,
                { timeout: 5000 },
            );
            await lookupRoute
                .fulfill({
                    status: 200,
                    contentType: "application/json",
                    body: "[]",
                })
                .catch(() => {});
            await page.unroute("**/Sessions");
            assert.equal(
                await page.evaluate(() => DanmukuM1.metrics.canvases),
                0,
            );
            result.checks.push(
                "exit aborts stalled session lookup and ignores its late response",
            );
            // Force one automatic failure and repeated manual retries; verify feedback stays on the player.
            await leave(page);
            let delayedRoute;
            const delayed = new Promise((resolve) => {
                delayedRoute = resolve;
            });
            await page.route("**/Danmuku/Playback/**", (route) =>
                delayedRoute(route),
            );
            await play(page, false);
            const oldRoute = await delayed;
            await leave(page);
            await page.unroute("**/Danmuku/Playback/**");
            await play(page);
            await oldRoute
                .fulfill({
                    status: 200,
                    contentType: "application/json",
                    body: '{"status":"Ready","items":[]}',
                })
                .catch(() => {});
            await sleep(250);
            assert((await page.evaluate(() => DanmukuM1.metrics.selected)) > 0);
            result.checks.push(
                "aborted late playback response cannot overwrite a new player",
            );
            await leave(page);
            let fail = true,
                failureStatus = 503;
            await page.route("**/Danmuku/Playback/**", (route) =>
                fail
                    ? route.fulfill({
                          status: failureStatus,
                          body: '{"code":"PlaybackCapacity"}',
                      })
                    : route.continue(),
            );
            await page.evaluate(
                (id) => (location.hash = "/details?id=" + id),
                item,
            );
            await page.locator("button.btnPlay:not(.hide)").first().click();
            await page
                .locator(".danmuku-controls button")
                .filter({ hasText: "重试弹幕" })
                .waitFor({ timeout: 30000 });
            await page
                .locator(".danmuku-controls button")
                .filter({ hasText: "重试弹幕" })
                .click({ force: true });
            await page.waitForFunction(() =>
                document
                    .querySelector(".danmuku-message")
                    .textContent.includes("失败"),
            );
            await page.waitForFunction(() => DanmukuM1.metrics.requests === 0);
            const automaticAttempts = network.filter(
                (r) =>
                    r.path.includes("/Danmuku/Playback/") && r.status === 503,
            ).length;
            await sleep(1100);
            assert.equal(
                network.filter(
                    (r) =>
                        r.path.includes("/Danmuku/Playback/") &&
                        r.status === 503,
                ).length,
                automaticAttempts,
                "must not automatically retry",
            );
            failureStatus = 410;
            await page
                .locator(".danmuku-controls button")
                .filter({ hasText: "重试弹幕" })
                .click({ force: true });
            await page.waitForFunction(() =>
                document
                    .querySelector(".danmuku-message")
                    .textContent.includes("过期"),
            );
            await page.waitForFunction(() => DanmukuM1.metrics.requests === 0);
            fail = false;
            await page
                .locator(".danmuku-controls button")
                .filter({ hasText: "重试弹幕" })
                .click({ force: true });
            await page.waitForFunction(() => DanmukuM1.metrics.selected > 0);
            await page.unroute("**/Danmuku/Playback/**");
            result.checks.push(
                "automatic failure without repeated requests, manual retry, expiry feedback and success",
            );
        }
        await leave(page);
        async function cleanupCycles() {
            const samples = [];
            for (let i = 0; i < 20; i++) {
                await play(page);
                await leave(page);
                const sample = await page.evaluate(() => {
                    if (window.gc) gc();
                    return {
                        ...DanmukuM1.metrics,
                        heap: performance.memory
                            ? performance.memory.usedJSHeapSize
                            : null,
                    };
                });
                const processes = execFileSync(
                    "ps",
                    ["-eo", "pid=,ppid=,rss=,comm="],
                    { encoding: "utf8" },
                )
                    .trim()
                    .split("\n")
                    .map((line) => {
                        const [p, pp, rss, comm] = line.trim().split(/\s+/);
                        return { pid: +p, parent: +pp, rss: +rss, comm };
                    });
                const owned = new Set([process.pid]);
                let changed = true;
                while (changed) {
                    changed = false;
                    for (const p of processes)
                        if (owned.has(p.parent) && !owned.has(p.pid)) {
                            owned.add(p.pid);
                            changed = true;
                        }
                }
                sample.browserProcessRssKiB = processes
                    .filter(
                        (p) =>
                            p.pid !== process.pid &&
                            owned.has(p.pid) &&
                            p.comm !== "ps",
                    )
                    .reduce((total, p) => total + p.rss, 0);
                samples.push(sample);
                if ((i + 1) % 5 === 0)
                    console.log(engine + " cleanup " + (i + 1) + "/20");
            }
            return samples;
        }
        if (!performanceRun) {
            result.cleanup = await cleanupCycles();
            result.checks.push(
                "20 SPA enter/exit cycles return Canvas/listener/frame/request/collection counters to baseline",
            );
        }
        if (!performanceRun) {
            await play(page);
            await leave(page);
            await page.evaluate(() => ApiClient.logout());
            await page.waitForFunction(
                () =>
                    DanmukuM1.metrics.canvases === 0 &&
                    DanmukuM1.metrics.selected === 0,
            );
            await login(page, credentials.viewer);
            await play(page);
            await page
                .locator('.danmuku-controls button[aria-label="弹幕设置"]')
                .click({ force: true });
            assert.equal(
                await page
                    .locator('.danmuku-panel select[aria-label="字号"]')
                    .inputValue(),
                "100",
            );
            assert.equal(
                await page
                    .locator('.danmuku-panel select[aria-label="密度"]')
                    .inputValue(),
                "medium",
            );
            await leave(page);
            const config = await api("/Plugins/" + plugin + "/Configuration"),
                body = {};
            for (const [k, v] of Object.entries(config))
                body[k[0].toUpperCase() + k.slice(1)] = v;
            body.EnableWebSupport = body.WebEnabled = false;
            await api("/Plugins/" + plugin + "/Configuration", body, "POST");
            // The contract requires a fresh document. Navigating to the same
            // hash URL can remain a same-document navigation in Firefox.
            await page.reload();
            await page.locator(".homePage").first().waitFor();
            await play(page, false);
            assert.equal(await page.locator(".danmuku-controls").count(), 0);
            await page
                .locator("video")
                .first()
                .evaluate((v) => v.pause());
            await page.evaluate(() => (location.hash = "/home"));
            await page.locator(".homePage").first().waitFor();
            body.EnableWebSupport = body.WebEnabled = true;
            await api("/Plugins/" + plugin + "/Configuration", body, "POST");
            await page.route("**/Danmuku/Web/Danmuku.js*", (route) =>
                route.fulfill({ status: 404, body: "" }),
            );
            // The contract requires a fresh document. Navigating to the same
            // hash URL can remain a same-document navigation in Firefox.
            await page.reload();
            await page.locator(".homePage").first().waitFor();
            await play(page, false);
            assert.equal(await page.locator(".danmuku-canvas").count(), 0);
            await page
                .locator("video")
                .first()
                .evaluate((v) => v.pause());
            await page.evaluate(() => (location.hash = "/home"));
            await page.locator(".homePage").first().waitFor();
            await page.unroute("**/Danmuku/Web/Danmuku.js*");
            // The contract requires a fresh document. Navigating to the same
            // hash URL can remain a same-document navigation in Firefox.
            await page.reload();
            await page.locator(".homePage").first().waitFor();
            await play(page);
            await leave(page);
            result.checks.push(
                "resources remain released after logout, ordinary user playback, preferences isolated in same browser, disabled and missing-script states preserve video playback, re-enable works",
            );
        }
        if (performanceRun) {
            for (const maximum of [false, true])
                for (const viewport of [
                    { width: 1440, height: 900 },
                    { width: 390, height: 844 },
                ]) {
                    const record = {
                        maximum,
                        viewport,
                        load: [],
                        settings: [],
                        seek: [],
                        frames: [],
                    };
                    const cfg = await api(
                        "/Plugins/" + plugin + "/Configuration",
                    ); // write PascalCase to Jellyfin's native configuration API
                    const body = {};
                    for (const [k, v] of Object.entries(cfg))
                        body[k[0].toUpperCase() + k.slice(1)] = v;
                    body.LongLoadLimit = maximum ? 20000 : 10000;
                    body.MediumDensity = maximum ? 100 : 30;
                    body.HighDensity = maximum ? 100 : 50;
                    await api(
                        "/Plugins/" + plugin + "/Configuration",
                        body,
                        "POST",
                    );
                    await page.setViewportSize(viewport);
                    for (let i = 0; i < 21; i++) {
                        const measured = await page.evaluate(async (id) => {
                            const start = performance.now();
                            const data = await ApiClient.getJSON(
                                ApiClient.getUrl("Danmuku/Playback/" + id, {
                                    playbackId: crypto.randomUUID(),
                                }),
                            );
                            return {
                                elapsed: performance.now() - start,
                                count: data.selectedCount,
                            };
                        }, item);
                        assert.equal(measured.count, maximum ? 20000 : 10000);
                        if (i === 0) record.firstMeasured = measured.elapsed;
                        else record.load.push(measured.elapsed);
                    }
                    record.loadP95 = p95(record.load);
                    assert(
                        record.loadP95 <= 2000,
                        "load P95 " + record.loadP95,
                    );
                    await play(page);
                    await control(page, "密度", "medium");
                    await control(page, "区域", 100);
                    await control(page, "字号", 100);
                    await control(page, "不透明度", 75);
                    for (let i = 0; i < 20; i++) {
                        record.settings.push(
                            await page.evaluate(async (i) => {
                                const s = document.querySelector(
                                    '.danmuku-panel select[aria-label="区域"]',
                                );
                                const start = performance.now();
                                s.value = i % 2 ? "100" : "75";
                                s.dispatchEvent(new Event("change"));
                                await new Promise(requestAnimationFrame);
                                return performance.now() - start;
                            }, i),
                        );
                        record.seek.push(
                            await page.evaluate(async (i) => {
                                const v = document.querySelector("video");
                                v.pause();
                                return await new Promise((resolve) => {
                                    v.addEventListener(
                                        "seeked",
                                        () => {
                                            const start =
                                                performance.now() -
                                                (DanmukuM1.metrics
                                                    .lastRebuildMs || 0);
                                            requestAnimationFrame(() =>
                                                resolve(
                                                    performance.now() - start,
                                                ),
                                            );
                                        },
                                        { once: true },
                                    );
                                    v.currentTime = 20 + i * 3;
                                });
                            }, i),
                        );
                    }
                    record.settingsP95 = p95(record.settings);
                    record.seekP95 = p95(record.seek);
                    assert(
                        record.settingsP95 <= 200 && record.seekP95 <= 200,
                        "response latency",
                    );
                    await leave(page);
                    record.cleanup = preflight ? [] : await cleanupCycles();
                    await play(page);
                    for (const enabled of preflight ? [] : [false, true]) {
                        await control(page, "显示弹幕", String(enabled));
                        await page
                            .locator("video")
                            .first()
                            .evaluate(async (v) => {
                                v.currentTime = 120;
                                await new Promise((r) =>
                                    v.addEventListener("seeked", r, {
                                        once: true,
                                    }),
                                );
                                await v.play();
                            });
                        const before = await page.evaluate(() => {
                            DanmukuM1.metrics.peakActive = 0;
                            const v = document.querySelector("video"),
                                q = v.getVideoPlaybackQuality();
                            return {
                                time: v.currentTime,
                                total: q.totalVideoFrames,
                                dropped: q.droppedVideoFrames,
                                rendered: DanmukuM1.metrics.rendered,
                            };
                        });
                        const progress = [];
                        for (let minute = 0; minute < 10; minute++) {
                            await sleep(60000);
                            progress.push(
                                await page.evaluate(() => {
                                    const v = document.querySelector("video");
                                    return {
                                        time: v.currentTime,
                                        paused: v.paused,
                                        active: DanmukuM1.metrics.active,
                                        rendered: DanmukuM1.metrics.rendered,
                                    };
                                }),
                            );
                            assert(
                                !progress[minute].paused &&
                                    progress[minute].time - before.time >=
                                        (minute + 1) * 58,
                                "playback stopped during measured segment",
                            );
                            console.log(
                                JSON.stringify({
                                    engine,
                                    maximum,
                                    viewport,
                                    enabled,
                                    minute: minute + 1,
                                }),
                            );
                        }
                        const after = await page.evaluate(() => {
                            const v = document.querySelector("video"),
                                q = v.getVideoPlaybackQuality();
                            return {
                                time: v.currentTime,
                                total: q.totalVideoFrames,
                                dropped: q.droppedVideoFrames,
                                rendered: DanmukuM1.metrics.rendered,
                                peakActive: DanmukuM1.metrics.peakActive,
                            };
                        });
                        assert(
                            after.time - before.time >= 590,
                            "not continuous playback",
                        );
                        assert(
                            after.total - before.total > 10000,
                            "insufficient decoded frames",
                        );
                        if (enabled)
                            assert(
                                after.rendered - before.rendered > 500,
                                "insufficient rendered comments",
                            );
                        record.frames.push({
                            enabled,
                            before,
                            after,
                            progress,
                            ratio:
                                (after.dropped - before.dropped) /
                                (after.total - before.total),
                        });
                    }
                    if (!preflight)
                        assert(
                            record.frames[1].ratio - record.frames[0].ratio <=
                                0.01,
                            "drop delta",
                        );
                    await leave(page);
                    result.performance.push(record);
                    fs.writeFileSync(
                        path.join(dir, "performance-progress.json"),
                        JSON.stringify(result, null, 2),
                    );
                }
        }
        result.consoleMessages = consoleMessages;
        result.nativeNavigationCancellations = errors.filter(
            (e) =>
                e.includes("CancelledError") &&
                e.includes("tanstack.query-core.bundle.js") &&
                !e.includes("/Danmuku/"),
        );
        assert.deepEqual(
            errors.filter(
                (e) => !result.nativeNavigationCancellations.includes(e),
            ),
            [],
            "unexpected browser page errors",
        );
        result.checks.push(
            "no Danmuku page exceptions or framework overlay; native TanStack navigation cancellations recorded separately",
        );
        fs.writeFileSync(
            path.join(
                dir,
                engine +
                    (preflight
                        ? "-preflight"
                        : performanceRun
                          ? "-performance"
                          : "-browser") +
                    ".json",
            ),
            JSON.stringify(result, null, 2),
        );
        console.log(
            "PASS",
            engine,
            preflight
                ? "preflight"
                : performanceRun
                  ? "performance"
                  : "browser",
        );
    } catch (e) {
        await page
            .screenshot({ path: path.join(dir, engine + "-failure.png") })
            .catch(() => {});
        fs.writeFileSync(
            path.join(dir, engine + "-failure.json"),
            JSON.stringify(
                {
                    url: page.url(),
                    title: await page.title(),
                    errors,
                    network,
                    diagnostics: await page.evaluate(() => ({
                        metrics: window.DanmukuM1 && DanmukuM1.metrics,
                        api: typeof window.ApiClient,
                        events: typeof window.Events,
                        user:
                            window.ApiClient &&
                            ApiClient.getCurrentUserId &&
                            ApiClient.getCurrentUserId(),
                        video: document.querySelector("video") && {
                            time: document.querySelector("video").currentTime,
                            paused: document.querySelector("video").paused,
                            classes: document.querySelector("video").className,
                        },
                        controls: !!document.querySelector(".osdControls"),
                        scripts: Array.from(document.scripts)
                            .map(
                                (s) => new URL(s.src || location.href).pathname,
                            )
                            .filter((p) => p.includes("Danmuku")),
                    })),
                    message: redact(e.stack),
                },
                null,
                2,
            ),
        );
        throw e;
    } finally {
        await browser.close();
    }
})().catch((e) => {
    console.error(redact(e.stack || e));
    process.exitCode = 1;
});
