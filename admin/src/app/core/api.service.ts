import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface SessionDto { id: string; name: string; status: string; linkedPhoneMasked?: string | null; }
export interface QrDto { sessionId: string; status: string; qr: string | null; linkedPhoneMasked?: string | null; }
export interface LinkedNumberDto { sessionId: string; phone: string; }
export interface MessageStatusDto {
  id: string; status: string; attempts: number; createdAt: string;
  sentAt: string | null; nextAttemptAt: string | null; lastErrorCode: string | null;
}
export interface EngineSessionDto { sessionId: string; state: string; step: string | null; reason: string | null; crashes: number; }
export interface SystemStatusDto {
  provider: string; scope?: string;
  sidecar: { state: string; step: string | null; reason: string | null; crashes: number; autoStart: boolean };
  sessions: EngineSessionDto[];
}
export interface HealthDto { status: string; checks: { name: string; status: string; detail: string | null }[]; }

/**
 * Holds the API key in memory only (a private signal): never written to localStorage, sessionStorage, cookies or the URL.
 * A page reload logs out by design.
 */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly key = signal('');
  private readonly base = signal('');
  readonly loggedIn = computed(() => this.key().length > 0);

  login(baseUrl: string, apiKey: string): void {
    this.base.set(baseUrl.trim().replace(/\/+$/, ''));
    this.key.set(apiKey.trim());
  }
  logout(): void { this.key.set(''); }

  private headers() { return { 'X-Api-Key': this.key() }; }
  private url(path: string) { return this.base() + path; }

  createSession(name: string): Observable<SessionDto> {
    return this.http.post<SessionDto>(this.url('/v1/sessions'), { name }, { headers: this.headers() });
  }
  session(id: string): Observable<SessionDto> { return this.http.get<SessionDto>(this.url(`/v1/sessions/${id}`), { headers: this.headers() }); }
  qr(id: string): Observable<QrDto> { return this.http.get<QrDto>(this.url(`/v1/sessions/${id}/qr`), { headers: this.headers() }); }
  linkedNumber(id: string): Observable<LinkedNumberDto> { return this.http.get<LinkedNumberDto>(this.url(`/v1/sessions/${id}/linked-number`), { headers: this.headers() }); }
  message(id: string): Observable<MessageStatusDto> { return this.http.get<MessageStatusDto>(this.url(`/v1/messages/${id}`), { headers: this.headers() }); }
  systemStatus(): Observable<SystemStatusDto> { return this.http.get<SystemStatusDto>(this.url('/v1/system/status'), { headers: this.headers() }); }
  /** Engine control (Admin key): start | stop | restart one session's engine. */
  engine(action: 'start' | 'stop' | 'restart', sessionId: string): Observable<SystemStatusDto> {
    return this.http.post<SystemStatusDto>(this.url(`/v1/system/sidecar/${action}?sessionId=${sessionId}`), null, { headers: this.headers() });
  }
  /** Unlinks the number; a new QR scan is needed afterwards (Admin key). */
  logoutSession(id: string): Observable<SessionDto> { return this.http.post<SessionDto>(this.url(`/v1/sessions/${id}/logout`), null, { headers: this.headers() }); }
  /** Removes the session from Rasel; purge=true also deletes its stored WhatsApp data (Admin key). */
  deleteSession(id: string, purge: boolean): Observable<void> { return this.http.delete<void>(this.url(`/v1/sessions/${id}${purge ? '?purge=true' : ''}`), { headers: this.headers() }); }
  health(): Observable<HealthDto> { return this.http.get<HealthDto>(this.url('/health/ready')); }
}

/** A short, safe message for an HTTP failure (RFC 7807 `code` when present). */
export function describeError(e: unknown): string {
  if (e instanceof HttpErrorResponse) {
    if (e.status === 0) return 'Cannot reach the API (network error or origin not allowed by Cors:AllowedOrigins).';
    const code = (e.error && typeof e.error === 'object' && 'code' in e.error) ? String(e.error.code) : '';
    return `HTTP ${e.status}${code ? ' ' + code : ''}`;
  }
  return 'Unexpected error';
}
