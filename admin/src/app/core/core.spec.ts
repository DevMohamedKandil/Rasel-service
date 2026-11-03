import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { ApiService } from './api.service';
import { authGuard } from './auth.guard';
import { maskPhones } from './mask';
import { parseQr } from './qr';

describe('maskPhones', () => {
  it('masks phone-like numbers and keeps the last two digits', () => {
    expect(maskPhones('failed for +201001234567 now')).toBe('failed for **********67 now');
    expect(maskPhones('call 0100 123 4567')).toContain('*********67');
  });
  it('leaves short numbers and plain codes alone', () => {
    expect(maskPhones('provider_error attempts 5')).toBe('provider_error attempts 5');
    expect(maskPhones(null)).toBe('');
  });
});

describe('parseQr', () => {
  it('extracts only a png data url from the bridge html', () => {
    expect(parseQr('<img src="data:image/png;base64,AAAA">')).toEqual({ kind: 'image', src: 'data:image/png;base64,AAAA' });
  });
  it('never passes through other html', () => {
    expect(parseQr('<img src="x" onerror="alert(1)">')).toEqual({ kind: 'none' });
    expect(parseQr('<script>alert(1)</script>')).toEqual({ kind: 'none' });
  });
  it('plain text and empty', () => {
    expect(parseQr('FAKE-QR-123')).toEqual({ kind: 'text', text: 'FAKE-QR-123' });
    expect(parseQr(null)).toEqual({ kind: 'none' });
  });
});

describe('ApiService', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] }));

  it('sends the key as header, uses the base url, and never persists it', () => {
    const setItem = vi.spyOn(Storage.prototype, 'setItem');
    const api = TestBed.inject(ApiService);
    const http = TestBed.inject(HttpTestingController);
    api.login('http://api.test/', 'secret-key-123456');
    api.message('abc').subscribe();
    const req = http.expectOne('http://api.test/v1/messages/abc');
    expect(req.request.headers.get('X-Api-Key')).toBe('secret-key-123456');
    expect(setItem).not.toHaveBeenCalled();
    expect(JSON.stringify(localStorage)).not.toContain('secret-key');
    expect(JSON.stringify(sessionStorage)).not.toContain('secret-key');
  });

  it('health call carries no key', () => {
    const api = TestBed.inject(ApiService);
    const http = TestBed.inject(HttpTestingController);
    api.login('http://api.test', 'secret-key-123456');
    api.health().subscribe();
    expect(http.expectOne('http://api.test/health/ready').request.headers.has('X-Api-Key')).toBe(false);
  });

  it('logout clears the key and the guard redirects to /login', () => {
    const api = TestBed.inject(ApiService);
    const router = TestBed.inject(Router);
    expect(TestBed.runInInjectionContext(() => authGuard({} as never, {} as never))).toEqual(router.createUrlTree(['/login']));
    api.login('http://api.test', 'k');
    expect(TestBed.runInInjectionContext(() => authGuard({} as never, {} as never))).toBe(true);
    api.logout();
    expect(api.loggedIn()).toBe(false);
  });
});
