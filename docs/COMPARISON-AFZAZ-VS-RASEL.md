# Afzaz vs Rasel: WhatsApp handling

## ملخص بالعربية
أفزاز اليوم يشغّل جسر WhatsApp (Node + whatsapp-web.js) لكل شركة كعملية فرعية من الـ API نفسه، ويرسل إليه بـ HTTP بدون توثيق وبشكل متزامن، ونجاح الإرسال يعتمد على النص `SendDone` فقط. لا يوجد OTP ولا طابور دائم ولا إعادة محاولة، والتحكم في المعدل موجود فقط في شاشة الرسائل الفورية (دفعات عشوائية). راسل يوفر طابورًا دائمًا وإعادة محاولة وتوثيقًا بين الخدمات وOTP وتحديد معدل، لكنه يفتقد بعض ما يفعله أفزاز (الإرسال لمجموعات واتساب بالاسم، إرفاق ملفات PDF، دورة حياة الجسر لكل شركة). الخطة: ترحيل على مراحل مع إمكانية الرجوع، دون تغيير أفزاز الآن.

## Scope and method
Read-only review of Afzaz (backend `AfzazBackend/afzazbackend/src/Services/ERPBackEnd`, bridge `WhatsappService/WPService/server/index.js`, frontend `azazfront`). Paths are relative to those roots. Nothing in Afzaz was run or changed. Rasel facts come from this repo (`docs/ARCHITECTURE.md`, `sidecar/`).

