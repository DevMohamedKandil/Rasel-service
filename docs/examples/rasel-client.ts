// Server-side TypeScript client (Node 18+, uses the built-in fetch). Use it from YOUR backend only:
// the API key must never be shipped to a browser or a mobile app.
// Run the demo:  node --experimental-strip-types rasel-client.ts   (Node 22.6+)

export interface Problem { code: string; detail?: string; status: number }
export class RaselError extends Error {
  status: number;
  code: string;
  retryAfter?: number;
  constructor(status: number, code: string, detail: string, retryAfter?: number) {
    super(`${status} ${code}: ${detail}`);
    this.status = status;
    this.code = code;
    this.retryAfter = retryAfter;
  }
}

export class RaselClient {
  private baseUrl: string;
  private apiKey: string;
  private maxRetries: number;
  constructor(baseUrl: string, apiKey: string, maxRetries = 3) {
    this.baseUrl = baseUrl;
    this.apiKey = apiKey;
    this.maxRetries = maxRetries;
  }

  private async call<T>(method: string, path: string, body?: unknown, idempotencyKey?: string): Promise<T> {
    const canRetry = method === 'GET' || idempotencyKey !== undefined;
    for (let attempt = 0; ; attempt++) {
      try {
        const res = await fetch(this.baseUrl.replace(/\/+$/, '') + path, {
          method,
          headers: {
            'X-Api-Key': this.apiKey,
            ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
            ...(idempotencyKey ? { 'Idempotency-Key': idempotencyKey } : {}),
          },
          body: body !== undefined ? JSON.stringify(body) : undefined,
        });
        if (res.ok) return (res.status === 204 ? undefined : await res.json()) as T;
        if (res.status >= 500 && canRetry && attempt < this.maxRetries) { await sleep(2 ** attempt * 1000); continue; }
        const p = (await res.json().catch(() => ({}))) as Partial<Problem>;
        const ra = res.headers.get('Retry-After');
        throw new RaselError(res.status, p.code ?? 'unknown', p.detail ?? res.statusText, ra ? Number(ra) : undefined);
      } catch (e) {
        if (e instanceof RaselError) throw e;
        if (canRetry && attempt < this.maxRetries) { await sleep(2 ** attempt * 1000); continue; }
        throw e;
      }
    }
  }

  createSession(name: string) { return this.call<{ id: string; name: string; status: string }>('POST', '/v1/sessions', { name }); }
  sendText(phone: string, text: string, idempotencyKey: string, sessionId?: string) {
    return this.call<{ messageId: string; status: string; replayed: boolean }>('POST', '/v1/messages', { phone, text, sessionId }, idempotencyKey);
  }
  getMessage(id: string) {
    return this.call<{ id: string; status: string; attempts: number; lastErrorCode: string | null }>('GET', `/v1/messages/${id}`);
  }
  requestOtp(phone: string, purpose: string, language: 'ar' | 'en' = 'ar') {
    return this.call<{ challengeId: string; expiresInSeconds: number; resendAfterSeconds: number }>('POST', '/v1/otp/request', { phone, purpose, channel: 'whatsapp', language });
  }
  verifyOtp(challengeId: string, code: string) {
    return this.call<{ token: string; expiresInSeconds: number }>('POST', '/v1/otp/verify', { challengeId, code });
  }
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

// ---- demo (runs only when executed directly) ----
if (process.argv[1]?.endsWith('rasel-client.ts')) {
  const rasel = new RaselClient(process.env['RASEL_URL'] ?? 'http://127.0.0.1:5114', process.env['RASEL_KEY'] ?? '');
  const s = await rasel.createSession('ts-demo');
  await fetch(`${process.env['RASEL_URL']}/v1/sessions/${s.id}/qr`, { headers: { 'X-Api-Key': process.env['RASEL_KEY'] ?? '' } }); // Fake only: connects the session
  const m = await rasel.sendText('+2010' + String(Math.floor(Math.random() * 1e8)).padStart(8, '1'), 'hello', 'ts-' + Date.now(), s.id);
  console.log('send', m);
  await sleep(1500);
  console.log('status', await rasel.getMessage(m.messageId));
  const c = await rasel.requestOtp('+201005550777', 'login', 'en');
  console.log('otp', c);
  await rasel.requestOtp('+201005550777', 'login').catch((e: RaselError) => console.log('again ->', e.status, e.code, 'retryAfter', e.retryAfter));
  await rasel.verifyOtp(c.challengeId, '000000').catch((e: RaselError) => console.log('verify ->', e.status, e.code, e.message));
}
