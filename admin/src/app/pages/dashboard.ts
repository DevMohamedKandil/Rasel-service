import { Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HealthService } from '../core/health.service';
import { I18n } from '../core/i18n';
import { SessionStore } from '../core/session.store';
import { Hint } from '../ui/hint';
import { Icon } from '../ui/icon';

@Component({
  selector: 'app-dashboard',
  imports: [RouterLink, Icon, Hint],
  template: `
    <div class="page">
      <div class="page-head"><div><h1>{{ i18n.t('dash.title') }}</h1><p>{{ i18n.t('dash.sub') }}</p></div></div>

      <div class="grid cols-3">
        <div class="card stat">
          <span class="label"><span class="ico-chip" [class]="chip()"><app-icon name="pulse" /></span>{{ i18n.t('dash.service') }} <app-hint key="hint.health" /></span>
          @if (health.overall() === 'unknown') { <span class="skel" style="height: 2.2rem; width: 70%"></span> }
          @else { <span class="value sm">{{ i18n.t('status.' + health.overall()) }}</span> }
        </div>
        <div class="card stat">
          <span class="label"><span class="ico-chip info"><app-icon name="layers" /></span>{{ i18n.t('dash.sessions') }} <app-hint key="hint.sessionsCount" /></span>
          <span class="value">{{ store.sessions().length }}</span>
          <span class="sub">{{ connected() }} {{ i18n.t('dash.connected') }} · {{ needsQr() }} {{ i18n.t('dash.needs') }}</span>
        </div>
        <div class="card stat">
          <span class="label"><span class="ico-chip ok"><app-icon name="check" /></span>{{ i18n.t('dash.checks') }} <app-hint key="hint.checks" /></span>
          @if (health.data(); as h) { <span class="value">{{ healthy() }} / {{ h.checks.length }}</span> }
          @else if (health.overall() === 'down') { <span class="value">-</span> }
          @else { <span class="skel" style="height: 2.2rem; width: 50%"></span> }
        </div>
      </div>

      <h2 class="section-title">{{ i18n.t('dash.quick') }}</h2>
      <div class="grid cols-3">
        <a class="card row" routerLink="/sessions"><span class="ico-chip"><app-icon name="plus" /></span><span><b>{{ i18n.t('dash.q1') }}</b><br><span class="muted" style="font-size: var(--t-sm)">{{ i18n.t('dash.q1d') }}</span></span></a>
        <a class="card row" routerLink="/messages"><span class="ico-chip info"><app-icon name="search" /></span><span><b>{{ i18n.t('dash.q2') }}</b><br><span class="muted" style="font-size: var(--t-sm)">{{ i18n.t('dash.q2d') }}</span></span></a>
        <a class="card row" routerLink="/health"><span class="ico-chip warn"><app-icon name="pulse" /></span><span><b>{{ i18n.t('dash.q3') }}</b><br><span class="muted" style="font-size: var(--t-sm)">{{ i18n.t('dash.q3d') }}</span></span></a>
      </div>

      <h2 class="section-title">{{ i18n.t('dash.how') }}</h2>
      <div class="steps">
        <div class="card step"><span class="num">1</span><h3>{{ i18n.t('dash.s1') }}</h3><p>{{ i18n.t('dash.s1d') }}</p></div>
        <div class="card step"><span class="num">2</span><h3>{{ i18n.t('dash.s2') }}</h3><p>{{ i18n.t('dash.s2d') }}</p></div>
        <div class="card step"><span class="num">3</span><h3>{{ i18n.t('dash.s3') }}</h3><p>{{ i18n.t('dash.s3d') }}</p></div>
      </div>
    </div>
  `,
})
export class DashboardPage {
  protected readonly i18n = inject(I18n);
  protected readonly health = inject(HealthService);
  protected readonly store = inject(SessionStore);
  protected readonly connected = computed(() => this.store.sessions().filter((s) => s.status === 'Connected').length);
  protected readonly needsQr = computed(() => this.store.sessions().filter((s) => s.status === 'NeedsQr').length);
  protected readonly healthy = computed(() => this.health.data()?.checks.filter((c) => c.status.toLowerCase() === 'healthy').length ?? 0);
  protected chip() { const o = this.health.overall(); return o === 'ok' ? 'ok' : o === 'unknown' ? 'warn' : 'bad'; }
}
