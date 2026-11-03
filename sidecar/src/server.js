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

// WhatsApp Web navigates during a cold first load and whatsapp-web.js does not guard for it; retry initialize()
// for that one known race, crash on anything else so real bugs stay visible.
process.on('uncaughtException', (err) => {
    if (err && String(err.message).includes('Execution context was destroyed')) {
        console.log('Recovered from navigation race, retrying: ' + err.message);
        ready = false;
        setTimeout(() => client.initialize(), 2000);
    } else {
        console.log('Uncaught exception, exiting: ' + ((err && err.stack) || err));
        process.exit(1);
    }
});

client.on('qr', (qr) => qrCode.toDataURL(qr, (err, url) => { if (!err) qrHtml = `<img src="${url}">`; }));
client.on('ready', () => { ready = true; qrHtml = ''; console.log('Client is ready'); });
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
