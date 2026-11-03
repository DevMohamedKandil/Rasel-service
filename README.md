# Rasel

A standalone, reusable WhatsApp messaging and OTP gateway. Other projects integrate with it over REST using an API key.

> **WhatsApp is FAKE by default.** `WhatsApp:Provider=Fake` keeps everything in memory: no real message is ever sent. A fake session shows a fake QR and becomes `Connected` the first time its QR is fetched. Real providers: `Bridge` (adapter for the bundled `sidecar/` whatsapp-web.js bridge; proven with one real group send against the original bridge, see ARCHITECTURE.md) and `WebJs` (invented sidecar contract, stub-tested only).

## Architecture

```mermaid
flowchart LR
    Client[Calling project] -->|X-Api-Key| API
    Admin["admin/<br/>Angular UI, key in memory"] -->|X-Api-Key, CORS allow-list| API
    subgraph Rasel
        API["Rasel.Api<br/>auth, rate limits, ProblemDetails, health"] --> APP["Rasel.Application<br/>use cases + ports"]
        APP --> DOM["Rasel.Domain<br/>entities, state rules"]
        INF["Rasel.Infrastructure<br/>EF Core/SQLite, providers, worker"] -.implements ports.-> APP
        API -.composition root.-> INF
        INF --> DB[(SQLite<br/>migrations)]
        W[OutboxWorker] -->|claims due rows| DB
        W --> PROV{{IWhatsAppProvider}}
    end
    PROV -->|Fake default| FAKE[In-memory fake]
    PROV -->|Bridge, X-Bridge-Token| SIDECAR["sidecar/<br/>whatsapp-web.js + Chromium<br/>session volume"]
    PROV -->|WebJs, invented contract| WEBJS[stub-tested only]
    SIDECAR --> WA[(WhatsApp)]
    W -->|signed webhook| Client
    DEPLOY["deploy/<br/>Docker compose, Windows scripts"] -.runs.-> API
    DEPLOY -.runs.-> SIDECAR
```

Folders: `src/` API (.NET), `sidecar/` WhatsApp bridge (Node), `admin/` admin UI (Angular), `deploy/` Docker and Windows installers, `docs/`.

## Quickstart (5 commands)

```bash
cp deploy/.env.example deploy/.env                      # 1. fill SECURITY_*, SEED_API_KEY, BRIDGE_SHARED_TOKEN
docker compose --env-file deploy/.env -f deploy/docker-compose.yml up -d --build   # 2. API + sidecar
curl http://localhost:8080/health/ready                 # 3. healthy?
curl -H "X-Api-Key: $SEED_API_KEY" -H "Content-Type: application/json" -d '{"name":"main"}' http://localhost:8080/v1/sessions   # 4. create a session
cd admin && npm install --legacy-peer-deps && npm start # 5. admin UI on :4200 (set CORS_ORIGIN=http://localhost:4200)
```

Without Docker: `dotnet run --project src/Rasel.Api` (Fake provider). Windows services: [deploy/README.md](deploy/README.md). Sidecar: [sidecar/README.md](sidecar/README.md). Admin: [admin/README.md](admin/README.md). API reference: [docs/API.md](docs/API.md) ([العربية](docs/API.ar.md), [openapi.json](docs/openapi.json)). Integration guide: [docs/INTEGRATION.md](docs/INTEGRATION.md) ([العربية](docs/INTEGRATION.ar.md), [examples](docs/examples)). Illustrated user guide: [English](docs/USER-GUIDE.en.md) | [العربية](docs/USER-GUIDE.ar.md).

Dependency rule: `Domain <- Application <- Infrastructure`, `Api` composes both. Details and decisions: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). OTP threat model: [docs/SECURITY.md](docs/SECURITY.md). Audit of the original prototype: [docs/AUDIT.md](docs/AUDIT.md).

## Run

```bash
dotnet run --project src/Rasel.Api      # http://localhost:5114, Swagger UI at /swagger (Development only)
```

* On first start the schema is created by the committed EF Core migrations and a `dev` client is seeded. In **Development** a random API key is logged once; set `Seed__DevApiKey` to choose your own (the only way to get a known key outside Development).
* A database created by the old prototype (`EnsureCreated`, no migration history) is not upgradable: delete `rasel.db` once.
* Outside Development the app **refuses to start** while secrets are the committed `dev-only` placeholders or shorter than 32 characters.
* Production example:

```bash
ASPNETCORE_ENVIRONMENT=Production \
Security__OtpPepper="$(openssl rand -hex 32)" Security__TokenSecret="$(openssl rand -hex 32)" \
Seed__DevApiKey="$(openssl rand -hex 24)" Database__Path=/var/lib/rasel/rasel.db \
dotnet run --project src/Rasel.Api
```

