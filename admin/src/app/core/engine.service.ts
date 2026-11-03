import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { ApiService, SystemStatusDto } from './api.service';

/** States in which the engine is still getting ready: poll quickly until it settles. */
export const TRANSITIONAL = ['Preparing', 'InstallingDependencies', 'Starting'];
export const FAST_MS = 3000;
export const SLOW_MS = 15000;
const BOOST_MS = 30000;

/**
 * Engine/session state from GET /v1/system/status. Admin keys get per-session details; Standard keys only the coarse aggregate state.
 * Polls every 3 s while anything is Preparing/Starting (or just after a start/stop/restart), otherwise every 15 s.
 */
@Injectable({ providedIn: 'root' })
export class EngineService {
  private readonly api = inject(ApiService);
  readonly status = signal<SystemStatusDto | null>(null);
  readonly failed = signal(false);
  /** Unknown (not loaded yet) counts as not admin: buttons stay disabled until the server has said so. */
  readonly isAdmin = computed(() => this.status()?.scope === 'Admin');
  readonly aggregate = computed(() => this.status()?.sidecar.state ?? null);
  readonly transitional = computed(() => {
    const s = this.status();
    if (!s) return false;
    return TRANSITIONAL.includes(s.sidecar.state) || s.sessions.some((x) => TRANSITIONAL.includes(x.state));
  });
  private timer: ReturnType<typeof setTimeout> | null = null;
  private running = 0;
  private boostUntil = 0;

  constructor() {
    // A new key may have another scope: never keep the previous key's status after sign-out.
    effect(() => { if (!this.api.loggedIn()) this.reset(); });
  }

  start() { if (this.running++ === 0) this.tick(); }
  stop() { if (this.running > 0 && --this.running === 0 && this.timer) { clearTimeout(this.timer); this.timer = null; } }
  /** After an action: poll fast for a while and right now. */
  boost() { this.boostUntil = Date.now() + BOOST_MS; this.refresh(); }
  reset() { this.status.set(null); this.failed.set(false); }

  /** Detailed state of one session (Admin keys), else null. */
  sessionEngine(id: string) { return this.status()?.sessions.find((x) => x.sessionId === id) ?? null; }
  /** State to show for a session: its own when known (Admin), otherwise the aggregate. */
  stateFor(id: string): string | null { return this.sessionEngine(id)?.state ?? this.aggregate(); }

  refresh(done?: () => void) {
    this.api.systemStatus().subscribe({
      next: (s) => { this.status.set(s); this.failed.set(false); done?.(); },
      error: () => { this.failed.set(true); done?.(); },
    });
  }

  /** The next poll is scheduled after the answer, so the interval reflects the state just received. */
  private tick() {
    this.refresh(() => {
      if (this.running === 0) return;
      const fast = this.transitional() || Date.now() < this.boostUntil;
      this.timer = setTimeout(() => { if (this.running > 0) this.tick(); }, fast ? FAST_MS : SLOW_MS);
    });
  }
}
