<div dir="rtl">

# مرجع واجهة راسل (ملخص بالعربية)

هذا ملخص بجداول للمرجع الكامل [API.md](API.md) (الإنجليزي). كل ما هنا مأخوذ من الكود أو مُجرَّب محلياً بالمزوّد التجريبي (Fake). العقد القياسي: [openapi.json](openapi.json). دليل الدمج: [INTEGRATION.ar.md](INTEGRATION.ar.md).

## الأساسيات

* كل مسارات `/v1` تتطلب الترويسة `X-Api-Key`. مسارات `/health*` بدون مفتاح.
* الأخطاء بصيغة `application/problem+json` وفيها الحقل `code` (قيمة ثابتة تعتمد عليها الأكواد، لا تعتمد على نص `detail`).
* ترويسات الرد: `X-Api-Version: v1` (لا تظهر مع 401/429 للمصادقة)، `Idempotent-Replayed: true` عند إعادة طلب بنفس مفتاح منع التكرار، `Retry-After` (بالثواني) مع 429.
* لا يوجد مسار لإنشاء أو تدوير المفاتيح. يُزرع عميل واحد `dev` عند أول تشغيل بقاعدة فارغة (المفتاح من `Seed__DevApiKey`)؛ أي مفتاح آخر يحتاج إدخال سطر في جدول `ApiClients` (لم يُنفَّذ هذا الإجراء).

## المسارات

| الطريقة والمسار | الغرض | الرد الناجح | أهم الحقول والقواعد |
|---|---|---|---|
| `POST /v1/sessions` | إنشاء جلسة | 201 `{id,name,status}` | `name` مطلوب، حتى 100 حرف |
| `GET /v1/sessions/{id}` | حالة الجلسة | 200 | الحالات: Starting, NeedsQr, Connected, Failed, Stopped |
| `GET /v1/sessions/{id}/qr` | بيانات الربط | 200 `{sessionId,status,qr}` | في Fake يصبح فتح الرمز سبباً في اتصال الجلسة |
| `DELETE /v1/sessions/{id}` | إيقاف الجلسة | 204 | |
| `POST /v1/messages` | وضع رسالة في الطابور | 202 `{messageId,status,replayed}` | `text` مطلوب حتى 4096؛ `phone` أو `groupName` (واحد فقط)؛ `sessionId` اختياري؛ ترويسة `Idempotency-Key` (حتى 128 حرف ASCII مرئي) |
| `GET /v1/messages/{id}` | حالة الرسالة | 200 | `status`: Queued / Sent / Failed؛ `attempts`؛ `nextAttemptAt` أثناء الانتظار؛ `lastErrorCode`: `provider_transient_error` أو `provider_error` |
| `POST /v1/otp/request` | طلب رمز تحقق | 200 `{challengeId,expiresInSeconds:300,resendAfterSeconds:60}` | `phone`، `purpose` (حتى 64)، `channel` = whatsapp أو sms، `language` = ar للعربية وإلا الإنجليزية. الرمز لا يُعاد أبداً |
| `POST /v1/otp/verify` | التحقق من الرمز | 200 `{token,expiresInSeconds:300}` | `challengeId` و`code` من 6 أرقام |
| `GET /health/live` | الخدمة تعمل | 200 | |
| `GET /health/ready` | قاعدة البيانات والطابور | 200 أو 503 | |

قواعد رقم الهاتف: دولي يبدأ بـ `+` (أو `00`)، من 8 إلى 15 رقماً، الرقم الأول ليس صفراً؛ تُتجاهل المسافات والشرطات والأقواس والنقاط.

## منع التكرار (Idempotency)

| الطلب | النتيجة |
|---|---|
| نفس المفتاح ونفس المحتوى | 202 بنفس `messageId` و`replayed: true` (مُجرَّب) |
| نفس المفتاح بمحتوى مختلف | 409 `idempotency_key_reused` (مُجرَّب) |

## أكواد الأخطاء

