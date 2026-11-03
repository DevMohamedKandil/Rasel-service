import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApiService } from '../core/api.service';
import { dictionaryKeys } from '../core/i18n';
import { SendPage, sendErrorKey } from './send';

const SID = '11111111-1111-1111-1111-111111111111';
const MID = '22222222-2222-2222-2222-222222222222';
const err = (status: number, code: string) => new HttpErrorResponse({ status, error: { code } });
const msg = (status: string, attempts: number, code: string | null) =>
  ({ id: MID, status, attempts, createdAt: '', sentAt: null, nextAttemptAt: null, lastErrorCode: code });

function setup() {
  TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  const http = TestBed.inject(HttpTestingController);
  TestBed.inject(ApiService).login('http://api', 'k');
  const fixture = TestBed.createComponent(SendPage);
  fixture.detectChanges();
  http.expectOne('http://api/v1/system/status').flush({
    provider: 'Managed', scope: 'Admin', sidecar: { state: 'Connected', step: null, reason: null, crashes: 0, autoStart: true },
    sessions: [{ sessionId: SID, state: 'Connected', step: null, reason: null, crashes: 0 }, { sessionId: 'bad', state: 'NeedsQr', step: null, reason: null, crashes: 0 }],
  });
  fixture.detectChanges();
  return { fixture, http, page: fixture.componentInstance, el: fixture.nativeElement as HTMLElement };
}

describe('SendPage', () => {
  it('disables non-connected sessions', () => {
    const { el } = setup();
    const opts = Array.from(el.querySelectorAll<HTMLOptionElement>('[data-testid="send-session"] option'));
    expect(opts.length).toBe(3);
    expect(opts[1].disabled).toBe(false);
    expect(opts[2].disabled).toBe(true);
  });

  it('posts session, phone, text and an Idempotency-Key, then polls to Failed with a reason', () => {
    vi.useFakeTimers();
    try {
      const { fixture, http, page } = setup();
      page.sessionId = SID; page.recipient = '+201001234567'; page.text = 'hi';
      page.send();
      const r = http.expectOne('http://api/v1/messages');
      expect(r.request.body).toEqual({ sessionId: SID, phone: '+201001234567', text: 'hi' });
      expect(r.request.headers.get('Idempotency-Key')!.length).toBeGreaterThan(8);
      r.flush({ messageId: MID, status: 'Queued', replayed: false }, { status: 202, statusText: 'Accepted' });
      expect(page.last()[0].to).not.toContain('201001234567');
      vi.advanceTimersByTime(2000);
      http.expectOne(`http://api/v1/messages/${MID}`).flush(msg('Queued', 1, 'provider_transient_error'));
      expect(page.current()!.attempts).toBe(1);
      vi.advanceTimersByTime(2000);
      http.expectOne(`http://api/v1/messages/${MID}`).flush(msg('Failed', 2, 'provider_error'));
      fixture.detectChanges();
      expect(page.polling()).toBe(false);
      expect(page.reasonKey()).toBe('err.provider_error');
      vi.advanceTimersByTime(10000);
      http.expectNone(`http://api/v1/messages/${MID}`);
    } finally { vi.useRealTimers(); }
  });

  it('stops polling after 60 seconds', () => {
    vi.useFakeTimers();
    try {
      const { http, page } = setup();
      page.recipient = 'Team'; page.kind = 'group'; page.text = 'x';
      page.send();
      http.expectOne('http://api/v1/messages').flush({ messageId: MID, status: 'Queued', replayed: false });
      for (let i = 0; i < 30; i++) {
        vi.advanceTimersByTime(2000);
        http.expectOne(`http://api/v1/messages/${MID}`).flush(msg('Queued', 0, null));
      }
      expect(page.polling()).toBe(false);
      expect(page.pollNote()).toBe('snd.pollStop');
    } finally { vi.useRealTimers(); }
  });

  it('maps 409/429/503 codes to localized keys that exist in both languages', () => {
    const ar = new Set(dictionaryKeys('ar'));
    const en = new Set(dictionaryKeys('en'));
    for (const e of [err(409, 'session_not_connected'), err(409, 'idempotency_conflict'), err(429, 'recipient_daily_limit'), err(429, 'rate_limited'), err(503, 'sidecar_unavailable'), err(404, 'group_not_found')]) {
      const k = sendErrorKey(e);
      expect(ar.has(k) && en.has(k)).toBe(true);
    }
    expect(sendErrorKey(err(503, 'x'))).toBe('snd.err.503');
  });

  it('shows the char counter', async () => {
    const { fixture, el } = setup();
    await fixture.whenStable();
    const ta = el.querySelector<HTMLTextAreaElement>('[data-testid="send-text"]')!;
    ta.value = 'abc';
    ta.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="send-count"]')!.textContent).toContain('3 / 4096');
  });
});
