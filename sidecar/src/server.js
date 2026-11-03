'use strict';
const qrCode = require('qrcode');
const { Client, LocalAuth } = require('whatsapp-web.js');
const { loadConfig } = require('./config');
const { createApp } = require('./app');

const config = loadConfig();
let ready = false;
let qrHtml = '';

const args = ['--disk-cache-size=52428800', '--media-cache-size=52428800'];
if (config.noSandbox) args.push('--no-sandbox', '--disable-setuid-sandbox');

const client = new Client({
    authStrategy: new LocalAuth({ dataPath: config.sessionDir }),
    puppeteer: { args, executablePath: config.chromiumPath },
});

// WhatsApp Web is still loading/navigating while whatsapp-web.js injects (cold start or restore of a saved session).
// Known transient races: "Execution context was destroyed" and "Cannot read properties of null (reading 'Socket')"
// (window.require('WAWebSocketModel') is null before WA Web finished loading: Client.js inject()). Destroy the browser and
// re-initialize with back-off, bounded so a real bug still exits and the supervisor restarts the process.
const TRANSIENT = /Execution context was destroyed|reading 'Socket'|Target closed|Protocol error/;
const MAX_RETRIES = 5;
let retries = 0;
function reinitialize(reason) {
    retries += 1;
    ready = false;
    const delay = Math.min(30000, 2000 * 2 ** (retries - 1));
    console.log(`Transient init error (${retries}/${MAX_RETRIES}), retrying in ${delay}ms: ${reason}`);
    setTimeout(() => client.destroy().catch(() => {}).finally(() => client.initialize()), delay);
}
process.on('uncaughtException', (err) => {
    const msg = String((err && err.message) || err);
    if (TRANSIENT.test(msg) && retries < MAX_RETRIES) {
        reinitialize(msg);
    } else {
        console.log('Uncaught exception, exiting: ' + ((err && err.stack) || err));
        process.exit(1);
    }
});

client.on('qr', (qr) => qrCode.toDataURL(qr, (err, url) => { if (!err) qrHtml = `<img src="${url}">`; }));
client.on('ready', () => { ready = true; retries = 0; qrHtml = ''; console.log('Client is ready'); });
client.on('auth_failure', (msg) => console.error('Authentication failure: ' + msg));
client.on('disconnected', (reason) => {
    console.log('Client disconnected: ' + reason);
    ready = false;
    client.destroy().catch(e => console.log('destroy failed: ' + e)).finally(() => client.initialize());
});

const app = createApp({ client, state: { isReady: () => ready, qrHtml: () => qrHtml }, sharedToken: config.sharedToken });
if (!config.sharedToken && config.host !== '127.0.0.1' && config.host !== 'localhost') {
    console.warn('WARNING: non-loopback HOST without SHARED_TOKEN; anyone who can reach it can send messages.');
}
client.initialize();
app.listen(config.port, config.host, () => console.log(`Rasel sidecar listening on ${config.host}:${config.port}`));
