import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { ApiService, MessageStatusDto } from '../core/api.service';
import { EngineService } from '../core/engine.service';
import { I18n } from '../core/i18n';
import { maskPhones } from '../core/mask';
import { errorKey } from '../core/msg-view';
import { SessionStore } from '../core/session.store';
import { ToastService, errorToastKey } from '../core/toast';
import { Hint } from '../ui/hint';
import { Icon } from '../ui/icon';

export const POLL_MS = 2000;
export const POLL_MAX_MS = 60000;
export const MAX_TEXT = 4096;
const SEND_CODES = ['invalid_phone', 'invalid_text', 'invalid_target', 'group_not_found', 'group_ambiguous', 'session_not_found', 'session_not_connected',
  'idempotency_key_reused', 'idempotency_conflict', 'daily_limit_reached', 'recipient_daily_limit', 'rate_limited'];

/** i18n key for a failed send: send-specific codes first, then the shared 409/429/503 texts. */
export function sendErrorKey(e: unknown): string {
  if (e instanceof HttpErrorResponse) {
    const code = e.error && typeof e.error === 'object' && 'code' in e.error ? String((e.error as { code: unknown }).code) : '';
    if (SEND_CODES.includes(code)) return 'snd.err.' + code;
    if (e.status === 503) return 'snd.err.503';
  }
  return errorToastKey(e);
}

export interface SendOption { id: string; label: string; state: string; ok: boolean; }
export interface LastSend { id: string; to: string; kind: 'phone' | 'group'; status: string; at: number; }
interface Current { id: string; replayed: boolean; status: string; attempts: number; lastErrorCode: string | null; }

