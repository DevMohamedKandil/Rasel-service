# Rasel admin

Angular 21 (standalone components, signals, no UI library) admin console for the Rasel API.

```bash
npm install --legacy-peer-deps   # npm 10.9 hits an arborist bug on this tree without the flag
npm start                        # http://localhost:4200
npm run build                    # dist/admin
npm test                         # vitest (jsdom)
```

* Sign in with the API base URL and an API key. The key lives in an in-memory signal only (never localStorage/sessionStorage/cookies/URL); a reload signs out.
* Pages: Sessions (create, status, QR; the API has no list endpoint so only sessions created in the tab appear), Messages (lookup by id: status, attempts, timestamps, coarse last error with phone-like numbers masked; the API never returns text, destination or OTP code), Health (`/health/ready`).
* The API must allow the UI origin: `Cors__AllowedOrigins__0=http://localhost:4200` (explicit origins only, disabled by default).
* The QR HTML from the Bridge provider is never rendered; only a `data:image/png;base64` URL is extracted.
* Needs Node 20.19+/22.12+. Angular 22 (latest) requires Node 22.22+, so 21 is used here.
