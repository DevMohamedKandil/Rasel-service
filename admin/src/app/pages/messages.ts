import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, MessageStatusDto } from '../core/api.service';
import { I18n } from '../core/i18n';
import { maskPhones } from '../core/mask';
import { errorKey, messageView } from '../core/msg-view';
import { ToastService } from '../core/toast';
import { Hint } from '../ui/hint';
import { Icon } from '../ui/icon';
import { Illustration } from '../ui/illustration';

@Component({
  selector: 'app-messages',
  imports: [FormsModule, Icon, Illustration, Hint],
  template: `
    <div class="page">
      <div class="page-head"><div><h1>{{ i18n.t('msg.title') }}</h1><p>{{ i18n.t('msg.sub') }}</p></div></div>
      <form class="card form-row" (ngSubmit)="lookup()">
        <div class="field">
          <label for="mid">{{ i18n.t('msg.id') }} <app-hint key="hint.msgid" /></label>
          <input id="mid" class="input ltr" name="id" [(ngModel)]="id" placeholder="00000000-0000-0000-0000-000000000000" autocomplete="off" required />
        </div>
        <button type="submit" class="btn" [disabled]="busy()"><app-icon name="search" />{{ i18n.t('msg.go') }}</button>
      </form>
      <p class="faint" style="margin: var(--s-3) 0; font-size: var(--t-sm)"><app-icon name="lock" /> {{ i18n.t('msg.privacy') }} <app-hint key="hint.masked" /></p>
      @if (error()) { <div class="notice bad" role="alert"><app-icon name="alert" /><span>{{ i18n.t(error()) }}</span></div> }

      @if (busy()) {
        <div class="card" style="margin-top: var(--s-4)"><span class="skel" style="height: 3rem; margin-bottom: 1rem"></span><span class="skel" style="height: 5rem"></span></div>
      } @else if (msg(); as m) {
        <div class="card" style="margin-top: var(--s-4)">
          <div class="row wrap" style="justify-content: space-between">
            <code class="muted">{{ m.id }}</code>
            <span class="row" style="gap: .25rem"><span class="badge" [class]="view().tone">{{ i18n.t('msg.t.' + key(m.status)) }}</span><app-hint [key]="'hint.badge.' + key(m.status)" /></span>
          </div>
          <div class="row" style="gap: .25rem; margin-top: var(--s-4)"><b>{{ i18n.t('msg.steps') }}</b><app-hint key="hint.steps" /></div>
          <div class="timeline" role="list">
            @for (s of view().steps; track s.key) {
              <div class="tl-step" role="listitem" [class]="s.state" [attr.aria-label]="i18n.t('msg.t.' + s.key)">
                <span class="tl-dot"><app-icon [name]="s.state === 'bad' ? 'close' : s.state === 'done' ? 'check' : s.key === 'sending' ? 'send' : 'inbox'" /></span>
                <span>{{ i18n.t('msg.t.' + s.key) }}</span>
              </div>
            }
          </div>
          <div class="notice" [class]="view().tone === 'warn' ? 'warn' : view().tone === 'ok' ? 'ok' : view().tone === 'bad' ? 'bad' : ''" role="status">
            <app-icon [name]="view().tone === 'ok' ? 'check' : view().tone === 'info' ? 'inbox' : 'alert'" /><span>{{ i18n.t(view().summaryKey) }}</span>
          </div>
          <dl class="kv" style="margin-top: var(--s-5)">
            <div><dt>{{ i18n.t('msg.attempts') }} <app-hint key="hint.attempts" /></dt><dd>{{ m.attempts }}</dd></div>
            <div><dt>{{ i18n.t('msg.created') }}</dt><dd>{{ i18n.fmt(m.createdAt) }}</dd></div>
            <div><dt>{{ i18n.t('msg.sent') }}</dt><dd>{{ i18n.fmt(m.sentAt) }}</dd></div>
            @if (m.status === 'Queued' && m.nextAttemptAt) { <div><dt>{{ i18n.t('msg.next') }}</dt><dd>{{ i18n.fmt(m.nextAttemptAt) }}</dd></div> }
            <div>
              <dt>{{ i18n.t('msg.err') }} <app-hint key="hint.lasterr" /></dt>
              <dd>@if (errKey(); as k) { {{ i18n.t(k) }} <code class="faint">{{ lastError() }}</code> } @else { {{ i18n.t('msg.noerr') }} }</dd>
            </div>
          </dl>
        </div>
      } @else if (!error()) {
        <div class="empty" style="margin-top: var(--s-4)"><app-illustration kind="message" /><h3>{{ i18n.t('msg.empty') }}</h3><p>{{ i18n.t('msg.emptyd') }}</p></div>
      }
    </div>
  `,
})
export class MessagesPage {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  protected readonly i18n = inject(I18n);
  id = '';
  readonly msg = signal<MessageStatusDto | null>(null);
  readonly error = signal('');
  readonly busy = signal(false);
  readonly view = computed(() => messageView(this.msg()?.status ?? '', this.msg()?.attempts ?? 0));
  readonly errKey = computed(() => errorKey(this.msg()?.lastErrorCode));

  key(s: string) { return s === 'Sent' ? 'sent' : s === 'Failed' ? 'failed' : 'queued'; }
  lastError() { return maskPhones(this.msg()?.lastErrorCode); }

  lookup() {
    this.error.set('');
    this.msg.set(null);
    if (!/^[0-9a-fA-F-]{36}$/.test(this.id.trim())) return this.error.set('msg.invalid');
    this.busy.set(true);
    this.api.message(this.id.trim()).subscribe({
      next: (m) => { this.msg.set(m); this.busy.set(false); },
      error: (e) => {
        this.busy.set(false);
        if (e?.status === 404) this.error.set('msg.notfound'); else this.toast.error(e);
      },
    });
  }
}
