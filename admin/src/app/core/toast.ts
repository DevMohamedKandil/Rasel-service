import { Injectable, inject, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { I18n } from './i18n';

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
    let k = 'toast.generic';
    if (e instanceof HttpErrorResponse) {
      if (e.status === 0) k = 'toast.network';
      else if (e.status === 401 || e.status === 403) k = 'toast.401';
      else if (e.status === 429) k = 'toast.429';
      else if (e.status >= 500) k = 'toast.5xx';
    }
    this.show('error', this.i18n.t(k));
  }
}