Terminate TLS in front of Rasel (reverse proxy) and set `Api__TrustForwardedHeaders=true`; otherwise per-IP throttling sees the proxy address and 20 wrong keys per minute from anyone lock out all callers. Review findings: [docs/REVIEW.md](docs/REVIEW.md).

## Configuration

Every setting lives in `src/Rasel.Api/appsettings.json` and can be overridden with environment variables (`Section__Key`). All sections are validated at startup; an invalid value stops the host before it serves traffic.

| Key | Default | Meaning |
|---|---|---|
| `Database:Path` | `rasel.db` | SQLite file |
| `Database:MigrateOnStartup` | `true` | Apply migrations at startup; if `false`, startup fails when migrations are pending |
| `Security:OtpPepper` | dev placeholder | HMAC key for OTP hashes, >= 32 chars, never `dev-only` outside Development |
| `Security:TokenSecret` | dev placeholder | HS256 key for the verification token, same rules, must differ from the pepper |
| `Seed:DevApiKey` / `Seed:DevDailyMessageLimit` | empty / 1000 | Seeds the `dev` client when the database has no client |
| `Api:RequestsPerMinute` | 600 | Per API key quota (429 `rate_limited`) |
| `Api:FailedAuthPerMinute` | 20 | Wrong-key attempts per client IP before it is throttled (429 `auth_throttled`) |
| `Api:TrustForwardedHeaders` | false | Trust `X-Forwarded-For` from the single reverse proxy so the failed-auth throttle is per real client. Enable only when the proxy overwrites the header and the API port is not directly reachable |
| `Api:MaxKeyLength` | 128 | Longer keys are rejected without a lookup |
| `Api:MaxRequestBodyBytes` | 65536 | Kestrel body limit (413) |
| `Api:ShutdownTimeoutSeconds` | 30 | Graceful shutdown window |
| `Otp:TtlSeconds` | 300 | Code lifetime |
| `Otp:MaxAttempts` | 5 | Wrong guesses before the challenge locks |
| `Otp:CodeLength` | 6 | Digits (4-9) |
| `Otp:ResendCooldownSeconds` | 60 | Minimum gap between codes for one phone/purpose |
| `Otp:PerPhonePerHour` / `Otp:PerClientPerMinute` | 5 / 60 | Code request limits |
| `Outbox:PollIntervalMs` | 500 | Idle poll interval of the worker |
| `Pacing:MinDelayMs` / `Pacing:MaxDelayMs` | 1000 / 5000 | Random jitter before a send that follows another send on the same session (validated at startup) |
| `Pacing:MinGapMs` | 1500 | Minimum gap between two bulk messages on one session (OTP is exempt) |
| `Pacing:DailyPerRecipient` | 5 | Messages per client and recipient number per UTC day (`429 recipient_daily_limit`) |
| `Otp:AppName` / `Otp:TemplatesEn` / `Otp:TemplatesAr` | Rasel / built-in 6+6 | OTP text variants (`{app}`, `{code}`, `{minutes}`; app and code required); chosen at random, language from the request `language` (`ar`/`en`) |
| `Outbox:MaxAttempts` | 5 | Attempts before a transient failure becomes `Failed` |
| `Outbox:BaseBackoffSeconds` / `Outbox:MaxBackoffSeconds` | 5 / 900 | Exponential retry backoff |
| `Outbox:LeaseSeconds` / `Outbox:BatchSize` | 120 / 10 | Claim lease (crash recovery) and batch size |
| `WhatsApp:Provider` | `Fake` | `Fake`, `Bridge` or `WebJs` |
| `WhatsApp:Bridge:BaseUrl` | empty | Sidecar address, e.g. `http://127.0.0.1:5001` (required for `Bridge`; plain http only on loopback, or with a SharedToken) |
| `WhatsApp:Bridge:SharedToken` | empty | Sent as `X-Bridge-Token`; the sidecar's `SHARED_TOKEN`. 16+ chars, required for plain http to a non-loopback host |
| `Cors:AllowedOrigins` | `[]` | Explicit origins allowed to call the API from a browser (admin UI). Empty = CORS off; `*` is rejected |
| `WhatsApp:Bridge:AttemptTimeoutSeconds` / `ReadRetries` / `Breaker*` | 30 / 2 / as WebJs | Same resilience knobs as WebJs |
| `WhatsApp:WebJs:BaseUrl` / `ApiToken` | empty | Sidecar address and bearer token (required for `WebJs`) |
| `WhatsApp:WebJs:AttemptTimeoutSeconds` | 10 | Per-attempt HTTP timeout |
| `WhatsApp:WebJs:ReadRetries` | 2 | Retries for GET/DELETE only (sends are never retried at HTTP level) |
| `WhatsApp:WebJs:Breaker*` | 5 / 0.5 / 30 s / 30 s | Circuit breaker: min throughput, failure ratio, sampling, break duration |
| `Webhooks:Enabled` / `Url` / `Secret` | false / empty / dev placeholder | Signed `message.sent` / `message.failed` events (https outside Development) |