| HTTP | `code` | المعنى | ماذا تفعل |
|---|---|---|---|
| 400 | `invalid_request` | جسم الطلب تالف | صحّح الطلب |
| 400 | `invalid_name` / `invalid_text` / `invalid_target` / `invalid_phone` / `invalid_purpose` / `invalid_channel` / `invalid_idempotency_key` / `invalid_code_format` | مدخل غير صالح | صحّح المدخل |
| 400 | `otp_invalid` | رمز خاطئ (الرد يذكر المحاولات المتبقية) | اسمح بمحاولة أخرى |
| 401 | `unauthorized` | المفتاح مفقود أو غير صحيح | تحقق من المفتاح |
| 404 | `session_not_found` / `message_not_found` / `otp_not_found` / `group_not_found` | غير موجود | تحقق من المعرّف أو أنشئ الجلسة |
| 409 | `session_not_connected` | الجلسة غير متصلة | اربطها أولاً |
| 409 | `session_stopped` | الجلسة متوقفة | أنشئ جلسة جديدة |
| 409 | `idempotency_key_reused` / `idempotency_conflict` | مفتاح منع التكرار مستخدم بمحتوى آخر / قيد المعالجة | مفتاح جديد لمحتوى جديد |
| 409 | `group_ambiguous` | أكثر من مجموعة بنفس الاسم | استخدم رقماً |
| 409 | `otp_already_used` / `otp_busy` | رمز مستخدم / قيد التحديث | اطلب رمزاً جديداً / أعد بعد ثانية |
| 410 | `otp_expired` / `otp_superseded` | الرمز منتهٍ / حلّ محله رمز أحدث | اطلب رمزاً جديداً / استخدم الأحدث |
| 423 | `otp_locked` | محاولات خاطئة كثيرة (بعد 5) | توقف واطلب رمزاً لاحقاً |
| 429 | `otp_resend_too_soon` | أُرسل رمز للتو | انتظر `Retry-After` |
| 429 | `otp_rate_limited` | رموز كثيرة (60 في الدقيقة للعميل، 5 في الساعة للرقم) | خفّف الطلبات |
| 429 | `daily_limit_reached` | الحد اليومي للعميل (بتوقيت UTC) | انتظر منتصف الليل UTC |
| 429 | `recipient_daily_limit` | رسائل كثيرة لنفس الرقم اليوم (الافتراضي 5) (مُجرَّب) | انتظر منتصف الليل UTC |
| 429 | `rate_limited` | أكثر من 600 طلب في الدقيقة للمفتاح | انتظر 60 ثانية |
| 429 | `auth_throttled` | مفاتيح خاطئة كثيرة من نفس العنوان | انتظر 60 ثانية |
| 502 | `otp_delivery_failed` / `provider_unavailable` | تعذّر التسليم / مزوّد واتساب غير متاح | أعد المحاولة لاحقاً |

## الحدود

| الحد | القيمة الافتراضية | النطاق |
|---|---|---|
| `Api:RequestsPerMinute` | 600 | لكل مفتاح |
| `Api:FailedAuthPerMinute` | 20 | لكل عنوان IP |
| `Api:MaxRequestBodyBytes` | 65536 | للطلب |
| الحد اليومي للعميل | 1000 (للعميل المزروع) | يوم UTC، حدّ مرن |
| `Pacing:DailyPerRecipient` | 5 | لكل عميل ورقم مستلم في اليوم |
| التباعد `Pacing:MinGapMs` | 1500 مللي ثانية | لكل جلسة للرسائل الجماعية |
| محاولات الإرسال `Outbox:MaxAttempts` | 5 مع تأخير متصاعد (5 ثوانٍ يتضاعف حتى 900) | لكل رسالة |

## Webhooks

اختياري ومعطّل افتراضياً (`Webhooks:Enabled`, `Url`, `Secret`). تُرسل راسل طلب POST واحداً عند الحالة النهائية `message.sent` أو `message.failed`، بمحاولة واحدة بلا إعادة. الترويسات: `X-Rasel-Timestamp` (ثوانٍ يونكس) و`X-Rasel-Signature: sha256=<hex>` حيث التوقيع = `HMAC_SHA256(secret, timestamp + "." + rawBody)` بحروف hex صغيرة. تحقّق من البايتات الخام وبمقارنة ثابتة الزمن وارفض الطوابع القديمة. مثال التحقق: [examples/verify-webhook.ts](examples/verify-webhook.ts). **قُرئ من الكود ولم يُستقبل webhook حقيقي أثناء التوثيق.**

## الإصدارات

الإصدار في المسار (`/v1`)، وأي تغيير كاسر يأتي كمسار جديد `/v2` بجانب القديم. تجاهل الحقول الجديدة غير المعروفة في الردود.

</div>
