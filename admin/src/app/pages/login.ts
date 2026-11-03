import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ApiService } from '../core/api.service';
import { I18n } from '../core/i18n';
import { ThemeService } from '../core/theme';
import { ToastService } from '../core/toast';
import { Hint } from '../ui/hint';
import { Icon } from '../ui/icon';

@Component({
  selector: 'app-login',
  imports: [FormsModule, Icon, Hint],
  template: `
    <div class="auth">
      <section class="hero">
        <div class="brand">
          <span class="brand-mark"><app-icon name="chat" /></span>
          <span><b>{{ i18n.t('app.name') }}</b><small>{{ i18n.t('app.tag') }}</small></span>
        </div>
        <h1>{{ i18n.t('login.hero1') }}</h1>
        <p>{{ i18n.t('login.hero2') }}</p>
        <ul>
          <li><app-icon name="qr" />{{ i18n.t('login.f1') }}</li>
          <li><app-icon name="refresh" />{{ i18n.t('login.f2') }}</li>
          <li><app-icon name="shield" />{{ i18n.t('login.f3') }}</li>
        </ul>
      </section>
      <section class="auth-pane">
        <div class="auth-tools">
          <button type="button" class="icon-btn" [attr.aria-label]="i18n.t('top.theme')" (click)="theme.toggle()"><app-icon [name]="theme.theme() === 'dark' ? 'sun' : 'moon'" /></button>
          <button type="button" class="icon-btn" (click)="i18n.toggle()"><app-icon name="globe" /><span class="txt">{{ i18n.t('top.lang') }}</span></button>
        </div>
        <div class="card auth-card">
          <div>
            <h1>{{ i18n.t('login.title') }}</h1>
            <p class="muted">{{ i18n.t('login.sub') }}</p>
          </div>
          <form (ngSubmit)="submit()">
            <div class="field">
              <label for="base">{{ i18n.t('login.base') }} <app-hint key="hint.base" /></label>
              <input id="base" class="input ltr" name="base" [(ngModel)]="base" [placeholder]="i18n.t('login.basePh')" autocomplete="off" required />
            </div>
            <div class="field">
              <label for="key">{{ i18n.t('login.key') }} <app-hint key="hint.key" /></label>
              <input id="key" class="input ltr" name="key" type="password" [(ngModel)]="key" autocomplete="off" required />
            </div>
            @if (error()) { <div class="notice bad" role="alert"><app-icon name="alert" /><span>{{ error() }}</span></div> }
            <button type="submit" class="btn" [disabled]="busy()"><app-icon name="lock" />{{ busy() ? i18n.t('login.busy') : i18n.t('login.submit') }}</button>
          </form>
          <p class="faint" style="font-size: var(--t-xs)">{{ i18n.t('login.hint') }}</p>
        </div>
      </section>
    </div>
  `,
})
export class LoginPage {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  protected readonly i18n = inject(I18n);
  protected readonly theme = inject(ThemeService);
  base = '';
  key = '';
  readonly busy = signal(false);
  readonly error = signal('');

  submit() {
    this.error.set('');
    this.busy.set(true);
    this.api.login(this.base, this.key);
    // Verify the key with a cheap authenticated call (unknown session id: 404 means the key was accepted).
    this.api.session('00000000-0000-0000-0000-000000000000').subscribe({
      next: () => this.done(),
      error: (e) => {
        if (e?.status === 404) return this.done();
        this.api.logout();
        this.busy.set(false);
        if (e?.status === 401) this.error.set(this.i18n.t('login.bad'));
        else { this.error.set(''); this.toast.error(e); }
      },
    });
  }
  private done() { this.key = ''; this.busy.set(false); this.router.navigateByUrl('/dashboard'); }
}