@Component({
  selector: 'app-send',
  imports: [FormsModule, Icon, Hint],
  template: `
    <div class="page">
      <div class="page-head"><div><h1>{{ i18n.t('snd.title') }}</h1><p>{{ i18n.t('snd.sub') }}</p></div></div>
      <div class="notice" style="margin-bottom: var(--s-4)"><app-icon name="alert" /><span>{{ i18n.t('snd.warn') }}</span></div>
      <div class="send-grid">
        <form class="card send-form" (ngSubmit)="send()" data-testid="send-form">
          <div class="field">
            <label for="ssess">{{ i18n.t('snd.session') }} <app-hint key="snd.hint.session" /></label>
            <select id="ssess" class="input" name="session" [(ngModel)]="sessionId" data-testid="send-session">
              <option value="">{{ i18n.t('snd.sessionDefault') }}</option>
              @for (o of options(); track o.id) {
                <option [value]="o.id" [disabled]="!o.ok">{{ o.label }} ({{ stLabel(o.state) }}{{ o.ok ? '' : ' - ' + i18n.t('snd.notConnected') }})</option>
              }
            </select>
          </div>
          <div class="field">
            <label for="stype">{{ i18n.t('snd.type') }} <app-hint key="snd.hint.type" /></label>
            <select id="stype" class="input" name="type" [(ngModel)]="kind" data-testid="send-type">
              <option value="phone">{{ i18n.t('snd.phone') }}</option>
              <option value="group">{{ i18n.t('snd.group') }}</option>
            </select>
          </div>
          <div class="field">
            <label for="srecip">{{ i18n.t(kind === 'phone' ? 'snd.recipPhone' : 'snd.recipGroup') }} <app-hint [key]="kind === 'phone' ? 'snd.hint.phone' : 'snd.hint.group'" /></label>
            <input id="srecip" class="input" [class.ltr]="kind === 'phone'" name="recipient" [(ngModel)]="recipient" autocomplete="off" required
              [attr.maxlength]="kind === 'group' ? 128 : null" [placeholder]="kind === 'phone' ? '+201001234567' : ''" data-testid="send-recipient" />
            @if (kind === 'phone') { <small>{{ i18n.t('snd.phoneNote') }}</small> }
          </div>
          <div class="field">
            <label for="stext">{{ i18n.t('snd.text') }} <app-hint key="snd.hint.text" /></label>
            <textarea id="stext" class="input" name="text" rows="5" [(ngModel)]="text" [attr.maxlength]="max" required data-testid="send-text"></textarea>
            <small class="muted" data-testid="send-count">{{ text.length }} / {{ max }} {{ i18n.t('snd.chars') }}</small>
          </div>
          <div class="row"><button type="submit" class="btn" [disabled]="!canSend()" data-testid="send-btn"><app-icon name="send" />{{ i18n.t(sending() ? 'snd.sending' : 'snd.send') }}</button></div>
          @if (error()) { <div class="notice" role="alert" data-testid="send-error"><app-icon name="alert" /><span>{{ i18n.t(error()) }}</span></div> }
        </form>

        <div class="col">
          @if (current(); as c) {
            <div class="card" data-testid="send-result" role="status">
              <h3 style="margin-top: 0">{{ i18n.t('snd.accepted') }}</h3>
              <p class="muted" style="margin: 0 0 var(--s-2)">{{ i18n.t('snd.msgId') }}: <bdi dir="ltr"><code data-testid="send-msgid">{{ c.id }}</code></bdi></p>
              @if (c.replayed) { <p class="muted">{{ i18n.t('snd.replayed') }}</p> }
              <p class="row" style="gap: .5rem; margin: 0">{{ i18n.t('snd.status') }}: <span class="badge" [class]="tone(c.status)" data-testid="send-status">{{ stKey(c.status) }}</span>
                @if (polling()) { <span class="dot ok"></span> }</p>
              <p class="muted" style="margin: var(--s-2) 0 0">{{ i18n.t('snd.attempts') }}: <span data-testid="send-attempts">{{ c.attempts }}</span></p>
              @if (reasonKey(); as rk) { <p data-testid="send-reason" style="margin: var(--s-2) 0 0"><b>{{ i18n.t('snd.reason') }}:</b> {{ i18n.t(rk) }}</p> }
              @if (pollNote()) { <p class="muted" style="font-size: var(--t-sm)" data-testid="send-pollnote">{{ i18n.t(pollNote()) }}</p> }
            </div>
          }
          <div class="card">
            <h3 style="margin-top: 0">{{ i18n.t('snd.last') }}</h3>
            <p class="muted" style="font-size: var(--t-sm)">{{ i18n.t('snd.lastNote') }}</p>
            @if (last().length === 0) { <p class="muted">{{ i18n.t('snd.noLast') }}</p> }
            <ul class="last" data-testid="send-last">
              @for (l of last(); track l.id) {
                <li><bdi dir="ltr">{{ l.to }}</bdi> <span class="badge" [class]="tone(l.status)">{{ stKey(l.status) }}</span> <code class="sess-id">{{ l.id.slice(0, 8) }}</code></li>
              }
            </ul>
          </div>
        </div>
      </div>
    </div>
  `,
  styles: `
    .send-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(24rem, 100%), 1fr)); gap: var(--s-4); align-items: start; }
    .send-form { display: flex; flex-direction: column; gap: var(--s-4); }
    .col { display: flex; flex-direction: column; gap: var(--s-4); min-width: 0; }
    textarea.input { resize: vertical; min-height: 7rem; font: inherit; }
    .last { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: var(--s-2); }
    .last li { display: flex; flex-wrap: wrap; gap: var(--s-2); align-items: center; }
    code { overflow-wrap: anywhere; }
  `,
})
export class SendPage implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly store = inject(SessionStore);
  protected readonly engine = inject(EngineService);
  protected readonly i18n = inject(I18n);
  protected readonly max = MAX_TEXT;

  sessionId = '';
  kind: 'phone' | 'group' = 'phone';
  recipient = '';
  text = '';
  readonly sending = signal(false);
  readonly error = signal('');
  readonly current = signal<Current | null>(null);
  readonly polling = signal(false);
  readonly pollNote = signal('');
  readonly last = signal<LastSend[]>([]);
  private timer: ReturnType<typeof setTimeout> | null = null;
  private pollGen = 0;

  /** Sessions from GET /v1/system/status (Admin keys); Standard keys get an empty list there, so fall back to this tab's known sessions. */
  readonly options = computed<SendOption[]>(() => {
    const names = new Map(this.store.sessions().map((s) => [s.id, s.name]));
    const eng = this.engine.status()?.sessions ?? [];
    const rows = eng.length
      ? eng.map((e) => ({ id: e.sessionId, state: e.state, label: names.get(e.sessionId) ?? e.sessionId.slice(0, 8) }))
      : this.store.sessions().map((s) => ({ id: s.id, state: s.status, label: s.name }));
    return rows.map((r) => ({ ...r, ok: r.state === 'Connected' }));
  });
  readonly reasonKey = computed(() => {
    const c = this.current();
    return c ? errorKey(c.lastErrorCode) : null;
  });
  canSend() { return !this.sending() && this.recipient.trim().length > 0 && this.text.trim().length > 0 && this.text.length <= MAX_TEXT; }

  ngOnInit() { this.engine.start(); }
  ngOnDestroy() { this.stopPoll(); this.engine.stop(); }

  stLabel(s: string) { const k = 'st.' + s; const t = this.i18n.t(k); return t === k ? s : t; }
  stKey(s: string) { const k = 'snd.st.' + s; const t = this.i18n.t(k); return t === k ? s : t; }
  tone(s: string) { return s === 'Sent' ? 'ok' : s === 'Failed' ? 'bad' : 'info'; }

  send() {
    if (!this.canSend()) return;
    const body = {
      ...(this.sessionId ? { sessionId: this.sessionId } : {}),
      ...(this.kind === 'phone' ? { phone: this.recipient.trim() } : { groupName: this.recipient.trim() }),
      text: this.text,
    };
    const to = this.kind === 'phone' ? maskPhones(this.recipient.trim()) : this.recipient.trim();
    const kind = this.kind;
    this.sending.set(true);
    this.error.set('');
    this.api.sendMessage(body, crypto.randomUUID()).subscribe({
      next: (r) => {
        this.sending.set(false);
        this.current.set({ id: r.messageId, replayed: r.replayed, status: r.status, attempts: 0, lastErrorCode: null });
        this.last.update((l) => [{ id: r.messageId, to, kind, status: r.status, at: Date.now() }, ...l].slice(0, 10));
        this.text = '';
        this.startPoll(r.messageId);
      },
      error: (e) => { this.sending.set(false); this.error.set(sendErrorKey(e)); this.toast.error(e); },
    });
  }

  private stopPoll() { this.pollGen++; if (this.timer) { clearTimeout(this.timer); this.timer = null; } this.polling.set(false); }
  private startPoll(id: string) {
    this.stopPoll();
    const gen = this.pollGen;
    const t0 = Date.now();
    this.polling.set(true);
    this.pollNote.set('');
    const tick = () => {
      this.api.message(id).subscribe({
        next: (m) => { if (gen === this.pollGen) this.apply(m, gen, t0, tick); },
        error: () => { if (gen === this.pollGen) { this.pollNote.set('snd.pollErr'); this.next(t0, tick); } },
      });
    };
    this.timer = setTimeout(tick, POLL_MS);
  }
  private apply(m: MessageStatusDto, gen: number, t0: number, tick: () => void) {
    this.pollNote.set('');
    this.current.update((c) => (c && c.id === m.id ? { ...c, status: m.status, attempts: m.attempts, lastErrorCode: m.lastErrorCode } : c));
    this.last.update((l) => l.map((x) => (x.id === m.id ? { ...x, status: m.status } : x)));
    if (m.status === 'Sent' || m.status === 'Failed') { this.stopPoll(); return; }
    this.next(t0, tick);
  }
  private next(t0: number, tick: () => void) {
    if (Date.now() - t0 >= POLL_MAX_MS) { this.stopPoll(); this.pollNote.set('snd.pollStop'); return; }
    this.timer = setTimeout(tick, POLL_MS);
  }
}
