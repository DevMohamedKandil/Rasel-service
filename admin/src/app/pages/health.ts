import { Component, OnInit, inject } from '@angular/core';
import { HealthService } from '../core/health.service';
import { I18n } from '../core/i18n';
import { Hint } from '../ui/hint';
import { Icon } from '../ui/icon';

@Component({
  selector: 'app-health',
  imports: [Icon, Hint],
  template: `
    <div class="page">
      <div class="page-head">
        <div><h1>{{ i18n.t('hl.title') }} <app-hint key="hint.health" /></h1><p>{{ i18n.t('hl.sub') }}</p></div>
        <button type="button" class="btn ghost" [disabled]="health.loading()" (click)="health.refresh()"><app-icon name="refresh" />{{ i18n.t('hl.refresh') }}</button>
      </div>
      @if (health.overall() === 'down') {
        <div class="notice bad" role="alert"><app-icon name="alert" /><span>{{ i18n.t('status.down') }}</span></div>
      } @else if (health.data(); as h) {
        <div class="card row" style="margin-bottom: var(--s-4)">
          <span class="ico-chip" [class]="health.overall() === 'ok' ? 'ok' : 'bad'"><app-icon [name]="health.overall() === 'ok' ? 'check' : 'alert'" /></span>
          <span><span class="muted" style="font-size: var(--t-sm)">{{ i18n.t('hl.overall') }}</span><br><b style="font-size: var(--t-lg)">{{ i18n.t('status.' + health.overall()) }}</b></span>
        </div>
        <div class="grid cols-2">
          @for (c of h.checks; track c.name) {
            <div class="card hc">
              <span class="ico-chip" [class]="ok(c.status) ? 'ok' : 'bad'"><app-icon [name]="icon(c.name)" /></span>
              <span class="meta"><b>{{ label(c.name) }} <app-hint [key]="hintKey(c.name)" /></b><span>{{ detail(c.detail) }}</span></span>
              <span class="badge" [class]="ok(c.status) ? 'ok' : 'bad'">{{ ok(c.status) ? i18n.t('hl.healthy') : i18n.t('hl.unhealthy') }}</span>
            </div>
          }
        </div>
      } @else {
        <div class="grid cols-2">
          @for (n of [1, 2]; track n) { <div class="card"><span class="skel" style="height: 3rem"></span></div> }
        </div>
      }
    </div>
  `,
})
export class HealthPage implements OnInit {
  protected readonly health = inject(HealthService);
  protected readonly i18n = inject(I18n);
  ngOnInit() { this.health.refresh(); }
  hintKey(n: string) { return n === 'database' || n === 'outbox' ? 'hint.hc.' + n : 'hint.health'; }
  detail(d: string | null) { if (!d) return ''; const k = 'hl.d.' + d; const t = this.i18n.t(k); return t === k ? '' : t; }
  ok(s: string) { return s.toLowerCase() === 'healthy'; }
  icon(n: string) { return n === 'database' ? 'db' : n === 'outbox' ? 'inbox' : 'pulse'; }
  label(n: string) { const k = 'hl.' + n; const t = this.i18n.t(k); return t === k ? n : t; }
}
