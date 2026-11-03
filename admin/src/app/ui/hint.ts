import { Component, ElementRef, HostListener, inject, input, signal } from '@angular/core';
import { I18n } from '../core/i18n';

let seq = 0;

/** A small "?" button that shows a translated plain-language tooltip on hover, focus and tap; Esc closes it. */
@Component({
  selector: 'app-hint',
  template: `
    <button type="button" class="hint-btn" [attr.aria-label]="i18n.t('hint.what')" [attr.aria-describedby]="open() ? id : null" [attr.aria-expanded]="open()"
      (mouseenter)="show()" (mouseleave)="hide()" (focus)="show()" (blur)="hide()" (click)="toggle($event)" #btn>
      <svg viewBox="0 0 24 24" width="1em" height="1em" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><circle cx="12" cy="12" r="9.5" /><path d="M9.6 9.4a2.5 2.5 0 114 2c-.9.6-1.6 1-1.6 2.2M12 16.8h.01" /></svg>
    </button>
    @if (open()) {
      <span class="hint-pop" role="tooltip" [id]="id" [style.top.px]="top()" [style.left.px]="left()">{{ i18n.t(key()) }}</span>
    }
  `,
  styles: `
    :host { display: inline-flex; vertical-align: middle; }
    .hint-btn { display: inline-grid; place-items: center; width: 1.5rem; height: 1.5rem; padding: 0; border: 0; border-radius: 50%; background: none; color: var(--c-faint); cursor: help; font-size: 1.1rem; }
    .hint-btn:hover, .hint-btn[aria-expanded='true'] { color: var(--c-accent-text); }
    .hint-pop { position: fixed; z-index: 300; width: max-content; max-width: min(18rem, calc(100vw - 1.5rem)); padding: .6rem .85rem; border-radius: .7rem; background: var(--c-text); color: var(--c-bg); font-size: .8rem; font-weight: 600; line-height: 1.55; text-align: start; box-shadow: var(--shadow-2); pointer-events: none; white-space: normal; }
  `,
})
export class Hint {
  readonly key = input.required<string>();
  protected readonly i18n = inject(I18n);
  private readonly el = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly id = 'hint-' + ++seq;
  protected readonly open = signal(false);
  protected readonly top = signal(0);
  protected readonly left = signal(0);

  show() {
    const r = this.el.nativeElement.getBoundingClientRect();
    const w = Math.min(288, window.innerWidth - 24);
    const centre = r.left + r.width / 2;
    this.left.set(Math.max(12, Math.min(centre - w / 2, window.innerWidth - w - 12)));
    this.top.set(r.bottom + 8);
    this.open.set(true);
  }
  hide() { this.open.set(false); }
  toggle(e: Event) { e.preventDefault(); e.stopPropagation(); this.open() ? this.hide() : this.show(); }
  @HostListener('document:keydown.escape') onEsc() { this.hide(); }
  @HostListener('document:click') onDoc() { this.hide(); }
  @HostListener('window:scroll') onScroll() { this.hide(); }
}
