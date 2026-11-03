# Architecture review (whole repository)

Scope: `src/`, `tests/`, `sidecar/`, `admin/`, `deploy/` at the commit that introduced this file. Findings are ordered by severity. Status: **Fixed** (with a test), **Open** (reason given), **Verified OK** (suspected, tested, not a defect).

## High

| # | Where | Finding | Status |
|---|---|---|---|
| H1 | `src/Rasel.Application/UseCases/MessageSender.cs:31` | Only `ProviderException` was caught. Any other exception from a provider (bug, `JsonException`, unmapped `HttpRequestException`) escaped `SendAsync`, aborted the whole batch in `OutboxWorker.ProcessBatchAsync`, and left the row leased without incrementing `Attempts`. After the lease (120 s) the same row was claimed first again (ordered by `NextAttemptAt`), so one poison message starved every message behind it forever and never reached `Failed`. | **Fixed**: unexpected exceptions (not cancellation) count as a transient attempt with the generic text `unexpected delivery error` and become `Failed` after `Outbox:MaxAttempts`. Tests: `ReviewFixTests.Unexpected_provider_exception_...`, `Cancellation_is_not_recorded_...` |

## Medium

| # | Where | Finding | Status |
|---|---|---|---|
| M1 | `src/Rasel.Api/Security/ApiKeyAuthenticationHandler.cs:39-40` | The failed-auth throttle is keyed by `RemoteIpAddress` and checked before the key is validated. Behind a reverse proxy every caller shares the proxy address, so 20 wrong keys per minute from anyone lock out every legitimate client (`429 auth_throttled`). Docs only warned about it. | **Fixed**: opt-in `Api:TrustForwardedHeaders` (single hop, `ForwardLimit=1`) makes the throttle per real client. Default stays off so a spoofed `X-Forwarded-For` cannot dodge the throttle. Tests: `ReviewFixApiTests.Forwarded_headers_...`, `With_TrustForwardedHeaders_...` |
| M2 | `src/Rasel.Application/UseCases/SendTextHandler.cs:28` | `groupName` had no length check. The column is 128 characters but SQLite does not enforce it, so up to the 64 KiB body limit was stored in `Target` and `Destination` and echoed into provider calls. | **Fixed**: `400 invalid_target` above 128. Test: `Overlong_group_name_is_rejected_...` |
| M3 | `src/Rasel.Infrastructure/Persistence/DatabaseInitializer.cs:42` | `Seed:DevApiKey` was accepted with any length in Production (`admin` would become the master key). The README promised 16+. | **Fixed**: startup fails below 16 characters outside Development. Test: `Short_seed_api_key_is_refused_outside_development` |
| M4 | `tests/Rasel.Api.Tests/RaselFactory.cs:24` | Test database path was hard-coded to `C:\DevSpace\claude-scratch\...`: the suite was not runnable on any other machine or CI. | **Fixed**: `Path.GetTempPath()` |
| M5 | `BridgeWhatsAppProvider.cs` (all members), `FindConnectedAsync` in `Repositories.cs:62` | The Bridge provider maps every Rasel session of every API client onto the one WhatsApp account of the sidecar. A second API client can create a session, see it `Connected` and send through the first client's number. Rasel's tenancy boundary does not extend to the sidecar. | **Open, by design**: one WhatsApp account = one Rasel deployment (or one client). Documented in `sidecar/README.md` terms; a per-client sidecar routing table is a feature, not a fix. |
| M6 | `Repositories.cs:91` (`ClaimDueAsync`), `MessageSender.cs:47` | Leases are 120 s but a batch is processed sequentially with pacing delays and up to 30 s provider timeouts. With more than one API instance, a lease can expire mid-batch, a second instance sends the same message, and the first instance's `TrySaveAsync` fails after the send already happened (duplicate). Single node (the documented topology) is safe. | **Open**: needs per-message lease renewal or a smaller batch for multi-node; out of scope while SQLite is the only store (ADR-2). |

## Low

| # | Where | Finding | Status |
|---|---|---|---|
| L1 | `src/Rasel.Domain/ValueObjects/PhoneNumber.cs:33` | `StartsWith("00")` was culture-sensitive. | Fixed (`StringComparison.Ordinal`) |
| L2 | `src/Rasel.Application/UseCases/SendPacer.cs:22` | `_sessions` dictionary never evicts stopped sessions. Bounded by the number of sessions; negligible. | Open |
| L3 | `src/Rasel.Application/UseCases/SessionUseCases.cs:68-69`, `BridgeWhatsAppProvider.cs:37` | One QR request costs 3 `GET /Qr` and 2 readiness probes (provider calls status inside `GetQrAsync`, the handler calls it again). Wasteful, not incorrect. | Open |
| L4 | `sidecar/src/app.js:57` | `getChats()` is called on every group send, O(chats) with a headless browser. Fine at OTP/alert volumes, slow for thousands of chats. A short TTL cache would fix it. | Open |
| L5 | `BridgeWhatsAppProvider.cs:55` | Phone sends use `GET /sendMessage?phone&message` (text and number in the URL). Verified that .NET HttpClient logging does not print the query (`LoggingLeakTests`), and the sidecar does not log URLs, but a proxy between API and sidecar could. A `POST` variant would remove the exposure. | Open (original bridge contract; sidecar could add POST) |
| L6 | `src/Rasel.Application/UseCases/OtpHandlers.cs:145` | Retry jitter uses `Random.Shared` instead of the injected `IRandomSource`; not deterministic in tests (harmless: timing only). | Open |
| L7 | `src/Rasel.Application/UseCases/SessionUseCases.cs:32` | `CreateSessionHandler` marks the session `Failed` only for `ProviderException`; another exception leaves it `Starting`. | Open |
| L8 | `src/Rasel.Api/appsettings.json:3` | `AllowedHosts: *`. Host-header filtering is left to the reverse proxy. | Open |
| L9 | `admin/` | Session list is client-side only (no list endpoint in the API); a reload loses it by design (key is in memory). No component-level tests for pages, only core logic. | Open |
| L10 | `deploy/install-sidecar.ps1` | The shared token appears in the scheduled task arguments (readable by local administrators only). | Open, documented |
| L11 | Webhooks | Fire-and-forget, 5 s timeout, no retry; webhook latency blocks the worker for that message. Documented in ARCHITECTURE. | Open |

## Verified OK (suspected, tested, not a defect)

* OTP code or phone number in HttpClient logs through the Bridge URL: `LoggingLeakTests` captures all log lines at Trace level for a phone send and finds neither.
* `ex is HttpRequestException or TaskCanceledException && !ct...` in `HttpWebhookDispatcher.cs:218` parses as `(A or B) && C`, as intended.
* Constant-time key comparison, one active OTP per (client, phone, purpose) enforced by a filtered unique index, the OTP code never stored or returned, `GET /v1/messages/{id}` client-scoped: covered by existing tests.

## Layering and SOLID

No violations found: Domain has no packages; handlers see ports only; the API never touches `DbContext`. Smells (not defects): `MessageSender` mixes delivery, failure policy and webhook dispatch (acceptable at this size); `RequestOtpHandler` takes 15 constructor dependencies, a sign it should be split into issue/deliver collaborators if it grows again.

## Test gaps still open

* No end-to-end test of the real sidecar process with a real WhatsApp page (needs a linked phone; not run here).
* Outbox behaviour under a lease expiry with two workers (M6) is not tested because it is not supported.
* Admin pages have no DOM-level tests.
