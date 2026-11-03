import { Component, OnDestroy, OnInit, effect, inject, signal } from '@angular/core';
import { map } from 'rxjs';
import { PhoneReveal } from '../core/phone-reveal';
import { FormsModule } from '@angular/forms';
import { ApiService, SessionDto } from '../core/api.service';
import { I18n } from '../core/i18n';
import { QrView, parseQr } from '../core/qr';
import { SessionStore } from '../core/session.store';
import { ToastService } from '../core/toast';
import { EngineService } from '../core/engine.service';
import { Confirm } from '../ui/confirm';
import { Hint } from '../ui/hint';
import { Icon } from '../ui/icon';
import { Illustration } from '../ui/illustration';

export type SessionAction = 'start' | 'stop' | 'restart' | 'logout' | 'delete';

@Component({
  selector: 'app-sessions',
  imports: [FormsModule, Icon, Illustration, Hint, Confirm],
  template: `
    <div class="page">
      <div class="page-head"><div><h1>{{ i18n.t('ses.title') }}</h1><p>{{ i18n.t('ses.sub') }}</p></div></div>
      <form class="card form-row" (ngSubmit)="create()">
        <div class="field">
          <label for="sname">{{ i18n.t('ses.name') }} <app-hint key="hint.session" /></label>
          <input id="sname" class="input" name="name" [(ngModel)]="name" autocomplete="off" required />
        </div>
        <button type="submit" class="btn" [disabled]="creating()"><app-icon name="plus" />{{ i18n.t('ses.create') }}</button>
        <app-hint key="hint.addNumber" />
      </form>
      @if (engine.status() && !engine.isAdmin()) {
        <div class="notice" style="margin: var(--s-4) 0" data-testid="std-note"><app-icon name="lock" /><span>{{ i18n.t('ses.stdNote') }}</span></div>
      }
      @if (engine.aggregate(); as agg) {
        <div class="notice" style="margin: var(--s-4) 0" data-testid="engine-banner" role="status">
          <app-icon name="pulse" /><span><b>{{ i18n.t('ses.engine') }}:</b> {{ i18n.t('eng.' + agg) }}
          @if (engine.transitional()) { <span class="dot ok" style="margin-inline-start: .4rem"></span> }</span>
          <app-hint [key]="'hint.eng.' + agg" />
        </div>
      }
      <div class="notice" style="margin: var(--s-4) 0"><app-icon name="alert" /><span>{{ i18n.t('ses.note') }}</span></div>

      @if (creating()) { <div class="card" style="margin-bottom: var(--s-4)"><span class="skel" style="height: 4rem"></span></div> }
      @if (store.sessions().length === 0 && !creating()) {
        <div class="empty"><app-illustration kind="phone" /><h3>{{ i18n.t('ses.empty') }}</h3><p>{{ i18n.t('ses.emptyd') }}</p></div>
      }
      <div class="grid sess-grid">
        @for (s of store.sessions(); track s.id) {
          <div class="card sess">
            <div class="sess-head">
              <div><span class="sess-name">{{ s.name }}</span><code class="sess-id">{{ s.id }}</code></div>
              <span class="row" style="gap: .25rem"><span class="badge" [class]="tone(s.status)">{{ stLabel(s.status) }}</span><app-hint [key]="'hint.st.' + s.status" /></span>
            </div>
            @if (engine.stateFor(s.id); as es) {
              <p class="engine-line" data-testid="engine-state">
                <span class="muted">{{ i18n.t('ses.engine') }}:</span> {{ i18n.t('eng.' + es) }} <app-hint [key]="'hint.eng.' + es" />
              </p>
              @if (engine.sessionEngine(s.id); as d) {
                @if (d.step || d.reason) {
                  <p class="muted" style="font-size: var(--t-sm); margin: 0" data-testid="engine-detail">{{ d.reason || d.step }} <app-hint key="hint.detail" /></p>
                }
              }
            }
            <div class="row wrap">
              <button type="button" class="btn ghost sm" (click)="refresh(s)"><app-icon name="refresh" />{{ i18n.t('ses.status') }}</button>
              <button type="button" class="btn sm" (click)="showQr(s)"><app-icon name="qr" />{{ i18n.t('ses.qr') }}</button>
            </div>
            <div class="row wrap acts" data-testid="session-actions">
              @for (a of actions; track a.id) {
                <span class="act">
                  <button type="button" class="btn ghost sm" [class.danger-ghost]="a.id === 'delete' || a.id === 'logout'" [attr.data-testid]="'act-' + a.id"
                    [disabled]="!allowed(a.id, s)" [title]="disabledWhy(a.id, s)" (click)="ask(a.id, s)"><app-icon [name]="a.icon" />{{ i18n.t('ses.' + a.id) }}</button>
                  <app-hint [key]="'hint.act.' + a.id" />
                </span>
              }
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
    @if (pending(); as p) {
      <app-confirm [heading]="i18n.t('dlg.' + p.action + '.title')" [message]="i18n.t('dlg.' + p.action + '.body')"
        [confirmLabel]="i18n.t('ses.' + p.action)" [danger]="p.action === 'delete' || p.action === 'logout'" [busy]="busy()"
        (confirmed)="run()" (cancelled)="cancelAsk()">
        @if (p.action === 'delete') {
          <label class="row" style="gap: .5rem; align-items: center"><input type="checkbox" name="purge" [checked]="purge()" (change)="purge.set($any($event.target).checked)" data-testid="purge-box" />
            {{ i18n.t('dlg.delete.purge') }} <app-hint key="dlg.delete.purgeHint" /></label>
        }
      </app-confirm>
    }
  `,
  styles: `
    .engine-line { margin: 0 0 var(--s-2); }
    .sess-grid { grid-template-columns: repeat(auto-fit, minmax(min(26rem, 100%), 1fr)); }
    .acts { display: grid; grid-template-columns: repeat(auto-fill, minmax(11rem, 1fr)); gap: var(--s-2); margin-top: var(--s-2); }
    .act { display: flex; align-items: center; gap: .15rem; }
    .act .btn { flex: 1; justify-content: center; white-space: nowrap; padding-inline: .6rem; }
    .qr-panel { grid-template-columns: minmax(0, 1fr); justify-items: center; text-align: center; }
    .qr-side ol { text-align: start; }
    .btn.danger-ghost:not(:disabled) { color: var(--c-bad); }
  `,
})
export class SessionsPage implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  protected readonly i18n = inject(I18n);
  protected readonly store = inject(SessionStore);
  protected readonly engine = inject(EngineService);
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

  readonly actions: { id: SessionAction; icon: string }[] = [
    { id: 'start', icon: 'send' }, { id: 'stop', icon: 'close' }, { id: 'restart', icon: 'refresh' },
    { id: 'logout', icon: 'logout' }, { id: 'delete', icon: 'alert' },
  ];
  readonly pending = signal<{ action: SessionAction; s: SessionDto } | null>(null);
  readonly purge = signal(false);
  readonly busy = signal(false);

  ngOnInit() { this.engine.start(); }
  ngOnDestroy() { this.stopPoll(); this.reveal.hide(); this.engine.stop(); }

  /** UI hint only: the server enforces the scope and the state rules. */
  allowed(a: SessionAction, s: SessionDto) {
    if (!this.engine.isAdmin()) return false;
    return a !== 'logout' || s.status === 'Connected';
  }
  disabledWhy(a: SessionAction, s: SessionDto) {
    if (!this.engine.isAdmin()) return this.i18n.t('ses.adminOnly');
    return a === 'logout' && s.status !== 'Connected' ? this.i18n.t('ses.needConnected') : '';
  }
  ask(a: SessionAction, s: SessionDto) { if (!this.allowed(a, s)) return; this.purge.set(false); this.pending.set({ action: a, s }); }
  cancelAsk() { if (!this.busy()) this.pending.set(null); }
  run() {
    const p = this.pending();
    if (!p || this.busy()) return;
    const { action, s } = p;
    this.busy.set(true);
    const done = () => { this.busy.set(false); this.pending.set(null); };
    const fail = (e: unknown) => { done(); this.toast.error(e); };
    if (action === 'logout') {
      this.api.logoutSession(s.id).subscribe({
        next: (n) => { done(); this.store.update(s.id, n); this.toast.success('ses.done.logout'); this.engine.boost(); this.showQr({ ...s, ...n }); },
        error: fail,
      });
    } else if (action === 'delete') {
      this.api.deleteSession(s.id, this.purge()).subscribe({
        next: () => { done(); if (this.qrFor() === s.id) this.closeQr(); this.store.remove(s.id); this.toast.success('ses.done.delete'); this.engine.boost(); },
        error: fail,
      });
    } else {
      this.api.engine(action, s.id).subscribe({
        next: () => { done(); this.toast.success('ses.done.' + action); this.engine.boost(); this.refresh(s); },
        error: fail,
      });
    }
  }
  private stopPoll() { if (this.poll) { clearInterval(this.poll); this.poll = null; } }

  create() {
    this.creating.set(true);
    this.api.createSession(this.name).subscribe({
      next: (s) => { this.store.add(s); this.creating.set(false); this.toast.success('ses.created'); this.engine.boost(); this.showQr(s); },
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
