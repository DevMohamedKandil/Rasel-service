import { Component, OnDestroy, effect, inject, signal } from '@angular/core';
import { map } from 'rxjs';
import { PhoneReveal } from '../core/phone-reveal';
import { FormsModule } from '@angular/forms';
import { ApiService, SessionDto } from '../core/api.service';
import { I18n } from '../core/i18n';
import { QrView, parseQr } from '../core/qr';
import { SessionStore } from '../core/session.store';
import { ToastService } from '../core/toast';
import { Hint } from '../ui/hint';
import { Icon } from '../ui/icon';
import { Illustration } from '../ui/illustration';

@Component({
  selector: 'app-sessions',
  imports: [FormsModule, Icon, Illustration, Hint],
  template: `
    <div class="page">
      <div class="page-head"><div><h1>{{ i18n.t('ses.title') }}</h1><p>{{ i18n.t('ses.sub') }}</p></div></div>
      <form class="card form-row" (ngSubmit)="create()">
        <div class="field">
          <label for="sname">{{ i18n.t('ses.name') }} <app-hint key="hint.session" /></label>
          <input id="sname" class="input" name="name" [(ngModel)]="name" autocomplete="off" required />
        </div>
        <button type="submit" class="btn" [disabled]="creating()"><app-icon name="plus" />{{ i18n.t('ses.create') }}</button>
      </form>
      <div class="notice" style="margin: var(--s-4) 0"><app-icon name="alert" /><span>{{ i18n.t('ses.note') }}</span></div>

      @if (creating()) { <div class="card" style="margin-bottom: var(--s-4)"><span class="skel" style="height: 4rem"></span></div> }
      @if (store.sessions().length === 0 && !creating()) {
        <div class="empty"><app-illustration kind="phone" /><h3>{{ i18n.t('ses.empty') }}</h3><p>{{ i18n.t('ses.emptyd') }}</p></div>
      }
      <div class="grid cols-2">
        @for (s of store.sessions(); track s.id) {
          <div class="card sess">
            <div class="sess-head">
              <div><span class="sess-name">{{ s.name }}</span><code class="sess-id">{{ s.id }}</code></div>
              <span class="row" style="gap: .25rem"><span class="badge" [class]="tone(s.status)">{{ stLabel(s.status) }}</span><app-hint [key]="'hint.st.' + s.status" /></span>
            </div>
            <div class="row wrap">
              <button type="button" class="btn ghost sm" (click)="refresh(s)"><app-icon name="refresh" />{{ i18n.t('ses.status') }}</button>
              <button type="button" class="btn sm" (click)="showQr(s)"><app-icon name="qr" />{{ i18n.t('ses.qr') }}</button>
            </div>
            @if (qrFor() === s.id) {
              <div class="qr-panel">
                <div class="qr-frame">
                  @if (qr().kind === 'image') { <img [src]="imageSrc()" [alt]="i18n.t('ses.qrtitle')" width="240" height="240" /> }
                  @else if (qr().kind === 'text') { <div class="qr-text"><div><small>{{ i18n.t('ses.qrtext') }}</small><br><code>{{ textQr() }}</code></div></div> }
                  @else if (qrNote() === 'ses.connectedNote') { <div class="qr-text"><span style="font-size: 5rem; line-height: 1; color: var(--ok, #25d366)" aria-hidden="true">✓</span></div> }
                  @else { <div class="qr-text"><span class="skel" style="width: 12rem; height: 12rem"></span></div> }
                </div>
                <div class="qr-side">
                  <h3>{{ i18n.t('ses.qrtitle') }} <app-hint key="hint.qr" /></h3>
                  @if (qrNote() !== 'ses.connectedNote') { <ol><li>{{ i18n.t('ses.qrsteps') }}</li></ol> }
                  @if (qrNote()) { <p class="muted" style="font-size: var(--t-sm)" role="status">{{ i18n.t(qrNote()) }}</p> }
                  @if (qrNote() !== 'ses.connectedNote') { <p class="live"><span class="dot ok"></span>{{ i18n.t('ses.qrauto') }}</p> }
                  @if (qrNote() === 'ses.connectedNote' && maskedFor(s)) {
                    <p class="row" style="gap: .5rem; margin: var(--s-2) 0">
                      <span class="muted">{{ i18n.t('ses.phone') }}:</span>
                      <bdi dir="ltr"><code data-testid="linked-phone">{{ reveal.revealed() ? reveal.full() : maskedFor(s) }}</code></bdi>
                      <button type="button" class="btn ghost sm" [attr.aria-pressed]="reveal.revealed()" [disabled]="reveal.loading()"
                        [attr.aria-label]="i18n.t(reveal.revealed() ? 'ses.phoneHide' : 'ses.phoneShow')" [title]="i18n.t(reveal.revealed() ? 'ses.phoneHide' : 'ses.phoneShow')"
                        (click)="toggleReveal(s)"><app-icon [name]="reveal.revealed() ? 'eyeoff' : 'eye'" /></button>
                    </p>
                    <p class="muted" style="font-size: var(--t-sm)">{{ i18n.t('ses.phoneHint') }}</p>
                  }
                  @if (qrNote() === 'ses.connectedNote') {
                    <details class="muted" style="font-size: var(--t-sm); margin-top: var(--s-2)">
                      <summary>{{ i18n.t('ses.disconnectTitle') }}</summary>
                      <p>{{ i18n.t('ses.disconnectHow') }}</p>
                    </details>
                    <details class="muted" style="font-size: var(--t-sm); margin-top: var(--s-2)">
                      <summary>{{ i18n.t('ses.addNumberTitle') }}</summary>
                      <p>{{ i18n.t('ses.addNumberHow') }}</p>
                    </details>
                  }
                  <div class="row wrap" style="margin-top: var(--s-3)">
                    <button type="button" class="btn ghost sm" (click)="showQr(s)"><app-icon name="refresh" />{{ i18n.t('ses.qrrefresh') }}</button>
                    <button type="button" class="btn ghost sm" (click)="closeQr()"><app-icon name="close" />{{ i18n.t('ses.close') }}</button>
                  </div>
                </div>
              </div>
            }
          </div>
        }
      </div>
    </div>
  `,
})
export class SessionsPage implements OnDestroy {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  protected readonly i18n = inject(I18n);
  protected readonly store = inject(SessionStore);
  name = 'main';
  readonly creating = signal(false);
  readonly qr = signal<QrView>({ kind: 'none' });
  readonly qrNote = signal('');
  readonly qrFor = signal('');
  private poll: ReturnType<typeof setInterval> | null = null;
  readonly reveal = new PhoneReveal((id) => this.api.linkedNumber(id).pipe(map((r) => r.phone)));

