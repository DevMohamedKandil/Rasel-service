'use strict';
const crypto = require('crypto');
const express = require('express');

const TOKEN_HEADER = 'x-bridge-token';

function tokenOk(expected, supplied) {
    if (typeof supplied !== 'string') return false;
    const a = crypto.createHash('sha256').update(expected).digest();
    const b = crypto.createHash('sha256').update(supplied).digest();
    return crypto.timingSafeEqual(a, b);
}

/**
 * Builds the HTTP surface. state = { isReady(), qrHtml(), phone?() }, client = { getChats(), sendMessage(id, text, opts) }.
 * Free of whatsapp-web.js so the contract is testable without WhatsApp. Bodies are plain text, as the Rasel Bridge provider expects.
 */
function createApp({ client, state, sharedToken = '' }) {
    const app = express();
    app.disable('x-powered-by');
    app.use(express.json({ limit: '1mb' }));

    // Unauthenticated liveness for container healthchecks; reveals only process state.
    app.get('/healthz', (_req, res) => res.json({ ok: true, ready: state.isReady() }));

    app.use((req, res, next) => {
        if (!sharedToken || tokenOk(sharedToken, req.get(TOKEN_HEADER))) return next();
        return res.status(401).send('Unauthorized');
    });

    app.get('/Qr', (_req, res) => res.send(state.qrHtml()));

    // Linked WhatsApp number (digits, no +) for the admin UI; null until ready. Behind the shared token like everything but /healthz.
    // Never logged. Rasel masks it; only an authenticated Rasel endpoint ever returns it in full.
    app.get('/info', (_req, res) => {
        res.set('Cache-Control', 'no-store');
        const phone = state.isReady() && typeof state.phone === 'function' ? state.phone() : null;
        res.json({ phone: /^[0-9]{5,20}$/.test(phone || '') ? phone : null });
    });

    // Unlinks the number (whatsapp-web.js logout); the host re-initializes so a fresh QR appears. 409 when not linked.
    app.post('/logout', async (_req, res) => {
        if (!state.isReady()) return res.status(409).json({ error: 'not_connected' });
        try {
            await state.logout();
            return res.json({ ok: true });
        } catch (err) {
            console.log('Error logging out: ' + err.message);
            return res.status(500).json({ error: 'logout_failed' });
        }
    });

    app.get('/sendMessage', async (req, res) => {
        if (!state.isReady()) return res.status(503).send('WhatsApp client not ready yet');
        const { phone, message } = req.query;
        if (typeof phone !== 'string' || !/^\d{8,15}$/.test(phone)) return res.status(400).send('Invalid phone number');
        if (typeof message !== 'string' || message.trim() === '') return res.status(400).send('Message is required');
        try {
            await client.sendMessage(phone + '@c.us', message);
            return res.send('SendDone');
        } catch (err) {
            console.log('Error sending message: ' + err.message);
            return res.status(500).send('Error sending message');
        }
    });

    // Exact group name: none -> 404, several -> 409. The ready check runs before validation, which the Rasel
    // status probe relies on (empty group name: 503 = not ready, 400 = ready; nothing is ever sent).
    app.post('/doSendPersonalizedTextToGroup', async (req, res) => {
        if (!state.isReady()) return res.status(503).send('WhatsApp client not ready yet');
        const groupName = req.body && req.body.groupName;
        const message = req.body && req.body.message;
        if (!groupName || typeof groupName !== 'string') return res.status(400).send('Group name is required');
        if (!message || typeof message !== 'string' || message.trim() === '') return res.status(400).send('Message is required');
        let chats;
        try {
            chats = await client.getChats();
        } catch (err) {
            console.log('Error getting chats: ' + err.message);
            return res.status(500).send('Error getting chats');
        }
        const groups = chats.filter(c => c.isGroup && c.name === groupName);
        if (groups.length === 0) return res.status(404).send('Group Not found');
        if (groups.length > 1) return res.status(409).send('Group name is ambiguous');
        try {
            await client.sendMessage(groups[0].id._serialized, message, { linkPreview: false });
            return res.send('SendDone');
        } catch (err) {
            console.log('Error sending to group: ' + err.message);
            return res.status(500).send('Error sending message');
        }
    });

    return app;
}

module.exports = { createApp, TOKEN_HEADER };
