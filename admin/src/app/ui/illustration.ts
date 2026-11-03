import { Component, input } from '@angular/core';

/** Small inline-SVG empty-state art, colours come from CSS variables. */
@Component({
  selector: 'app-illustration',
  template: `
    <svg viewBox="0 0 160 120" width="160" height="120" aria-hidden="true" focusable="false">
      <ellipse cx="80" cy="106" rx="52" ry="7" fill="var(--c-border)" opacity=".55" />
      @if (kind() === 'phone') {
        <rect x="52" y="14" width="56" height="88" rx="11" fill="var(--c-surface-2)" stroke="var(--c-border-strong)" stroke-width="2" />
        <rect x="60" y="28" width="40" height="40" rx="6" fill="none" stroke="var(--c-accent)" stroke-width="2.5" stroke-dasharray="6 5" />
        <circle cx="80" cy="48" r="7" fill="var(--c-accent)" opacity=".9" />
        <rect x="68" y="78" width="24" height="5" rx="2.5" fill="var(--c-border-strong)" />
        <rect x="72" y="90" width="16" height="4" rx="2" fill="var(--c-border)" />
      } @else {
        <rect x="26" y="26" width="86" height="54" rx="12" fill="var(--c-surface-2)" stroke="var(--c-border-strong)" stroke-width="2" />
        <path d="M44 80l-8 14 22-14z" fill="var(--c-surface-2)" stroke="var(--c-border-strong)" stroke-width="2" stroke-linejoin="round" />
        <rect x="40" y="40" width="46" height="6" rx="3" fill="var(--c-border-strong)" />
        <rect x="40" y="54" width="30" height="6" rx="3" fill="var(--c-border)" />
        <circle cx="118" cy="38" r="18" fill="var(--c-accent)" />
        <path d="M110 38l6 6 11-12" fill="none" stroke="#fff" stroke-width="3.5" stroke-linecap="round" stroke-linejoin="round" />
      }
    </svg>
  `,
  styles: `:host { display: inline-block; line-height: 0; }`,
})
export class Illustration {
  readonly kind = input<'phone' | 'message'>('phone');
}