  constructor() {
    // Logout (key cleared) must not leave a revealed number in memory.
    effect(() => { if (!this.api.loggedIn()) this.reveal.hide(); });
  }
  maskedFor(s: SessionDto) { return s.linkedPhoneMasked ?? ''; }
  toggleReveal(s: SessionDto) { this.reveal.toggle(s.id, (e) => this.toast.error(e)); }

  imageSrc() { const q = this.qr(); return q.kind === 'image' ? q.src : ''; }
  textQr() { const q = this.qr(); return q.kind === 'text' ? q.text : ''; }
  stLabel(s: string) { const k = 'st.' + s; const t = this.i18n.t(k); return t === k ? s : t; }
  tone(s: string) { return s === 'Connected' ? 'ok' : s === 'Failed' ? 'bad' : s === 'NeedsQr' ? 'warn' : s === 'Starting' ? 'info' : ''; }

  ngOnDestroy() { this.stopPoll(); this.reveal.hide(); }
  private stopPoll() { if (this.poll) { clearInterval(this.poll); this.poll = null; } }

  create() {
    this.creating.set(true);
    this.api.createSession(this.name).subscribe({
      next: (s) => { this.store.add(s); this.creating.set(false); this.toast.success('ses.created'); },
      error: (e) => { this.creating.set(false); this.toast.error(e); },
    });
  }
  refresh(s: SessionDto) {
    this.api.session(s.id).subscribe({
      next: (n) => this.store.update(n.id, n),
      error: (e) => this.toast.error(e),
    });
  }
  closeQr() { this.reveal.hide(); this.stopPoll(); this.qrFor.set(''); this.qr.set({ kind: 'none' }); this.qrNote.set(''); }
  showQr(s: SessionDto) {
    if (this.qrFor() !== s.id) this.reveal.hide();
    this.stopPoll();
    this.qrFor.set(s.id);
    this.qr.set({ kind: 'none' });
    this.qrNote.set('');
    this.fetchQr(s.id);
    this.poll = setInterval(() => this.fetchQr(s.id), 5000);
  }
  private fetchQr(id: string) {
    this.api.qr(id).subscribe({
      next: (r) => {
        if (this.qrFor() !== id) return;
        this.store.update(id, { status: r.status, linkedPhoneMasked: r.linkedPhoneMasked ?? null });
        const view = parseQr(r.qr);
        this.qr.set(view);
        this.qrNote.set(view.kind !== 'none' ? '' : r.status === 'Connected' ? 'ses.connectedNote' : 'ses.noqr');
        if (r.status === 'Connected') this.stopPoll();
      },
      error: (e) => { this.stopPoll(); this.toast.error(e); },
    });
  }
}
