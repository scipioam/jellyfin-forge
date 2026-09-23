'use strict';
const assert = require('assert/strict');
// Browser-only second media fixture exercises changing context while the real
// server supplies A and Files. No fake media is written to the database.
exports.check = async function ({page, item, api}) {
    const other = '11111111111111111111111111111111';
    const listPattern = /\/Danmuku\/Media\?/;
    const bindingPattern = new RegExp('/Danmuku/Media/' + other + '/Bindings');
    await page.route(listPattern, async route => {
        const response = await route.fetch(), data = await response.json();
        const key = 'Items' in data ? 'Items' : 'items';
        data[key].push({Id:other,Name:'Context B',Type:'Movie',CanBind:true});
        await route.fulfill({response,json:data});
    });
    await page.route(bindingPattern, route => route.fulfill({json:{MediaId:other,Version:0,FileIds:[],ActiveFileId:null,IsDeactivated:false,CheckStatus:'Exists'}}));
    await page.locator('[data-tab="media"]').click(); await page.locator('#dm-search-media').click();
    const selectA = async () => {
        await page.locator('#dm-media-list .dm-row').filter({hasNotText:'Context B'}).getByRole('button',{name:'管理绑定'}).first().click();
        await page.getByRole('button',{name:'添加已有文件',exact:true}).waitFor();
    };
    const selectB = async () => {
        await page.locator('#dm-media-list .dm-row').filter({hasText:'Context B'}).getByRole('button',{name:'管理绑定'}).click();
        await page.locator('#dm-binding h3').filter({hasText:'Context B'}).waitFor();
    };
    await selectA();
    let release, entered;
    const gate = new Promise(r=>release=r), started = new Promise(r=>entered=r);
    const filesPattern=/\/Danmuku\/Files\?/;
    await page.route(filesPattern,async route=>{ entered(); await gate; await route.continue(); });
    await page.getByRole('button',{name:'添加已有文件',exact:true}).click(); await started;
    await selectB(); release();
    await page.unroute(filesPattern);
    await page.waitForTimeout(100);
    assert.equal(await page.locator('#dm-picker-list').count(),0,'late A picker rendered in B');
    await selectA();
    const chooserPromise=page.waitForEvent('filechooser');
    await page.getByRole('button',{name:'上传并绑定（最多 10 个）',exact:true}).click(); const chooser=await chooserPromise;
    await selectB();
    let writes=0;
    const watch=request=>{if(request.method()==='POST' && /\/Danmuku\/ImportBatches$/.test(new URL(request.url()).pathname)) writes++;};
    page.on('request',watch);
    await chooser.setFiles({name:'must-not-import.json',mimeType:'application/json',buffer:Buffer.from('[{"progress":1,"content":"stale"}]')});
    await page.waitForFunction(()=>document.querySelector('#DanmukuError').textContent.includes('媒体或绑定状态已变化'));
    assert.equal(writes,0,'stale upload submitted');page.off('request',watch);
    await selectA();
    const original = await api('/Danmuku/Media/' + item + '/Bindings');
    let releaseUpdate, enteredUpdate;
    const updateGate = new Promise(r=>releaseUpdate=r), updating = new Promise(r=>enteredUpdate=r);
    const updatePattern = new RegExp('/Danmuku/Media/' + item + '/Bindings');
    await page.route(updatePattern, async route => {
        if (route.request().method() !== 'PUT') return route.continue();
        const response = await route.fetch(); enteredUpdate(); await updateGate; await route.fulfill({response});
    });
    await page.getByRole('button',{name:'停用本媒体弹幕',exact:true}).click(); await updating;
    await selectB(); releaseUpdate(); await page.waitForTimeout(100);
    assert.equal(await page.locator('#dm-binding h3').textContent(),'Context B','late A binding response replaced B');
    await page.unroute(updatePattern);
    const current = await api('/Danmuku/Media/' + item + '/Bindings');
    await api('/Danmuku/Media/' + item + '/Bindings', {expectedVersion:current.version,fileIds:original.fileIds,activeFileId:original.activeFileId}, 'PUT');
    await page.unroute(listPattern);await page.unroute(bindingPattern);
    await page.locator('#dm-search-media').click();await selectA();
};
