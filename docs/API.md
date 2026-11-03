# Rasel API Reference (v1)

Everything here was read from the source code or confirmed by running the API locally (Fake provider, port 5114, a throwaway local key). Where something was **read from code but not executed**, the text says **(code only)**. The machine-readable contract is [openapi.json](openapi.json); it is exported from the running Development API and a test (`OpenApiSyncTests`) fails if it drifts from the code (see [Keeping openapi.json in sync](#keeping-openapijson-in-sync)). Arabic summary tables: [API.ar.md](API.ar.md). Integration guide: [INTEGRATION.md](INTEGRATION.md).

## Table of contents

1. [Conventions](#1-conventions)
2. [Authentication](#2-authentication)
3. [Sessions](#3-sessions)
4. [Messages](#4-messages)
5. [OTP](#5-otp)
6. [Health](#6-health)
7. [Error codes](#7-error-codes)
8. [Limits, quotas, pacing](#8-limits-quotas-pacing)
9. [Webhooks](#9-webhooks)
10. [Versioning](#10-versioning)
11. [Keeping openapi.json in sync](#keeping-openapijson-in-sync)

## 1. Conventions

* Base URL: where you run Rasel, e.g. `http://localhost:5114` (Docker compose: `http://localhost:8080`).
* JSON in, JSON out. Property names are camelCase. Enums are returned as strings (`"Connected"`).
* Request body limit: `Api:MaxRequestBodyBytes`, default 65536 bytes.
* All `/v1` routes require `X-Api-Key`. `/health*` are anonymous.
* Errors are RFC 7807 `application/problem+json`: `{ "type", "title", "status", "detail", "code", "traceId" }`. `code` is the stable machine-readable value (equal to `title`); never parse `detail`.

Response headers you may see:

| Header | Where | Meaning |
|---|---|---|
| `X-Api-Version: v1` | Every answer produced inside `/v1` | Contract version. **Not** present on 401/429 authentication failures, nor on a malformed-body 400 (these are produced before the route runs; observed). |
| `Idempotent-Replayed: true` | `POST /v1/messages` | The answer is a replay of an earlier request with the same `Idempotency-Key`. |
| `Retry-After: <seconds>` | 429 and some 502 | How long to wait. See [error codes](#7-error-codes) for which ones set it. |
| `Location` | 201 (session), 202 (message) | URL of the created resource. |
| `WWW-Authenticate: ApiKey` | 401 | Authentication scheme. |

## 2. Authentication

Send `X-Api-Key: <key>` on every `/v1` request. The server stores only a SHA-256 hash of the key, compares in constant time, and rejects keys longer than `Api:MaxKeyLength` (128).

| Situation | Answer |
|---|---|
| Header missing, key unknown or inactive | `401 unauthorized` |
| More than `Api:FailedAuthPerMinute` (20) wrong keys per minute from one client IP | `429 auth_throttled` with `Retry-After: 60` **(code only)** |

```
$ curl -i http://127.0.0.1:5114/v1/sessions/00000000-0000-0000-0000-000000000000
HTTP/1.1 401 Unauthorized
Content-Type: application/problem+json
WWW-Authenticate: ApiKey

{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.2","title":"unauthorized","status":401,"detail":"A valid X-Api-Key header is required.","code":"unauthorized","traceId":"00-377c..."}
```

**Key management.** There is no endpoint to create, list or rotate keys. On the first start with an empty database one client named `dev` is seeded: with the key from `Seed__DevApiKey` if set (at least 16 characters outside Development), otherwise a random `rsl_...` key that is logged once in Development only. Any other key requires inserting a row in the `ApiClients` table (`Name`, `ApiKeyHash` = uppercase hex SHA-256 of the key, `DailyMessageLimit`, `Active`). That procedure is derived from the code and **was not executed**.

Browser access (CORS) is off unless `Cors:AllowedOrigins` lists explicit origins; allowed headers are `X-Api-Key`, `Content-Type`, `Idempotency-Key`; methods GET, POST, DELETE.

## 3. Sessions

A session is one WhatsApp number linked to the service.

Session `status` values: `Starting`, `NeedsQr`, `Connected`, `Failed`, `Stopped`.

### POST /v1/sessions

Create a session. Body:

| Field | Type | Required | Rules |
|---|---|---|---|
| `name` | string | yes | Not blank, max 100 characters (trimmed). Otherwise `400 invalid_name`. |

`201 Created`, `Location: /v1/sessions/{id}`:

```json
{"id":"8c5e9df5-b1d0-48b0-84fb-689e94623551","name":"doc","status":"NeedsQr"}
```

If the provider is down: the session is saved as `Failed` and the call answers `502 provider_unavailable`.

### GET /v1/sessions/{id}

Refreshes the status from the provider (unless the session is `Stopped`). `200` with the same shape as above. `404 session_not_found` for an unknown id or another client's session.

### GET /v1/sessions/{id}/qr

Returns the pairing data and refreshes the status.

```
$ curl -H "X-Api-Key: ..." http://127.0.0.1:5114/v1/sessions/2d88.../qr
{"sessionId":"2d88...","status":"Connected","qr":"fake-qr:2d882a1684044e6099e44bfbdee303ea"}
```

`qr` is `null` when none is available. With the **Fake** provider the value is the text `fake-qr:<id>` and **fetching it makes the session `Connected`**. With the Bridge provider it is an HTML `<img src="data:image/png;base64,...">` fragment (README); treat it as untrusted: extract only the `data:image/png` URL and never render the HTML (the admin console does exactly this). `409 session_stopped` when the session is stopped.

### Linked number (privacy)

When the session is `Connected`, `GET /v1/sessions/{id}` and `/qr` add `"linkedPhoneMasked":"+20100****880"` (first 5 and last 3 digits; `null` when unknown or not connected). The full number is never in those payloads.

### GET /v1/sessions/{id}/linked-number

Returns `{"sessionId":"...","phone":"+201000000880"}` with `Cache-Control: no-store`. `404 session_not_found` for an unknown id or another client's session; `409 session_not_connected` when no number is linked. The number is never logged or placed in OpenAPI examples. With the Bridge provider it comes from the sidecar's `GET /info` (token protected); the legacy bridge has no `/info`, so it is `null` there. The Fake provider returns `+201000000000`.

### DELETE /v1/sessions/{id}

Stops the session. `204 No Content` (confirmed). Afterwards the session is `Stopped`.

## 4. Messages

### POST /v1/messages

Queue a text message. The call only **queues**: it answers `202` immediately and a background worker sends.

Headers: `X-Api-Key` (required), `Idempotency-Key` (optional, strongly recommended; 1-128 printable ASCII characters 0x21-0x7E, otherwise `400 invalid_idempotency_key`).

| Field | Type | Required | Rules |
|---|---|---|---|
| `phone` | string | one of `phone` / `groupName` | International number. Spaces, `-`, `(`, `)`, `.` are ignored; a leading `00` becomes `+`; must start with `+`, then 8-15 digits, first digit not `0`. Else `400 invalid_phone`. |
| `groupName` | string | one of `phone` / `groupName` | Exact group name, max 128. Needs a provider that can list groups. `404 group_not_found`, `409 group_ambiguous`. |
| `text` | string | yes | Not blank, max 4096 characters. Else `400 invalid_text`. |
| `sessionId` | guid | no | Default: the client's first connected session. Unknown/other client's: `404 session_not_found`. Not connected: `409 session_not_connected`. |

Exactly one of `phone` / `groupName` (neither or both: `400 invalid_target`).

```
$ curl -H "X-Api-Key: ..." -H "Content-Type: application/json" -H "Idempotency-Key: doc-1" \
       -d '{"phone":"+201001234567","text":"hello"}' http://127.0.0.1:5114/v1/messages
HTTP/1.1 202 Accepted
Location: /v1/messages/22b0b59c-62b0-4d2f-9631-171d2ba9665b
X-Api-Version: v1

{"messageId":"22b0b59c-62b0-4d2f-9631-171d2ba9665b","status":"Queued","replayed":false}
```

**Idempotency** (confirmed by running):

| Request | Answer |
|---|---|
| Same key, same `sessionId`/`phone`/`groupName`/`text` | `202`, same `messageId`, `"replayed": true`, header `Idempotent-Replayed: true`. A replay is answered before any other check can fail. |
| Same key, different content | `409 idempotency_key_reused` |
| Two parallel requests with one key | The loser gets the winner's answer, or `409 idempotency_conflict` **(code only)** |

Order of checks (first failure wins): text, target, group name length, idempotency key, replay, session, client daily limit, phone format, per-recipient daily limit.

### GET /v1/messages/{id}

Delivery state only. It never returns the text, the destination or an OTP code.

```json
{"id":"de0cfdd4-...","status":"Sent","attempts":1,"createdAt":"2026-11-03T08:12:26.5468818+00:00","sentAt":"2026-11-03T08:12:28.048967+00:00","nextAttemptAt":null,"lastErrorCode":null}
```

| Field | Meaning |
|---|---|
| `status` | `Queued`, `Sent`, `Failed` |
| `attempts` | Send attempts so far |
| `nextAttemptAt` | Only while `Queued`; when the next attempt is due |
| `lastErrorCode` | `null`, `provider_transient_error` (still `Queued`, will retry) or `provider_error` (final `Failed`) |

`404 message_not_found` for an unknown id or another client's message. Observed in a local run: a queued message was `Sent` about 1.5 seconds after creation, with `attempts: 1`.

Retries: the worker retries transient failures with exponential backoff (`Outbox:BaseBackoffSeconds` 5, doubling, capped at `MaxBackoffSeconds` 900) up to `Outbox:MaxAttempts` (5), then the message becomes `Failed`.

## 5. OTP

### POST /v1/otp/request

Generates a one-time code and sends it to the user. **The code is never returned to the caller.**

| Field | Type | Required | Rules |
|---|---|---|---|
| `phone` | string | yes | Same rules as messages. `400 invalid_phone`. |
| `purpose` | string | yes | Max 64 characters, e.g. `login`. A code is bound to client + phone + purpose. `400 invalid_purpose`. |
| `channel` | string | no | `whatsapp` (default) or `sms` (case-insensitive). Else `400 invalid_channel`. With `whatsapp`, if no WhatsApp session is connected or sending fails, it falls back to the SMS provider. |
| `language` | string | no | `ar` selects the Arabic text variants; anything else uses English. |

`200 OK`:

```json
{"challengeId":"60490e5b-8e04-4050-bc46-ba9cb8643607","expiresInSeconds":300,"resendAfterSeconds":60}
```

Behavior (code, with the ones marked confirmed also run):

* A second request for the same client+phone+purpose within `resendAfterSeconds` (60) answers `429 otp_resend_too_soon` with `Retry-After` set to the remaining seconds (confirmed: `Retry-After: 59`).
* A new request supersedes the previous active challenge (the old code then fails with `otp_superseded`).
* More than 60 requests per minute per client, or 5 per hour per phone: `429 otp_rate_limited`, `Retry-After: 60`.
* Counts toward the client daily limit and the per-recipient daily limit.
* If the code cannot be delivered: `502 otp_delivery_failed` and the challenge is cancelled.
* OTP messages skip the long random pacing delay; they are only delayed when the session just sent something (README).

### POST /v1/otp/verify

| Field | Type | Required | Rules |
|---|---|---|---|
| `challengeId` | guid | yes | From the request answer. Unknown or another client's: `404 otp_not_found` (confirmed). |
| `code` | string | yes | Exactly 6 digits (`Otp:CodeLength`), else `400 invalid_code_format` (confirmed) without touching the challenge. |

Success `200`: `{"token":"<JWT>","expiresInSeconds":300}`. **The success path was not executed live** (the Fake provider keeps the code inside the process); it is covered by the project's unit tests.

The token is an HS256 JWT signed with `Security:TokenSecret`: claims `iss: "rasel"`, `aud: "rasel-otp-verified"`, `jti`, `clientId`, `phone` (E.164), `purpose`, `iat`, `exp` (5 minutes). Rasel has **no endpoint to validate a token**: your backend must verify the signature with the shared secret (or just trust the 200 answer of `/verify`, which is the usual choice).

Wrong code, confirmed sequence for one challenge with `Otp:MaxAttempts` 5:

| Try | Answer |
|---|---|
| 1 | `400 otp_invalid` "Incorrect code. 4 attempt(s) left." |
| 2-4 | `400 otp_invalid`, 3, 2, 1 attempt(s) left |
| 5 | `400 otp_invalid` "0 attempt(s) left." (the challenge is now locked) |
| 6 and later | `423 otp_locked` |

Other outcomes (code only): `410 otp_expired` (older than 300 s), `410 otp_superseded` (a newer code was issued), `409 otp_already_used`, `409 otp_busy` (retry in 1 s, set `Retry-After: 1`).

## 6. Health

Anonymous, no key.

| Path | Meaning |
|---|---|
| `GET /health`, `GET /health/live` | Process is up. `{"status":"ok","checks":[]}` |
| `GET /health/ready` | Database reachable and outbox draining. `503` when unhealthy; a degraded outbox stays `200`. |

```json
{"status":"ok","checks":[{"name":"database","status":"healthy","detail":"database reachable"},{"name":"outbox","status":"healthy","detail":"outbox draining"}]}
```

`status` is `ok`, `degraded` or `unhealthy`. Responses never contain exception details. These routes are not in `openapi.json` (excluded from the description).

## 7. Error codes

HTTP status and `code` for every error the code can produce. "Confirmed" means it was produced against the running API.

| HTTP | `code` | Meaning | What the caller should do |
|---|---|---|---|
| 400 | `invalid_request` | Malformed JSON/parameters, or body over the size limit (status then follows the underlying error) | Fix the request. Confirmed (malformed body). |
| 400 | `invalid_name` | Session name missing or over 100 | Fix. Confirmed |
| 400 | `invalid_text` | Text missing or over 4096 | Fix. Confirmed |
| 400 | `invalid_target` | Not exactly one of phone/groupName, or groupName over 128 | Fix. Confirmed |
| 400 | `invalid_phone` | Not an international number | Fix. Confirmed |
| 400 | `invalid_idempotency_key` | Key longer than 128 characters or containing non-printable / non-ASCII characters | Use a short ASCII key. |
| 400 | `invalid_purpose` | OTP purpose missing or over 64 | Fix. |
| 400 | `invalid_channel` | Channel not whatsapp/sms | Fix. Confirmed |
| 400 | `invalid_code_format` | Code is not 6 digits | Fix. Confirmed |
| 400 | `otp_invalid` | Wrong code; detail tells remaining attempts | Let the user retry. Confirmed |
| 401 | `unauthorized` | Missing/unknown/inactive key | Check the key. Confirmed |
| 404 | `session_not_found` | No such session (or no usable session when none given) | Create/link a session. Confirmed |
| 404 | `message_not_found` | No such message for this client | Check the id. Confirmed |
| 404 | `otp_not_found` | No such challenge for this client | Request a new code. Confirmed |
| 404 | `group_not_found` | No group with this exact name | Check the name. |
| 409 | `session_not_connected` | Session exists but is not Connected | Link it first. Confirmed |
| 409 | `session_stopped` | QR requested for a stopped session | Create a new session. Confirmed |
| 409 | `idempotency_key_reused` | Key used earlier with different content | Use a new key for new content. Confirmed |
| 409 | `idempotency_conflict` | Parallel request with the same key still in progress | Retry shortly with the same key. |
| 409 | `group_ambiguous` | More than one group with that name | Use a phone, or rename the group. |
| 409 | `otp_already_used` | Code was already consumed | Request a new code. |
| 409 | `otp_busy` | Challenge being updated concurrently | Retry after `Retry-After` (1 s). |
| 410 | `otp_expired` | Code older than 300 s | Request a new code. |
| 410 | `otp_superseded` | A newer code was issued | Use the newest code. |
| 423 | `otp_locked` | Too many wrong attempts | Stop; request a new code later. Confirmed |
| 429 | `otp_resend_too_soon` | A code was just sent | Wait `Retry-After` seconds. Confirmed |
| 429 | `otp_rate_limited` | Too many code requests (client or phone) | Back off; `Retry-After: 60`. |
| 429 | `daily_limit_reached` | Client daily quota used up (UTC day) | Wait until 00:00 UTC or raise the client limit. |
| 429 | `recipient_daily_limit` | Too many messages to this number today (default 5) | Wait until 00:00 UTC. Confirmed |
| 429 | `rate_limited` | More than `Api:RequestsPerMinute` (600) requests per key per minute | Wait `Retry-After: 60`. |
| 429 | `auth_throttled` | Too many wrong keys from this IP | Wait `Retry-After: 60`. |
| 502 | `otp_delivery_failed` | The code could not be delivered | Check the session, retry. |
| 502 | `provider_unavailable` | The WhatsApp provider failed; `Retry-After: 5` when transient | Retry later. |
| 500 | (no `code`) | Unexpected server error (generic handler) | Report with `traceId`. |

## 8. Limits, quotas, pacing

| Control | Default | Scope | Behavior |
|---|---|---|---|
| `Api:RequestsPerMinute` | 600 | per API key, all authenticated endpoints | `429 rate_limited` |
| `Api:FailedAuthPerMinute` | 20 | per client IP | `429 auth_throttled` before any key lookup |
| `Api:MaxRequestBodyBytes` | 65536 | request | rejected by the server |
| Client daily limit | 1000 for the seeded client (`Seed:DevDailyMessageLimit`) | per client, UTC day | Soft: concurrent requests may overshoot by the in-flight count. `429 daily_limit_reached`. Counts every stored message including OTP records. |
| `Pacing:DailyPerRecipient` | 5 | per client + recipient number, UTC day | `429 recipient_daily_limit` (the number is stored only as a keyed hash) |
| `Pacing:MinGapMs` / `MinDelayMs` / `MaxDelayMs` | 1500 / 1000 / 5000 | per session, bulk messages | Spacing between sends so traffic looks natural; configuration values read from `appsettings.json`. OTP is exempt from the long delay (README). |
| `Otp:*` | TTL 300 s, 5 attempts, 6 digits, 60 s resend, 5/hour/phone, 60/min/client | OTP | see section 5 |
| `Outbox:MaxAttempts` | 5 | per message | then `Failed` |

## 9. Webhooks

Optional, off by default. Enable with `Webhooks:Enabled=true`, `Webhooks:Url`, `Webhooks:Secret` (https outside Development). Rasel sends **one** POST when a message reaches its final state; intermediate retries do not fire it (code). Delivery is a single attempt with no retry: a non-2xx answer or network error is only logged. **The dispatcher was read from code; no live webhook was received** during this documentation work. Treat webhooks as a hint and keep polling `GET /v1/messages/{id}` as the source of truth.

Request:

```
POST <Webhooks:Url>
Content-Type: application/json
X-Rasel-Timestamp: 1767225600
X-Rasel-Signature: sha256=<hex>

{"event":"message.sent","clientId":"<guid>","timestamp":"2026-01-01T00:00:00+00:00","data":{"messageId":"<guid>","status":"Sent","error":null}}
```

`event` is `message.sent` or `message.failed`; for failures `data.error` carries the stored error text (provider output, not the message body).

**Signature**: `hex_lowercase( HMAC_SHA256( secret, timestamp + "." + rawBody ) )`, sent as `sha256=` + hex. `timestamp` is unix seconds, the same value as the `X-Rasel-Timestamp` header. Verify the raw bytes before parsing JSON, compare in constant time, and reject timestamps older than your tolerance (e.g. 5 minutes).

Verification sample: [examples/verify-webhook.ts](examples/verify-webhook.ts). Run result of its self-check (it signs a sample the same way the dispatcher does, then verifies):

```
valid   -> true
tampered-> false
stale   -> false
```

The HMAC construction was also cross-checked against .NET's `HMACSHA256` (the dispatcher's implementation): both produced `1b6ad1bc...94a3` for secret `k` and message `1700000000.{"a":1}`.

## 10. Versioning

URL-segment versioning (`/v1`). A breaking change ships as a new group (`/v2`) alongside the old one; every answer from inside a version carries `X-Api-Version`. Adding optional request fields or response properties is not considered breaking, so clients must ignore unknown properties. (From `ApiVersions.cs`; no `/v2` exists.)

## Keeping openapi.json in sync

`docs/openapi.json` is the OpenAPI 3 document served by the Development API at `/swagger/v1/swagger.json`.

* Check: `dotnet test tests/Rasel.Api.Tests --filter OpenApiSync` fails when the code and the file differ.
* Refresh after an intentional API change: `RASEL_UPDATE_OPENAPI=1 dotnet test tests/Rasel.Api.Tests --filter OpenApiSync`, review the diff, commit.

The OpenAPI document describes routes, parameters and bodies; the error codes, limits and webhook contract in this file come from the code and are not in it.
