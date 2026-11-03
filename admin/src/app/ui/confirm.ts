import { Component, HostListener, inject, input, output } from '@angular/core';
import { I18n } from '../core/i18n';
import { Icon } from './icon';

/** A modal confirmation: says what will happen, then Confirm or Cancel. Esc and a click outside cancel. */
@Component({
  selector: 'app-confirm',
  imports: [Icon],
  template: `
    <div class="dlg-back" (click)="cancelled.emit()">
      <div class="dlg card" role="dialog" aria-modal="true" [attr.aria-labelledby]="'dlg-title'" [attr.aria-describedby]="'dlg-body'" (click)="$event.stopPropagation()" data-testid="confirm-dialog">
        <h3 id="dlg-title"><app-icon [name]="danger() ? 'alert' : 'help'" /> {{ heading() }}</h3>
        <p id="dlg-body">{{ message() }}</p>
        <ng-content />
        <div class="row wrap dlg-actions">
          <button type="button" class="btn ghost sm" (click)="cancelled.emit()" [disabled]="busy()" data-testid="confirm-cancel">{{ i18n.t('dlg.cancel') }}</button>
          <button type="button" class="btn sm" [class.danger]="danger()" (click)="confirmed.emit()" [disabled]="busy()" data-testid="confirm-ok">{{ confirmLabel() }}</button>
        </div>
      </div>
    </div>
  `,
  styles: `
    .dlg-back { position: fixed; inset: 0; z-index: 250; display: grid; place-items: center; padding: 1rem; background: rgb(0 0 0 / .5); }
    .dlg { width: min(30rem, 100%); box-shadow: var(--shadow-2); }
    .dlg h3 { display: flex; align-items: center; gap: .5rem; margin: 0 0 .6rem; }
    .dlg p { margin: 0 0 1rem; line-height: 1.7; }
    .dlg-actions { justify-content: flex-end; margin-top: 1rem; }
    .btn.danger { background: var(--c-bad); border-color: transparent; color: var(--c-bg); }
  `,
})
export class Confirm {
  readonly heading = input.required<string>();
  readonly message = input.required<string>();
  readonly confirmLabel = input.required<string>();
  readonly danger = input(false);
  readonly busy = input(false);
  readonly confirmed = output<void>();
  readonly cancelled = output<void>();
  protected readonly i18n = inject(I18n);
  @HostListener('document:keydown.escape') onEsc() { if (!this.busy()) this.cancelled.emit(); }
}
