import { Injectable, signal } from '@angular/core';
import { SessionDto } from './api.service';

/** In-memory only: the API has no list endpoint, so this tab remembers what it created. */
@Injectable({ providedIn: 'root' })
export class SessionStore {
  readonly sessions = signal<SessionDto[]>([]);
  add(s: SessionDto) { this.sessions.update((l) => [s, ...l.filter((x) => x.id !== s.id)]); }
  update(id: string, patch: Partial<SessionDto>) { this.sessions.update((l) => l.map((x) => (x.id === id ? { ...x, ...patch } : x))); }
  remove(id: string) { this.sessions.update((l) => l.filter((x) => x.id !== id)); }
  clear() { this.sessions.set([]); }
}