## How Afzaz works today (evidence)
1. **Bridge lifecycle, per company.** `WhatsAppApiService.CreateCustomerWhatsAppWebServer` (ERPBackEnd.Application/WhatsApp/WhatsAppApiService.cs:195) allocates a config, probes `GET /Qr` for liveness (`IsBridgeAlive`, :328), copies `WhatsAppServiceTemplate` to `RuningWhatsAppService/<companyId>` (`InstallBridgeFiles`, :279; `CopyDirectory` copies files only, the subfolder copy is commented out) and starts `WhatsAppService.exe` via `cmd.exe /c`, output redirected to `bridge-stdout.log` (`StartBridgeProcess`, :342-357). Kill is `taskkill /T /F` on every `WhatsAppService` process whose path contains the company folder (:364-390). `RestartWhatsAppService` = kill, reinstall, start (:114-123). The process is a child of the API: no supervisor, no restart on crash, no health loop; liveness is checked only when someone clicks Activate.
2. **Per-company config.** Entity/table `WhatsAppWebConfig` (ServerIp, Port, CompanyId). `WhatsAppWebRepos.AddConfig` uses `WhatsApp:StartPort` or last port + 1, or asks the subscription service `GetNextFreeWhatsappPort` (WhatsApp/WhatsAppWebRepos.cs, around :20-60). One bridge process (one Chromium) per company; the Node bridge takes bind address/port from `process.argv[2]/[3]` (WPService index.js:314).
3. **Bridge API.** Plain `app.get` routes; no token or auth middleware found (the only "auth" in index.js is the `auth_failure` handler, :70): `/Qr` :65, `/send` :124, `/sendMessage` :152, `/doSendToGroup` :185, `/doSendTextToGroup` :328, `/doSendPersonalizedTextToGroup` (POST) :370, `/doSendMediaToGroup` :427. Phone and message travel in the query string, so they land in any proxy/access log. Success is the literal body `SendDone`; not ready is HTTP 503 (:154).
4. **Direct single send.** `SendWhatsapp.SendMessage` (WhatsApp/SendWhatsapp.cs:141-183): GET `/sendMessage?phone=..&message=..`, success only if body is `"SendDone"`, a new `HttpClient` per call, `.Result` blocking in `WhatsAppApiService.SendMessage` (:415-420). When `MiddlewareSettings:UseCustomConnectionDataBase` is true it picks a third-party provider (Twilio, Getgabs, Mottasl, Taqnyat) from `GeneralConfiguration` (SendWhatsapp.cs:39-88).
5. **Bills/statements/GPS reports.** `SendWhatsAppReport` (WhatsAppApiService.cs:1190-1335) renders a PDF into `WhatsAppGenPdfs` next to the exe and passes the LOCAL PATH to the bridge (the code comment says the bridge reads it with fs.readFileSync): API and bridge must share a disk. The text is a hard-coded AR+EN template with the customer name. Every failure collapses into the single key `whatsAppServiceNotConnected`. GPS: `SendGpsReport` (:965) and `SendAllUnsentGPSReportsAsync` with an `UnsentGPSReport` table (manual resend, WhatsAppController.cs:116).
6. **Instant customer message (bulk to WhatsApp groups).** `InstantCustomerMessageService` (Warehouses/InstantCustomerMessage/InstantCustomerMessageService.cs): in-memory static `ConcurrentDictionary<Guid,JobState>` (:94), background `RunAsync` (:232), batches of 2-6 customers, 1.5 s inside a batch and random 10-40 s between (:81-83, :253-264), up to 10 files of 25 MB (:89-90), per-customer Sent/Failed/SkippedNoGroup, polled by the screen via `GET api/InstantCustomerMessage/status` (controller :43). Per-customer results are real (derived from the bridge reply, `response.IsSuccessStatusCode && body == "SendDone"`), but the job lives only in memory (lost on restart/recycle, kept 6 h) and cannot be cancelled or resumed.
7. **Frontend.** `src/app/erp/notifictions/services/whats-app.service.ts` calls `api/WhatsApp/ConfigWhatsApp | existConfig | CancelWhatsAppService | RestartWhatsAppService | SendBillByWhatsApp...`, all as GET (lines 18-59); the QR comes from `ReadWhatsAppQr` (WhatsAppController.cs:170). Settings screens: `notifictions/settings/configurations/configurations.component.ts`, `notifications-configurations`, `notifications-management`. Bulk screen: `Warehouses/operations/instant-customer-message/instant-customer-message.component.ts`.
8. **OTP.** None for WhatsApp. A case-insensitive search of "otp" in ERPBackEnd.Application found only unrelated files (invitation links, vouchers, cheques, chart of accounts); no OTP issue/verify flow was seen.
9. **Deployment.** `WhatsAppServiceTemplate` ships inside the API output; the exe is a Node-packaged host; startup errors (npm, missing node on PATH) are visible only in `bridge-stdout.log` (comment at WhatsAppApiService.cs:344-349).

