import { Component, inject } from '@angular/core';
import { I18n } from '../core/i18n';
import { Hint } from '../ui/hint';
import { Icon } from '../ui/icon';

@Component({
  selector: 'app-help',
  imports: [Icon, Hint],
  template: `
    <div class="page">
      <div class="page-head"><div><h1>{{ i18n.t('help.title') }}</h1><p>{{ i18n.t('help.sub') }}</p></div></div>
      <div class="card row" style="align-items: flex-start">
        <span class="ico-chip"><app-icon name="chat" /></span>
        <div><h2>{{ i18n.t('help.what') }}</h2><p class="muted" style="margin-top: var(--s-2)">{{ i18n.t('help.whatd') }}</p></div>
      </div>
      <h2 class="section-title">{{ i18n.t('help.use') }}</h2>
      <div class="steps">
        @for (k of ['u1', 'u2', 'u3', 'u4']; track k; let i = $index) {
          <div class="card step"><span class="num">{{ i + 1 }}</span><p style="color: var(--c-text); margin: 0">{{ i18n.t('help.' + k) }}</p></div>
        }
      </div>
      <div class="notice ok" style="margin-top: var(--s-5)"><app-icon name="shield" /><span><b>{{ i18n.t('help.priv') }}.</b> {{ i18n.t('help.privd') }}</span></div>
      <h2 class="section-title">{{ i18n.t('help.terms') }}</h2>
      <div class="grid cols-2">
        @for (k of ['daily', 'otp', 'idem', 'pacing', 'masked']; track k) {
          <div class="card"><b>{{ i18n.t('help.t.' + k) }}</b> <app-hint [key]="'hint.' + k" /><p class="muted" style="font-size: var(--t-sm); margin-top: var(--s-2)">{{ i18n.t('hint.' + k) }}</p></div>
        }
      </div>
      <h2 class="section-title">{{ i18n.t('help.faq') }}</h2>
      @for (k of ['1', '2', '3']; track k) {
        <details class="faq"><summary>{{ i18n.t('help.q' + k) }}</summary><p>{{ i18n.t('help.a' + k) }}</p></details>
      }
    </div>
  `,
})
export class HelpPage {
  protected readonly i18n = inject(I18n);
}
