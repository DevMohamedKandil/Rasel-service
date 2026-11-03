import { Component, OnInit, inject, signal } from '@angular/core';
import { ApiService, HealthDto, describeError } from '../core/api.service';

@Component({
  selector: 'app-health',
  template: `
    <h2>Health</h2>
    <button type="button" (click)="load()">Refresh</button>
    @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    @if (health(); as h) {
      <p>Overall: <strong>{{ h.status }}</strong></p>
      <ul>@for (c of h.checks; track c.name) { <li>{{ c.name }}: {{ c.status }} ({{ c.detail ?? '' }})</li> }</ul>
    }
  `,
  styles: `.err { color: #b00020; }`,
})
export class HealthPage implements OnInit {
  private readonly api = inject(ApiService);
  readonly health = signal<HealthDto | null>(null);
  readonly error = signal('');
  ngOnInit() { this.load(); }
  load() {
    this.error.set('');
    this.api.health().subscribe({
      next: (h) => this.health.set(h),
      // an unhealthy check answers 503 with the same JSON body
      error: (e) => (e?.error?.checks ? this.health.set(e.error) : this.error.set(describeError(e))),
    });
  }
}
