'use strict';
const path = require('path');

/** Reads sidecar settings from the environment (never from argv, never from files). */
function loadConfig(env = process.env) {
    const port = Number(env.PORT || 5001);
    if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('PORT must be 1-65535');
    return {
        port,
        host: env.HOST || '127.0.0.1',
        sessionDir: path.resolve(env.SESSION_DIR || './sessions'),
        sharedToken: env.SHARED_TOKEN || '',
        chromiumPath: env.PUPPETEER_EXECUTABLE_PATH || undefined,
        noSandbox: env.CHROMIUM_NO_SANDBOX === '1',
    };
}

module.exports = { loadConfig };