## Comparison table
| Area | Afzaz | Rasel |
|---|---|---|
| Bridge lifecycle | Child process of the API via `cmd.exe`, per company; kill by `taskkill /T`; no supervisor, no auto-restart | Separate sidecar under an external supervisor (Task/NSSM/Docker); sidecar exits on unknown errors, bounded retry for known init races (sidecar/src/server.js). In-API managed supervisor is requested but not built |
| Endpoints / contract | Bridge: GET routes with data in the query string, success = body `SendDone`; backend: ~10 GET actions under `api/WhatsApp` | Versioned REST `/v1/...`, ProblemDetails with stable codes, openapi.json synced by a test; sidecar contract tested |
| Session / company model | One bridge and one linked phone per company, ports in DB | One linked phone per sidecar; API clients with scopes; per-company line means one sidecar per company |
| Sync vs queued | Synchronous HTTP (`.Result`, 5 min timeout in bulk); bulk job in memory | Durable outbox polled from the DB (ADR-5); API returns immediately |
| Retry / failure | None for single sends; GPS "unsent" table + manual resend; bulk marks Failed and continues | Retries in two layers (ADR-6); poison-message wedge fixed in review |
| Results real? | Bulk: yes per customer. Single/report: boolean only; every error shown as "not connected" | Per-message status from provider replies; probe separates not-ready (503) from bad input |
| Rate limit / ban protection | Only the bulk screen; single sends unthrottled | Pacing, per-recipient daily limit, OTP attempt limits (ARCHITECTURE.md "Ban-risk protection") |
| Backend to bridge auth | None found | `X-Bridge-Token` (sidecar/src/app.js:27), loopback default, warning when exposed without token |
| OTP | Not present | Yes, one active challenge per (client, phone, purpose) by DB constraint (ADR-4) |
| Idempotency | None | Idempotency support on the send API (confirm in docs/API.md) |
| Logging / PII | Phone + full message in URLs; unbounded `bridge-stdout.log` | Structured logs (bodies-not-logged policy to confirm in docs/SECURITY.md) |
| Configuration | appsettings `WhatsApp:ServerIp/StartPort` + DB table + GeneralConfiguration | Options validated at startup, secrets policy per environment (ADR-10) |
| Deployment | Template copied from API output, per-company folder, needs node on server | Dockerfile + install scripts (deploy/), pinned lockfile |
| Per-customer isolation | Separate process/folder/port per company | Client key and data scope; separate sidecar only if deployed so |
| Tests | No automated tests for bridge/WhatsApp found (search not exhaustive) | Sidecar contract tests (6 pass), API tests, openapi sync test |
| Known bugs/risks | Unauthenticated bridge; PII in GET; new `HttpClient` per call; shared-disk PDF path; in-memory bulk jobs; `CopyDirectory` skips subfolders; node crash = silent outage until Activate; past bug: bridge read `phone` while caller sent `phoneNo` (comment, SendWhatsapp.cs:162) | Sidecar `Socket`-null init race (mitigated, see sidecar/README); one account per sidecar; no group-by-name or attachments confirmed in the Rasel API |

## What Rasel does better
Durable queue and retries; authenticated bridge calls; versioned, documented, tested API; OTP; throttling on all sends; idempotency; startup-validated config; no PII in URLs.

## What Afzaz does that Rasel lacks (must-port list)
1. Send to a WhatsApp group by name (`doSendTextToGroup`, `doSendMediaToGroup`) including 404/409 handling and text-first vs caption mode.
2. File attachments (up to 10 files, 25 MB; PDF bills, statements, GPS reports) without a shared local disk.
3. Per-company bridge lifecycle (activate, QR, restart, cancel, port allocation), or a decision to use one Rasel line per company.
4. Bulk job status per customer (Sent/Failed/SkippedNoGroup) for the UI and the unsent-GPS retry list.
5. Arabic/English statement/invoice templates and phone normalisation (`NormalizePhoneForWhatsApp`, SendWhatsapp.cs, around :95-138).
6. Third-party providers (Twilio/Getgabs/Mottasl/Taqnyat): keep them in Afzaz behind `IGenericWhatsappProvider`; Rasel need not absorb them.
7. Self-provisioning of the bridge (install/start) exists only in Afzaz today.

## Phased migration plan (Afzaz is NOT changed now)
- **Phase 0, parity in Rasel:** items 1, 2, 5; staging Rasel with its own phone line. Exit: contract tests for groups and attachments.
- **Phase 1, shadow:** add `RaselWhatsappSender : IGenericWhatsappProvider` in Afzaz behind a flag, off by default; staging only, test and OTP-like messages.
- **Phase 2, one flow:** move one low-risk flow (statement by WhatsApp) for one pilot company; old path stays; compare delivery daily.
- **Phase 3, bulk and GPS:** move the instant customer message job and unsent-GPS resend to the Rasel outbox; UI reads Rasel job status.
- **Phase 4, retire the in-API bridge** for migrated companies; keep the template one more release.
- **Rollback at every phase:** flip the per-company (or global) flag back to the existing `WhatsAppWebConfig` path; the old bridge folder/config stay untouched until Phase 4 ends; no data migration is needed because Rasel keeps its own store.

## Not verified
Afzaz behaviour was read from code, not run. The production bridge is down and was not touched. Rasel items marked "confirm" come from its docs, not re-tested here.
