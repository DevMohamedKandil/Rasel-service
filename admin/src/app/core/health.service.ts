import { Injectable, inject, signal } from '@angular/core';
import { ApiService, HealthDto } from './api.service';

export type Overall = 'unknown' | 'ok' | 'bad' | 'down';

/** Polls the unauthenticated readiness endpoint for the top bar indicator and the dashboard. */
@Injectable({ providedIn: 'root' })
export class HealthService {
  private readonly api = inject(ApiService);
  readonly data = signal<HealthDto | null>(null);
  readonly overall = signal<Overall>('unknown');
  readonly loading = signal(false);
  private timer: ReturnType<typeof setInterval> | null = null;

  start() { if (!this.timer) { this.refresh(); this.timer = setInterval(() => this.refresh(), 30000); } }
  stop() { if (this.timer) { clearInterval(this.timer); this.timer = null; } }

  refresh() {
    this.loading.set(true);
    this.api.health().subscribe({
      next: (h) => this.apply(h),
      // an unhealthy check answers 503 with the same JSON body
      error: (e) => {
        if (e?.error?.checks) return this.apply(e.error);
        this.data.set(null);
        this.overall.set('down');
        this.loading.set(false);
      },
    });
  }
  private apply(h: HealthDto) {
    this.data.set(h);
    this.overall.set(h.status.toLowerCase() === 'ok' || h.status.toLowerCase() === 'healthy' ? 'ok' : 'bad');
    this.loading.set(false);
  }
}
