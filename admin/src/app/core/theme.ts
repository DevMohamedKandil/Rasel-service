import { Injectable, effect, signal } from '@angular/core';

export type Theme = 'dark' | 'light';
const KEY = 'rasel-theme';

function initial(): Theme {
  try {
    const s = localStorage.getItem(KEY);
    if (s === 'dark' || s === 'light') return s;
  } catch { /* storage blocked */ }
  return typeof matchMedia === 'function' && matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
}

/** Only the theme name is persisted; nothing else ever touches storage. */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly theme = signal<Theme>(initial());
  constructor() { effect(() => { document.documentElement.dataset['theme'] = this.theme(); }); }
  toggle() {
    const n: Theme = this.theme() === 'dark' ? 'light' : 'dark';
    this.theme.set(n);
    try { localStorage.setItem(KEY, n); } catch { /* ignore */ }
  }
}
