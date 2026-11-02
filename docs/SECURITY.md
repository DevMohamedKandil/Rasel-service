# Security and OTP threat model

## Assets and trust

* **OTP code**: a 6-digit secret valid for 5 minutes, delivered to a phone, proving control of that phone to the calling project.
* **Verification token**: HS256 JWT returned after a successful verify; the calling project trusts it.
* **API key**: identifies a calling project (client). A leaked key lets an attacker send messages and request codes as that client, within its quotas.
* **Secrets**: `Security:OtpPepper`, `Security:TokenSecret`, webhook secret.
* **Pending message text and destination**: stored only while a message waits in the outbox.

Trust boundary: the calling project (holds an API key) is trusted to call Rasel but its *end users* are not; end users never talk to Rasel directly. The network between them is assumed to be TLS (terminated in front of Rasel).

## Threats and controls (OTP)

| Threat | Control | Evidence |
|---|---|---|
| Stolen database reveals codes | Only `HMAC-SHA256(pepper, challengeId:code)` is stored; the pepper is a separate secret (>= 32 chars, never in the DB). A 6-digit space is brute-forceable offline **if both the DB and the pepper leak**, but codes are dead after 5 minutes. | `HmacOtpHasher`, startup validator |
| Online guessing of a code | 5 attempts per challenge, then `Locked` (423). Parallel guesses cannot exceed it: attempts are applied under optimistic concurrency. A new challenge needs a new SMS/WhatsApp message and is capped at 5/hour per phone. Residual: 25 guesses/hour/phone out of 10^6. | `Parallel_wrong_guesses_can_never_exceed_the_attempt_limit`, `OtpOptions` |
| Replay of a used code | Consumed state under optimistic concurrency: exactly one of N parallel verifies wins, the rest get 409. A later replay gets 409. | `Parallel_verifies_with_the_correct_code_produce_exactly_one_token` |
| Race creating two live codes | Filtered unique index: one Active challenge per (client, phone, purpose); loser gets 429. | `Parallel_code_requests_for_one_phone_create_a_single_active_challenge` |
| Timing side channel on the compare | `CryptographicOperations.FixedTimeEquals` over equal-length hex digests; the hash is computed before the status is looked at, so finished and active challenges cost the same. | `HmacOtpHasher`, `VerifyOtpHandler` |
| Enumeration of phones / challenges | Challenge ids are random GUIDs scoped to the client; unknown ids return a generic 404; the code is never in any response or log. `otp_resend_too_soon` confirms to *that client* that it asked recently, nothing about other clients. Residual: a client can learn whether *it* has an open challenge for a phone. | `Logs_never_contain_the_code_or_the_full_phone_number` |
| Garbage input burning attempts | Code format (digits, configured length) is checked before the challenge is loaded: no attempt is consumed. | `Malformed_codes_are_rejected_without_burning_an_attempt` |
| SMS/WhatsApp pumping (cost abuse) | Per phone 5/hour, per client 60/minute, per-client daily message limit, per-key request quota, resend cooldown. | `OtpOptions`, `DailyLimitGuard`, `ClientRateLimitMiddleware` |
| Expired code accepted | Expiry compared with `TimeProvider` at verify; first late attempt marks the challenge `Expired` (410). | `Expired_code_is_rejected_with_410` |
| Superseded code still works | Requesting a new code supersedes the previous one (410 on the old one). | `OtpHandlerTests` |
| Forged verification token | HS256 with `Security:TokenSecret`; carries `iss`, `aud`, `jti`, `clientId`, `phone`, `purpose`, 5 minute expiry. **The consumer must verify signature, `aud`, `exp` and `purpose`, and de-duplicate by `jti` if it needs single use.** | `HmacTokenIssuer` |

## Threats and controls (platform)

| Threat | Control |
|---|---|
| API key guessing | Keys are high entropy; stored as SHA-256 (indexed lookup is safe for random keys), re-compared in constant time; wrong keys are counted per client IP and the IP is answered `429 auth_throttled` **before any lookup** once over the limit (20/min). Keys longer than 128 chars are rejected unhashed. |
| Forgotten authentication on a new route | Fallback policy requires authentication everywhere except `/health*`; a test checks every mapped endpoint. |
| One client starving the service | Per-key request quota (600/min), 64 KiB body limit. |
| Secrets in the repo | Only dev placeholders are committed, and they are refused outside Development. The seeded key is logged only in Development. |
| PII in logs and storage | Logs and delivery records use the masked number (`+20*******567`); provider errors are redacted of digit runs and truncated; OTP text is never stored (delivered inline). |
| Message content at rest | The outbox must keep body and destination while a message is pending (so it survives restarts); both are erased when it becomes `Sent` or `Failed`. Residual: during that window they are plaintext in the SQLite file. Mitigate with disk encryption/file permissions; field encryption (ASP.NET Data Protection) is a possible next step. |
| Webhook forgery/replay | `X-Rasel-Signature` is HMAC over `timestamp.body`; receivers check the timestamp window. Webhook URL must be https outside Development. |
| Information leakage in errors | ProblemDetails with stable codes; unexpected exceptions return a generic 500; health output has no exception text. |
| Swagger exposure | Enabled only in Development. |

## Out of scope / residual risks

* **Single node**: rate limiters are in-process; with several instances limits multiply and the SQLite file cannot be shared.
* **Behind a proxy** per-IP throttling sees the proxy address unless forwarded headers are configured (not done).
* **No API key rotation/revocation endpoint**: `ApiClient.Active` exists but is managed directly in the database.
* **Phone takeover / SIM swap** and **social engineering** are not addressed by an OTP gateway.
* **Webhooks** are not retried.
* **Real provider** hardening (session storage of the whatsapp-web.js sidecar, WhatsApp account bans) is outside this repository.
