// Verifies a Rasel webhook. Header X-Rasel-Signature = "sha256=" + hex(HMAC_SHA256(secret, timestamp + "." + rawBody)),
// header X-Rasel-Timestamp = unix seconds. Always verify the RAW request bytes, before any JSON parsing.
import { createHmac, timingSafeEqual } from 'node:crypto';

export function verifyRaselWebhook(secret: string, rawBody: Buffer, timestampHeader: string, signatureHeader: string, toleranceSeconds = 300, nowSeconds = Math.floor(Date.now() / 1000)): boolean {
  const ts = Number(timestampHeader);
  if (!Number.isFinite(ts) || Math.abs(nowSeconds - ts) > toleranceSeconds) return false; // replayed or stale
  const expected = createHmac('sha256', secret).update(`${timestampHeader}.`).update(rawBody).digest('hex');
  const given = signatureHeader.startsWith('sha256=') ? signatureHeader.slice(7) : '';
  const a = Buffer.from(expected, 'utf8');
  const b = Buffer.from(given, 'utf8');
  return a.length === b.length && timingSafeEqual(a, b);
}

// Self-check (node --experimental-strip-types verify-webhook.ts): signs a sample the same way the dispatcher does.
if (process.argv[1]?.endsWith('verify-webhook.ts')) {
  const secret = 'example-webhook-secret';
  const body = Buffer.from(JSON.stringify({ event: 'message.sent', clientId: '00000000-0000-0000-0000-000000000000', timestamp: '2026-01-01T00:00:00+00:00', data: { messageId: '11111111-1111-1111-1111-111111111111', status: 'Sent', error: null } }));
  const ts = String(Math.floor(Date.now() / 1000));
  const sig = 'sha256=' + createHmac('sha256', secret).update(ts + '.').update(body).digest('hex');
  console.log('valid   ->', verifyRaselWebhook(secret, body, ts, sig));
  console.log('tampered->', verifyRaselWebhook(secret, Buffer.from(body.toString().replace('Sent', 'Failed')), ts, sig));
  console.log('stale   ->', verifyRaselWebhook(secret, body, ts, sig, 300, Number(ts) + 3600));
  console.log('signature', sig);
}
