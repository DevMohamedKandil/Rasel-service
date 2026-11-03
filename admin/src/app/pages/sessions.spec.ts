import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApiService } from '../core/api.service';
import { EngineService } from '../core/engine.service';
import { SessionStore } from '../core/session.store';
import { ToastService, errorToastKey } from '../core/toast';
import { I18n, dictionaryKeys } from '../core/i18n';
import { SessionsPage } from './sessions';

const SID = '11111111-1111-1111-1111-111111111111';
const status = (scope: string, state = 'Connected') => ({
  provider: 'Managed', scope,
  sidecar: { state, step: null, reason: null, crashes: 0, autoStart: true },
  sessions: scope === 'Admin' ? [{ sessionId: SID, state, step: null, reason: null, crashes: 0 }] : [],
});

function setup(scope: string, sessionStatus = 'Connected') {
  TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  const http = TestBed.inject(HttpTestingController);
  TestBed.inject(ApiService).login('http://api', 'k');
  TestBed.inject(SessionStore).add({ id: SID, name: 'main', status: sessionStatus });
  const fixture = TestBed.createComponent(SessionsPage);
  fixture.detectChanges();
  http.expectOne('http://api/v1/system/status').flush(status(scope, sessionStatus));
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const btn = (id: string) => el.querySelector<HTMLButtonElement>(`[data-testid="act-${id}"]`)!;
  return { fixture, http, el, btn, i18n: TestBed.inject(I18n) };
}

describe('SessionsPage controls', () => {
  afterEach(() => { TestBed.inject(EngineService).stop(); TestBed.resetTestingModule(); });

  it('Admin key: all five session buttons are enabled', () => {
    const { btn } = setup('Admin');
    for (const a of ['start', 'stop', 'restart', 'logout', 'delete']) expect(btn(a).disabled, a).toBe(false);
  });

  it('Standard key: buttons are disabled with an explanatory tooltip, and clicking opens nothing', () => {
    const { btn, fixture, el, i18n } = setup('Standard');
    for (const a of ['start', 'stop', 'restart', 'logout', 'delete']) {
      expect(btn(a).disabled, a).toBe(true);
      expect(btn(a).title).toBe(i18n.t('ses.adminOnly'));
    }
    expect(el.querySelector('[data-testid="std-note"]')).not.toBeNull();
    btn('delete').click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="confirm-dialog"]')).toBeNull();
  });

  it('Disconnect is disabled unless the session is connected', () => {
    const { btn, i18n } = setup('Admin', 'NeedsQr');
    expect(btn('logout').disabled).toBe(true);
    expect(btn('logout').title).toBe(i18n.t('ses.needConnected'));
    expect(btn('stop').disabled).toBe(false);
  });

  it('Stop asks first and explains queued messages wait; cancel sends nothing', () => {
    const { btn, fixture, el, http, i18n } = setup('Admin');
    btn('stop').click();
    fixture.detectChanges();
    const dlg = el.querySelector('[data-testid="confirm-dialog"]')!;
    expect(dlg.textContent).toContain(i18n.t('dlg.stop.body'));
    el.querySelector<HTMLButtonElement>('[data-testid="confirm-cancel"]')!.click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="confirm-dialog"]')).toBeNull();
    http.expectNone((r) => r.url.includes('/sidecar/stop'));
  });

  it('Stop confirmed calls the engine endpoint for that session only', () => {
    const { btn, fixture, el, http } = setup('Admin');
    btn('stop').click();
    fixture.detectChanges();
    el.querySelector<HTMLButtonElement>('[data-testid="confirm-ok"]')!.click();
    const req = http.expectOne(`http://api/v1/system/sidecar/stop?sessionId=${SID}`);
    expect(req.request.method).toBe('POST');
    req.flush(status('Admin', 'Stopped'), { status: 202, statusText: 'Accepted' });
  });

  it('Disconnect confirmed posts /logout and then shows the QR panel', () => {
    const { btn, fixture, el, http } = setup('Admin');
    btn('logout').click();
    fixture.detectChanges();
    expect(el.textContent).toContain(TestBed.inject(I18n).t('dlg.logout.body'));
    el.querySelector<HTMLButtonElement>('[data-testid="confirm-ok"]')!.click();
    http.expectOne(`http://api/v1/sessions/${SID}/logout`).flush({ id: SID, name: 'main', status: 'NeedsQr' });
    http.expectOne(`http://api/v1/sessions/${SID}/qr`).flush({ sessionId: SID, status: 'NeedsQr', qr: 'FAKE-QR' });
    expect(TestBed.inject(SessionStore).sessions()[0].status).toBe('NeedsQr');
  });

  it('Delete: plain delete has no purge flag; ticking the box maps to ?purge=true; the session leaves the list', () => {
    let t = setup('Admin');
    t.btn('delete').click();
    t.fixture.detectChanges();
    t.el.querySelector<HTMLButtonElement>('[data-testid="confirm-ok"]')!.click();
    t.http.expectOne({ method: 'DELETE', url: `http://api/v1/sessions/${SID}` }).flush(null, { status: 204, statusText: 'No Content' });
    expect(TestBed.inject(SessionStore).sessions().length).toBe(0);
    TestBed.inject(EngineService).stop(); TestBed.resetTestingModule();

    t = setup('Admin');
    t.btn('delete').click();
    t.fixture.detectChanges();
    const box = t.el.querySelector<HTMLInputElement>('[data-testid="purge-box"]')!;
    box.click();
    t.fixture.detectChanges();
    t.el.querySelector<HTMLButtonElement>('[data-testid="confirm-ok"]')!.click();
    t.http.expectOne({ method: 'DELETE', url: `http://api/v1/sessions/${SID}?purge=true` }).flush(null, { status: 204, statusText: 'No Content' });
  });

  it('a server refusal shows a friendly toast, not the raw body', () => {
    const { btn, fixture, el, http } = setup('Admin');
    btn('stop').click();
    fixture.detectChanges();
    el.querySelector<HTMLButtonElement>('[data-testid="confirm-ok"]')!.click();
    http.expectOne((r) => r.url.includes('/sidecar/stop')).flush({ code: 'forbidden_scope', secret: 'x' }, { status: 403, statusText: 'Forbidden' });
    const toast = TestBed.inject(ToastService);
    expect(toast.items()[0].text).toBe(TestBed.inject(I18n).t('toast.forbidden_scope'));
    expect(JSON.stringify(toast.items())).not.toContain('secret');
  });
});

