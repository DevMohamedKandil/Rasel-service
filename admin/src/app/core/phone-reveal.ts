import { signal } from '@angular/core';
import { Observable } from 'rxjs';

/** How long a revealed number stays visible before it is hidden again. */
export const REVEAL_MS = 30_000;

/**
 * Reveal/hide state for the linked phone number. The full number is fetched only on an explicit reveal, is kept in memory
 * only while revealed, and is dropped by hide() (also called on QR close, session change, logout and after REVEAL_MS).
 */
export class PhoneReveal {
  readonly revealed = signal(false);
  readonly full = signal('');
  readonly loading = signal(false);
  private timer: ReturnType<typeof setTimeout> | null = null;
  private forId = '';

  constructor(private readonly fetchFull: (sessionId: string) => Observable<string>, private readonly ttlMs = REVEAL_MS) {}

  toggle(sessionId: string, onError?: (e: unknown) => void): void {
    if (this.revealed() || this.loading()) { this.hide(); return; }
    this.loading.set(true);
    this.forId = sessionId;
    this.fetchFull(sessionId).subscribe({
      next: (phone) => {
        if (!this.loading() || this.forId !== sessionId) return; // hidden or session changed while loading
        this.loading.set(false);
        this.full.set(phone);
        this.revealed.set(true);
        this.timer = setTimeout(() => this.hide(), this.ttlMs);
      },
      error: (e) => { this.loading.set(false); onError?.(e); },
    });
  }

  hide(): void {
    if (this.timer) { clearTimeout(this.timer); this.timer = null; }
    this.forId = '';
    this.loading.set(false);
    this.revealed.set(false);
    this.full.set('');
  }
}
