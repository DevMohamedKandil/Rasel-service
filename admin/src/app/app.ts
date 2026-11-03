import { Component, effect, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { ApiService } from './core/api.service';
import { HealthService } from './core/health.service';
import { I18n } from './core/i18n';
import { SessionStore } from './core/session.store';
import { ThemeService } from './core/theme';
import { ToastService } from './core/toast';
import { Icon } from './ui/icon';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, Icon],
  template: `
    <a class="skip" href="#main" (click)="focusMain($event)">{{ i18n.t('top.skip') }}</a>
    @if (api.loggedIn()) {
      <div class="shell" [class.open]="menu()">
        <aside class="side" id="side-nav">
          <a class="brand" routerLink="/dashboard">
            <span class="brand-mark"><app-icon name="chat" /></span>
            <span><b>{{ i18n.t('app.name') }}</b><small>{{ i18n.t('app.tag') }}</small></span>
          </a>
          <nav class="nav" [attr.aria-label]="i18n.t('top.menu')">
            @for (n of nav; track n.path) {
              <a [routerLink]="n.path" routerLinkActive="on" ariaCurrentWhenActive="page"><app-icon [name]="n.icon" />{{ i18n.t(n.key) }}</a>
            }
          </nav>
          <div class="side-foot">{{ i18n.t('app.version') }}</div>
        </aside>
        <button type="button" class="backdrop" tabindex="-1" aria-hidden="true" (click)="menu.set(false)"></button>
        <div class="main-col">
          <header class="top">
            <button type="button" class="icon-btn menu-btn" [attr.aria-label]="i18n.t('top.menu')" [attr.aria-expanded]="menu()" aria-controls="side-nav" (click)="menu.set(!menu())"><app-icon name="menu" /></button>
            <span class="grow"></span>
            <span class="status-pill" role="status"><span class="dot" [class]="health.overall()"></span><span class="txt">{{ i18n.t('status.' + health.overall()) }}</span></span>
            <button type="button" class="icon-btn" [attr.aria-label]="i18n.t('top.theme')" [title]="i18n.t('top.theme')" (click)="theme.toggle()"><app-icon [name]="theme.theme() === 'dark' ? 'sun' : 'moon'" /></button>
            <button type="button" class="icon-btn" (click)="i18n.toggle()"><app-icon name="globe" /><span class="txt">{{ i18n.t('top.lang') }}</span></button>
            <button type="button" class="icon-btn" [attr.aria-label]="i18n.t('top.logout')" [title]="i18n.t('top.logout')" (click)="logout()"><app-icon name="logout" /><span class="txt">{{ i18n.t('top.logout') }}</span></button>
          </header>
          <main id="main" tabindex="-1"><router-outlet /></main>
        </div>
      </div>
    } @else {
      <main id="main" tabindex="-1"><router-outlet /></main>
    }
    <div class="toasts" aria-live="polite">
      @for (t of toast.items(); track t.id) {
        <div class="toast" [class.success]="t.kind === 'success'" [attr.role]="t.kind === 'error' ? 'alert' : 'status'">
          <span class="msg">{{ t.text }}</span>
          <button type="button" [attr.aria-label]="i18n.t('toast.dismiss')" (click)="toast.dismiss(t.id)"><app-icon name="close" /></button>
        </div>
      }
    </div>
  `,
})
export class App {
  protected readonly api = inject(ApiService);
  protected readonly i18n = inject(I18n);
  protected readonly theme = inject(ThemeService);
  protected readonly toast = inject(ToastService);
  protected readonly health = inject(HealthService);
  private readonly store = inject(SessionStore);
  private readonly router = inject(Router);
  protected readonly menu = signal(false);
  protected readonly nav = [
    { path: '/dashboard', key: 'nav.dashboard', icon: 'home' },
    { path: '/sessions', key: 'nav.sessions', icon: 'layers' },
    { path: '/messages', key: 'nav.messages', icon: 'chat' },
    { path: '/health', key: 'nav.health', icon: 'pulse' },
    { path: '/help', key: 'nav.help', icon: 'help' },
  ];

  constructor() {
    effect(() => { if (this.api.loggedIn()) this.health.start(); else this.health.stop(); });
    this.router.events.pipe(filter((e) => e instanceof NavigationEnd)).subscribe(() => this.menu.set(false));
  }
  protected focusMain(e: Event) { e.preventDefault(); document.getElementById('main')?.focus(); }
  protected logout() { this.api.logout(); this.store.clear(); this.router.navigateByUrl('/login'); }
}
