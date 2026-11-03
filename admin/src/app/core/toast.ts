import { Injectable, inject, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { I18n } from './i18n';

/** i18n key for an HTTP failure: scope, conflict and unavailable errors get their own plain-language text. */
export function errorToastKey(e: unknown): string {
  if (!(e instanceof HttpErrorResponse)) return 'toast.generic';
  const code = e.error && typeof e.error === 'object' && 'code' in e.error ? String((e.error as { code: unknown }).code) : '';
  if (e.status === 0) return 'toast.network';
  if (e.status === 403 && code === 'forbidden_scope') return 'toast.forbidden_scope';
  if (e.status === 401 || e.status === 403) return 'toast.401';
  if (e.status === 409) {
    const k = 'toast.409.' + code;
    return ['max_sessions_reached', 'session_not_connected', 'sidecar_not_managed', 'purge_incomplete', 'session_stopped'].includes(code) ? k : 'toast.409';
  }
  if (e.status === 429) return 'toast.429';
  if (e.status === 503) return 'toast.503';
  if (e.status >= 500) return 'toast.5xx';
  return 'toast.generic';
}

export interface Toast { id: number; kind: 'error' | 'success'; text: string; }

@Injectable({ providedIn: 'root' })
export class ToastService {
  private readonly i18n = inject(I18n);
  readonly items = signal<Toast[]>([]);
  private n = 0;

  show(kind: Toast['kind'], text: string) {
    const id = ++this.n;
    this.items.update((l) => [...l.slice(-3), { id, kind, text }]);
    setTimeout(() => this.dismiss(id), 6000);
  }
  dismiss(id: number) { this.items.update((l) => l.filter((x) => x.id !== id)); }
  success(key: string) { this.show('success', this.i18n.t(key)); }
  /** Friendly text only: never raw bodies, stacks or JSON. */
  error(e: unknown) {
    this.show('error', this.i18n.t(errorToastKey(e)));
  }
}
