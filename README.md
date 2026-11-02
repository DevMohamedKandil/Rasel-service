# Rasel

A standalone, reusable WhatsApp messaging and OTP gateway. Other projects integrate with it over REST using an API key.

> **WhatsApp is FAKE in this prototype.** `FakeWhatsAppProvider` and `FakeSmsProvider` keep everything in memory: no real message is ever sent. A fake session shows a fake QR and becomes `Connected` the first time its QR is fetched.

## Run

```bash
dotnet run --project src/Rasel.Api      # http://localhost:5114, Swagger UI at /swagger
```

**Dev API key:** on first start a `dev` client is seeded. A random key is logged **once** (`Seeded dev client. API key ...`). To choose your own, set `Seed__DevApiKey` before the first run. The SQLite file (`rasel.db`, configurable via `Database__Path`) is created automatically; delete it to re-seed.

All settings live in `src/Rasel.Api/appsettings.json` and can be overridden with environment variables (`Security__OtpPepper`, `Security__TokenSecret`, `Queue__MaxDelayMs`, ...). The committed secrets are dev-only placeholders.

## Examples

```bash
KEY=rsl_...   # from the startup log
H="X-Api-Key: $KEY"; J="Content-Type: application/json"; B=http://localhost:5114

curl $B/health
curl -H "$H" -H "$J" -d '{"name":"main"}' $B/v1/sessions              # -> {id, name, status}
curl -H "$H" $B/v1/sessions/{id}/qr                                    # fake QR; session becomes Connected
curl -H "$H" $B/v1/sessions/{id}                                       # status
curl -H "$H" -X DELETE $B/v1/sessions/{id}

curl -H "$H" -H "$J" -d '{"phone":"+201001234567","text":"hello"}' $B/v1/messages
curl -H "$H" -H "$J" -d '{"groupName":"Rasel Demo Group","text":"hello team"}' $B/v1/messages

curl -H "$H" -H "$J" -d '{"phone":"+201001234567","purpose":"login","channel":"whatsapp"}' $B/v1/otp/request
# -> {challengeId, expiresInSeconds, resendAfterSeconds}   (the code is never returned)
curl -H "$H" -H "$J" -d '{"challengeId":"...","code":"123456"}' $B/v1/otp/verify
# -> {token, expiresInSeconds}   token = HS256 JWT (clientId, phone, purpose), 5 min
```

Groups are addressed by **exact name**; zero matches returns 404 and more than one returns 409. Errors are RFC 7807 ProblemDetails with a stable `code` field.

OTP rules: 6-digit CSPRNG code, only an HMAC hash is stored, valid 5 minutes, single use, locked after 5 wrong attempts (423), 60 s resend cooldown (429 + `Retry-After`; a new request supersedes the previous challenge), WhatsApp delivery falls back to SMS. Codes, full phone numbers and OTP text are never logged.

## Tests

```bash
dotnet test
```

Unit tests cover the domain and use cases; the API tests run the whole app in memory (WebApplicationFactory) and read OTP codes from the fake provider's captured messages.

## Architecture

```
            +-----------------------------+
 clients -> |  Rasel.Api  (REST, API key) |
            +--------------+--------------+
                           |
            +--------------v--------------+
            |  Rasel.Application          |  use cases + ports (interfaces)
            +--------------+--------------+
                           |
            +--------------v--------------+       +--------------------+
            |  Rasel.Domain               |       |  Rasel.Infrastructure |
            |  entities, value objects    | <-----+  EF Core/SQLite, fake |
            +-----------------------------+       |  providers, queue,    |
                                                   |  HMAC, webhooks       |
                                                   +--------------------+
```

Dependencies point inward: Api and Infrastructure reference Application; Application references Domain.

## Next steps

- Real WhatsApp provider adapter behind `IWhatsAppProvider` (whatsapp-web.js sidecar or WhatsApp Cloud API) and a real SMS adapter.
- Dockerfile and compose file; EF Core migrations instead of `EnsureCreated`.
- SDKs (NuGet / npm) wrapping the REST API and token validation.
- Webhook docs: `message.sent` / `message.failed` events are POSTed with an `X-Rasel-Signature: sha256=<hmac>` header (enable with `Webhooks__Enabled=true`); per-client URLs and secrets are still to do.
- Persistent queue and rate limiter (both are in-memory and single-instance today).
