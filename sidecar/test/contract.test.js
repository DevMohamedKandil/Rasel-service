'use strict';
// Contract the Rasel Bridge provider depends on. Runs without WhatsApp: node --test test/
const test = require('node:test');
const assert = require('node:assert');
const { createApp } = require('../src/app');

function start({ ready = true, chats = [], token = '' } = {}) {
    const sent = [];
    const client = {
        getChats: async () => chats,
        sendMessage: async (id, text, opts) => { sent.push({ id, text, opts }); },
    };
    const app = createApp({ client, state: { isReady: () => ready, qrHtml: () => '<img src="x">' }, sharedToken: token });
    return new Promise(resolve => {
        const server = app.listen(0, '127.0.0.1', () => resolve({ base: `http://127.0.0.1:${server.address().port}`, sent, close: () => server.close() }));
    });
}
const post = (base, body, headers = {}) => fetch(base + '/doSendPersonalizedTextToGroup', { method: 'POST', headers: { 'content-type': 'application/json', ...headers }, body: JSON.stringify(body) });
const group = (name, id) => ({ isGroup: true, name, id: { _serialized: id } });

test('GET /Qr serves html', async () => {
    const s = await start({ ready: false });
    assert.match(await (await fetch(s.base + '/Qr')).text(), /<img/); s.close();
});
test('probe: empty group -> 503 when not ready, 400 when ready, nothing sent', async () => {
    let s = await start({ ready: false });
    assert.strictEqual((await post(s.base, { groupName: '', message: '' })).status, 503); s.close();
    s = await start({ ready: true });
    assert.strictEqual((await post(s.base, { groupName: '', message: '' })).status, 400);
    assert.strictEqual(s.sent.length, 0); s.close();
});
test('group send: 200 / 404 / 409', async () => {
    const s = await start({ chats: [group('A', '1@g.us'), group('B', '2@g.us'), group('B', '3@g.us')] });
    const ok = await post(s.base, { groupName: 'A', message: 'hi' });
    assert.strictEqual(ok.status, 200); assert.strictEqual(await ok.text(), 'SendDone');
    assert.strictEqual(s.sent[0].id, '1@g.us');
    assert.strictEqual((await post(s.base, { groupName: 'Z', message: 'hi' })).status, 404);
    assert.strictEqual((await post(s.base, { groupName: 'B', message: 'hi' })).status, 409);
    assert.strictEqual(s.sent.length, 1); s.close();
});
test('phone send validates and sends', async () => {
    const s = await start();
    assert.strictEqual((await fetch(s.base + '/sendMessage?phone=12&message=x')).status, 400);
    assert.strictEqual((await fetch(s.base + '/sendMessage?phone=201001234567&message=%20')).status, 400);
    assert.strictEqual((await fetch(s.base + '/sendMessage?phone=201001234567&message=hi')).status, 200);
    assert.strictEqual(s.sent[0].id, '201001234567@c.us'); s.close();
});
test('not ready -> 503 on phone send', async () => {
    const s = await start({ ready: false });
    assert.strictEqual((await fetch(s.base + '/sendMessage?phone=201001234567&message=hi')).status, 503); s.close();
});
test('shared token: required when configured, healthz stays open', async () => {
    const s = await start({ token: 'secret-token' });
    assert.strictEqual((await fetch(s.base + '/Qr')).status, 401);
    assert.strictEqual((await fetch(s.base + '/Qr', { headers: { 'X-Bridge-Token': 'wrong' } })).status, 401);
    assert.strictEqual((await fetch(s.base + '/Qr', { headers: { 'X-Bridge-Token': 'secret-token' } })).status, 200);
    assert.strictEqual((await fetch(s.base + '/healthz')).status, 200);
    assert.strictEqual((await post(s.base, { groupName: 'A', message: 'x' })).status, 401); s.close();
});
