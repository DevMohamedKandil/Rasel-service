# TextBee SMS provider

Rasel can use the hosted TextBee API as its SMS provider. WhatsApp remains independently configurable and the existing OTP channel selection is unchanged.

## Configuration

Keep the API key in an environment variable or secret store. Do not commit a real key.

```json
{
  "Sms": {
    "Provider": "TextBee",
    "TextBee": {
      "BaseUrl": "https://api.textbee.dev/api/v1/",
      "ApiKey": "",
      "DeviceId": "",
      "TimeoutSeconds": 15
    }
  }
}
```

Environment-variable equivalents:

- `Sms__Provider=TextBee`
- `Sms__TextBee__ApiKey=<secret>`
- `Sms__TextBee__DeviceId=<optional-device-id>`

The default remains `Sms:Provider=Fake`, so existing development and test behavior does not change. TextBee requires an HTTPS base URL and a non-empty API key. The optional device ID pins sends to one registered TextBee device; leave it empty to use TextBee's default-device selection.

## OTP channel selection

The existing `POST /v1/otp/request` contract already accepts `channel`:

- `channel: "sms"`: use the configured SMS provider directly.
- `channel: "whatsapp"` (or omitted): try WhatsApp first and fall back to the configured SMS provider if WhatsApp is unavailable or send fails.

No OTP API contract change is required.

## TextBee API behavior

Rasel calls `POST /api/v1/gateway/send-sms` with the `x-api-key` header and one E.164 recipient. TextBee's successful HTTP response means the batch was accepted by TextBee; it does not prove carrier delivery. TextBee delivery receipts and webhooks are not wired into Rasel by this provider adapter yet. Accordingly, the current `ISmsProvider` contract can only report successful API acceptance, not a final `delivered` state.

HTTP 408 and 5xx responses, network failures and timeouts are classified as transient provider errors. Authentication, validation and quota responses are permanent failures; in particular TextBee 429 plan-limit responses are not blindly retried.

## Operational setup

1. Create and verify a TextBee account.
2. Install the TextBee Android app on an Android phone with an active SMS-capable SIM.
3. Register the phone and grant SMS permissions; keep it powered and online.
4. Create an API key and store it as `Sms__TextBee__ApiKey`.
5. Set `Sms__Provider=TextBee` and optionally configure a device ID.
6. Test with a phone number you control before enabling SMS OTP for customers.

Do not expose the TextBee API key to Angular or other client applications. All requests must pass through Rasel's authenticated backend.

Official API reference: https://textbee.dev/docs/api-reference
Sending guide: https://textbee.dev/docs/sending-sms/sending-sms

## Email provider (SMTP)

Rasel also exposes an independent `IEmailProvider` transport. The default is `Fake`, so existing deployments do not attempt real email delivery until explicitly configured.

Example configuration:

```json
{
  "Email": {
    "Provider": "Smtp",
    "Smtp": {
      "Host": "smtp.example.com",
      "Port": 587,
      "Username": "",
      "Password": "",
      "FromEmail": "no-reply@example.com",
      "FromName": "Rasel",
      "EnableSsl": true,
      "TimeoutSeconds": 20
    }
  }
}
```

Set secrets through environment variables, not committed configuration:

- `Email__Provider=Smtp`
- `Email__Smtp__Host=smtp.example.com`
- `Email__Smtp__Port=587`
- `Email__Smtp__Username=<smtp-user>`
- `Email__Smtp__Password=<smtp-password-or-app-password>`
- `Email__Smtp__FromEmail=no-reply@example.com`

Use the SMTP credentials and sender address authorized by your mail provider. For Gmail or Google Workspace, use an app password where the account policy supports it; do not use the normal account password. Keep SMTP submission on port 587 with STARTTLS unless your provider specifies another secure configuration.

A successful SMTP send means the SMTP server accepted the message; it does not guarantee inbox placement or final delivery. Delivery/bounce webhooks are not implemented.

**Scope note:** this commit adds the email transport abstraction and SMTP adapter only. The existing public message API and OTP flow are still WhatsApp/SMS-oriented; email is not yet exposed as a customer-selectable channel. Adding email OTP or a public email-send endpoint requires a separate API/domain design (including email recipient validation, rate limits, idempotency, and durable outbox handling).
