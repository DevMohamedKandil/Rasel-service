# Architecture

## Layers

| Project | Owns | May reference |
|---|---|---|
| `Rasel.Domain` | Entities with their state rules (`OtpChallenge`, `Message`, `Session`, `ApiClient`), `PhoneNumber`, enums. No packages. | nothing |
| `Rasel.Application` | Use-case handlers, ports (interfaces), option classes + validators, error type (`AppException`), `PiiRedactor`. Time via BCL `TimeProvider`. | Domain |
| `Rasel.Infrastructure` | EF Core/SQLite + migrations, repositories, providers (Fake, WebJs), outbox worker, webhook dispatcher, HMAC/CSPRNG services, health checks, infrastructure options. | Application |
| `Rasel.Api` | HTTP only: endpoints, API-key authentication, rate-limit middleware, ProblemDetails mapping, versioning, health endpoints, composition root. | Application, Infrastructure (composition) |

Rules that tests and review keep honest: the Domain has no package references; handlers never see EF types, `HttpContext` or configuration; the API never touches `DbContext` (migration and seeding run from Infrastructure as the first hosted service); anything time dependent takes `TimeProvider` so tests drive time deterministically.

## Request flows

**Send.** `POST /v1/messages` validates, replays an existing `Idempotency-Key` if there is one, checks session/limits, and inserts a `Message` row in state `Queued` holding the body and destination. It answers `202` immediately. The `OutboxWorker` claims due rows (lease), calls the provider, and `MessageSender` records the outcome: `Sent`, rescheduled with exponential backoff (transient failure), or `Failed` (permanent failure or attempts exhausted). Body and destination are erased when the row becomes terminal. A webhook fires once, on the final outcome.

**OTP.** `request` creates an `OtpChallenge` (HMAC hash only), delivers inline (WhatsApp, falling back to SMS) because the caller needs the delivery result, and records a body-less `Message`. `verify` loads the challenge, compares in constant time, applies the attempt through the domain rules and saves with an optimistic-concurrency check, retrying on conflict. See [SECURITY.md](SECURITY.md).

## WhatsApp provider contract (sidecar)

`WebJsWhatsAppProvider` expects a small HTTP service in front of whatsapp-web.js. All calls carry `Authorization: Bearer <WhatsApp:WebJs:ApiToken>`; sends also carry `Idempotency-Key: <messageId>` (the sidecar should de-duplicate on it).

| Call | Request | Success answer |
|---|---|---|
| Create session | `POST /sessions` `{"sessionId": "<guid>"}` | 2xx |
| Stop session | `DELETE /sessions/{id}` | 2xx |
| QR | `GET /sessions/{id}/qr` | `{"qr": "<data>" \| null}` |
| Status | `GET /sessions/{id}/status` | `{"status": "Starting\|NeedsQr\|Connected\|Failed\|Stopped"}` |
| Send to phone | `POST /sessions/{id}/messages` `{"to": "+E164", "text": "...", "clientMessageId": "<guid>"}` | 2xx |
| Send to group | `POST /sessions/{id}/group-messages` `{"groupName": "...", "text": "...", "clientMessageId": "<guid>"}` | 2xx |
| Find groups | `GET /sessions/{id}/groups?name=...` | `[{"id": "...", "name": "..."}]` (Rasel keeps exact-name matches only) |

Error mapping: network error, timeout, open circuit, `408`, `429`, `5xx` are **transient** (outbox retries); other `4xx` are **permanent**. An error body `{"error": "..."}` is redacted and truncated before it is stored. This contract was written from the provider interface; it has been exercised only against HTTP stubs.

## Decisions

### ADR-1 Clean layering, ports in Application
*Context:* the gateway must outlive its WhatsApp provider and its database. *Decision:* use cases depend on interfaces declared in Application; Infrastructure implements them. *Consequence:* handlers are tested in memory, the real provider is a swap in DI. Cost: more files than a single-project prototype.

### ADR-2 SQLite with EF Core migrations, WAL on
*Decision:* SQLite, schema owned by committed migrations, applied at startup (`Database:MigrateOnStartup`, can be turned off). WAL mode is enabled so API requests and the worker do not block each other. *Consequence:* single node. Moving to PostgreSQL means a provider swap plus regenerating migrations; the filtered unique indexes and the version tokens are portable concepts.

### ADR-3 Optimistic concurrency with a domain-managed `Version`
*Context:* verify and outbox claiming are read-modify-write races. SQLite has no `rowversion`. *Decision:* entities carry a `long Version` bumped by their own state-changing methods and mapped as a concurrency token; a stale writer gets `DbUpdateConcurrencyException`, repositories turn it into `false`, handlers reload and retry (verify) or give up (claim, resend). *Consequence:* no locks, no lost attempts, a code is consumed at most once. Terminal no-op attempts do not bump the version, so late duplicates do not fight.

### ADR-4 One Active OTP challenge per (client, phone, purpose), enforced by the database
A filtered unique index (`Status = 0`) makes "two parallel requests create two live codes" impossible instead of merely unlikely. The loser maps to `otp_resend_too_soon`.

### ADR-5 Durable outbox polled from the database
*Decision:* messages are rows; the worker claims due rows with a lease; retries use exponential backoff. No in-memory queue. *Why not a broker:* one node, one table, no extra moving part. *Consequence:* at-least-once delivery. The window where a send succeeds but the process dies before saving yields a duplicate on lease expiry; the stable `messageId` is passed to the provider for de-duplication, and the final outcome is saved with `CancellationToken.None` so a graceful shutdown does not cause it. Message text is stored only while pending (see SECURITY.md).

### ADR-6 Retries live in two places on purpose
HTTP-level resilience (timeout, circuit breaker, retry) retries **idempotent reads only**. A `POST` send that timed out may already be delivered, so only the outbox, which knows the message id and attempt count, decides to resend.

### ADR-7 Fail-closed authentication
Authentication is a real ASP.NET scheme plus a fallback authorization policy requiring an authenticated user; only `/health*` opts out. A new route is protected by default. A test enumerates all endpoints to enforce this.

### ADR-8 URL-segment API versioning
`/v1` is a route group created by `MapVersion`; every versioned response carries `X-Api-Version`. A breaking change adds `/v2` beside it. The `Asp.Versioning` package was not adopted: with one version it would only add configuration.

### ADR-9 `TimeProvider` instead of a custom clock
Standard abstraction, usable by `Task.Delay` and timers, replaceable in tests (`FakeClock`).

### ADR-10 Options validated at startup, secrets policy by environment
Every option class has an `IValidateOptions`; the host validates before starting any hosted service (so before the database is touched). Development may use the committed placeholders; any other environment refuses to start with them.

### ADR-11 Errors as ProblemDetails with stable codes
Handlers throw `AppException(kind, code, detail)`; one exception handler maps to RFC 7807 with `code` and `traceId`. Framework errors (404, 415, malformed JSON) go through the same writer. Unexpected exceptions become a generic 500 without detail.

## Known limits

* Single node: the in-process rate limiter and the SQLite file are not shared across instances.
* Daily message quota is a soft limit (count-then-insert), documented in `DailyLimitGuard`.
* Webhooks are fire-and-forget (logged on failure, not retried).
* Real whatsapp-web.js sidecar not built or run here.
