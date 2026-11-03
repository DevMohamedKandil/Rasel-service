# Rasel sidecar

Thin HTTP bridge around [whatsapp-web.js](https://github.com/pedroslopez/whatsapp-web.js) (one WhatsApp account per process). Rasel talks to it with `WhatsApp:Provider=Bridge`.

## Run

```bash
npm install
PORT=5001 SHARED_TOKEN=change-me npm start   # then GET /Qr and scan it in WhatsApp > Linked devices
npm test                                      # contract tests, no WhatsApp needed
```

Needs Node 18+ and a Chromium (downloaded by puppeteer on `npm install`, or set `PUPPETEER_EXECUTABLE_PATH`).

| Env | Default | Meaning |
|---|---|---|
| `PORT` | 5001 | Listen port |
| `HOST` | 127.0.0.1 | Listen address (a warning is logged if non-loopback without a token) |
| `SESSION_DIR` | `./sessions` | WhatsApp login data (LocalAuth). It IS the logged-in account: persist it, protect it, never commit it |
| `SHARED_TOKEN` | empty | When set, every endpoint except `/healthz` needs header `X-Bridge-Token`. Put the same value in Rasel `WhatsApp:Bridge:SharedToken` |
| `PUPPETEER_EXECUTABLE_PATH` | bundled | System Chromium |
| `CHROMIUM_NO_SANDBOX` | unset | `1` inside containers running as root |

## Endpoint contract (what the Rasel Bridge provider uses)

| Call | Answer |
|---|---|
| `GET /healthz` | `{"ok":true,"ready":bool}` (no token) |
| `GET /Qr` | HTML `<img src="data:...">`, empty once linked |
| `POST /doSendPersonalizedTextToGroup` `{groupName,message}` | `200 SendDone`; `503` not ready; `400` bad input; `404` no such group; `409` several groups with that name; `500` failure. Empty group name is the readiness probe: `503` not ready, `400` ready, nothing sent |
| `GET /sendMessage?phone=<digits>&message=...` | `200 SendDone`; `503`; `400`; `500` |

Bodies are plain text. The contract is pinned by `test/contract.test.js`. Differences from the original prototype: no file endpoints, no company text, no argv ports, constant-time token check, strict phone validation (8-15 digits).

`patches/` fixes whatsapp-web.js 1.34.7 against the July 2026 WhatsApp Web change (applied by `patch-package` on install).