## API

All `/v1` endpoints need `X-Api-Key`. Errors are RFC 7807 `application/problem+json` with a stable `code` and a `traceId`. Versioned responses carry `X-Api-Version: v1`.

```bash
KEY=...   # your API key
H="X-Api-Key: $KEY"; J="Content-Type: application/json"; B=http://localhost:5114

curl $B/health/live                                                    # liveness
curl $B/health/ready                                                   # database + outbox (unhealthy check -> 503; DB-down path not exercised by tests)

curl -H "$H" -H "$J" -d '{"name":"main"}' $B/v1/sessions               # 201 {id, name, status}
curl -H "$H" $B/v1/sessions/{id}/qr                                    # fake QR; session becomes Connected
curl -H "$H" $B/v1/sessions/{id}                                       # status
curl -H "$H" -X DELETE $B/v1/sessions/{id}                             # 204

# send: 202 Queued; the outbox delivers it, retrying transient failures with backoff
curl -H "$H" -H "$J" -H "Idempotency-Key: order-42" \
     -d '{"phone":"+201001234567","text":"hello"}' $B/v1/messages
# same key + same body  -> 202 with the original messageId and header Idempotent-Replayed: true
# same key + other body -> 409 idempotency_key_reused
curl -H "$H" -H "$J" -d '{"groupName":"Rasel Demo Group","text":"hello team"}' $B/v1/messages

curl -H "$H" -H "$J" -d '{"phone":"+201001234567","purpose":"login","channel":"whatsapp"}' $B/v1/otp/request
# -> {challengeId, expiresInSeconds, resendAfterSeconds}   (the code is never returned)
curl -H "$H" -H "$J" -d '{"challengeId":"...","code":"123456"}' $B/v1/otp/verify
# -> {token, expiresInSeconds}   token = HS256 JWT (iss, aud, jti, clientId, phone, purpose), 5 min
```

| Status | `code` examples |
|---|---|
| 400 | `invalid_phone`, `invalid_text`, `invalid_target`, `invalid_code_format`, `otp_invalid`, `invalid_request` |
| 401 / 429 | `unauthorized`, `auth_throttled`, `rate_limited`, `otp_resend_too_soon`, `otp_rate_limited`, `daily_limit_reached`, `recipient_daily_limit` |
| 404 / 409 / 410 / 423 | `session_not_found`, `group_not_found`; `group_ambiguous`, `otp_already_used`, `idempotency_key_reused`; `otp_expired`, `otp_superseded`; `otp_locked` |
| 502 | `otp_delivery_failed`, `provider_unavailable` |

Groups are addressed by **exact name**: zero matches is 404, more than one is 409.

### Webhooks

`POST` with JSON `{event, clientId, timestamp, data}` and headers `X-Rasel-Timestamp` (unix seconds) and `X-Rasel-Signature: sha256=<hex>`, where the signature is `HMAC-SHA256(secret, "<timestamp>.<raw body>")`. Receivers should reject timestamps older than a few minutes. Events fire once per message, on the final outcome only. Webhook delivery is best effort (no retry yet).

### Message status`GET /v1/messages/{id}` returns `status`, `attempts`, `createdAt`, `sentAt`, `nextAttemptAt` and a coarse `lastErrorCode` (`provider_error` / `provider_transient_error`). It never returns the text, the destination or an OTP code, and answers 404 for another client's message.### Ban-risk notesPacing, the per-recipient cap and OTP text variation lower the chance of a WhatsApp ban; they do not remove it. Random long delays on OTP were rejected for UX: an OTP only gets the jitter, and only when its session sent something less than `MinGapMs` ago. For production OTP use the WhatsApp Cloud API, a dedicated OTP number, and a CAPTCHA before the request endpoint.
## Tests

```bash
dotnet build -warnaserror
dotnet test            # Domain, Application (in-memory), Infrastructure (real SQLite, concurrency), Api (in-process HTTP)
```

Adding a migration: `dotnet ef migrations add <Name> -p src/Rasel.Infrastructure -s src/Rasel.Infrastructure -o Persistence/Migrations`. A test fails if the model and the migrations drift apart.