describe('Add number flow', () => {
  afterEach(() => { TestBed.inject(EngineService).stop(); TestBed.resetTestingModule(); });

  it('creates the session then opens its QR panel', () => {
    const { fixture, el, http } = setup('Admin');
    el.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    http.expectOne({ method: 'POST', url: 'http://api/v1/sessions' }).flush({ id: 'new-1', name: 'main', status: 'Starting' });
    http.expectOne('http://api/v1/sessions/new-1/qr').flush({ sessionId: 'new-1', status: 'NeedsQr', qr: 'FAKE-QR' });
    fixture.detectChanges();
    expect(TestBed.inject(SessionStore).sessions().map((s) => s.id)).toContain('new-1');
    expect(el.querySelector('.qr-panel')).not.toBeNull();
  });

  it('409 max_sessions_reached gives the friendly limit message', () => {
    const { fixture, el, http, i18n } = setup('Admin');
    el.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    http.expectOne({ method: 'POST', url: 'http://api/v1/sessions' }).flush({ code: 'max_sessions_reached' }, { status: 409, statusText: 'Conflict' });
    expect(TestBed.inject(ToastService).items()[0].text).toBe(i18n.t('toast.409.max_sessions_reached'));
  });
});

describe('engine status polling', () => {
  it('polls quickly while Starting and slowly once settled', () => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);
    TestBed.inject(ApiService).login('http://api', 'k');
    const engine = TestBed.inject(EngineService);
    engine.start();
    http.expectOne('http://api/v1/system/status').flush(status('Admin', 'Starting'));
    vi.advanceTimersByTime(3000);
    http.expectOne('http://api/v1/system/status').flush(status('Admin', 'Starting'));
    vi.advanceTimersByTime(3000);
    http.expectOne('http://api/v1/system/status').flush(status('Admin', 'NeedsQr'));
    vi.advanceTimersByTime(3000);
    http.expectNone('http://api/v1/system/status'); // settled: slow poll now
    vi.advanceTimersByTime(12000);
    http.expectOne('http://api/v1/system/status').flush(status('Admin', 'NeedsQr'));
    engine.stop();
    vi.useRealTimers();
    TestBed.resetTestingModule();
  });
});

describe('errorToastKey', () => {
  const e = (statusCode: number, code?: string) => new HttpErrorResponse({ status: statusCode, error: code ? { code } : null });
  it('maps scope, conflict and unavailable errors to specific plain-language keys', () => {
    expect(errorToastKey(e(403, 'forbidden_scope'))).toBe('toast.forbidden_scope');
    expect(errorToastKey(e(401))).toBe('toast.401');
    expect(errorToastKey(e(409, 'max_sessions_reached'))).toBe('toast.409.max_sessions_reached');
    expect(errorToastKey(e(409, 'session_not_connected'))).toBe('toast.409.session_not_connected');
    expect(errorToastKey(e(409, 'whatever'))).toBe('toast.409');
    expect(errorToastKey(e(503))).toBe('toast.503');
    expect(errorToastKey(e(500))).toBe('toast.5xx');
    expect(errorToastKey(e(0))).toBe('toast.network');
  });
  it('every mapped key and every new control hint exists in both languages', () => {
    const ar = new Set(dictionaryKeys('ar')), en = new Set(dictionaryKeys('en'));
    const keys = ['toast.forbidden_scope', 'toast.409', 'toast.503', 'toast.409.max_sessions_reached', 'toast.409.session_not_connected', 'toast.409.sidecar_not_managed', 'toast.409.purge_incomplete', 'toast.409.session_stopped',
      ...['start', 'stop', 'restart', 'logout', 'delete'].flatMap((a) => [`hint.act.${a}`, `dlg.${a}.title`, `dlg.${a}.body`, `ses.${a}`, `ses.done.${a}`]), 'hint.addNumber', 'hint.engine', 'hint.detail', 'dlg.delete.purgeHint',
      ...['Disabled', 'Preparing', 'InstallingDependencies', 'Ready', 'Starting', 'NeedsQr', 'Connected', 'Stopped', 'Degraded', 'Failed'].flatMap((s) => [`eng.${s}`, `hint.eng.${s}`])];
    for (const k of keys) { expect(ar.has(k), 'ar ' + k).toBe(true); expect(en.has(k), 'en ' + k).toBe(true); }
  });
});
