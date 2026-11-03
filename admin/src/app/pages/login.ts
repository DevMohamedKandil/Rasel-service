import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ApiService, describeError } from '../core/api.service';

@Component({
  selector: 'app-login',
  imports: [FormsModule],
  template: `
    <h2>Sign in</h2>
    <form (ngSubmit)="submit()">
      <label>API base URL <input name="base" [(ngModel)]="base" placeholder="http://localhost:5114" required /></label>
      <label>API key <input name="key" type="password" [(ngModel)]="key" autocomplete="off" required /></label>
      <button type="submit" [disabled]="busy()">Sign in</button>
    </form>
    <p class="hint">The key is kept in memory only. Reloading the page signs you out.</p>
    @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
  `,
  styles: `label { display: block; margin: .5rem 0; } input { display: block; width: 24rem; max-width: 100%; } .err { color: #b00020; } .hint { color: #666; }`,
})
export class LoginPage {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  base = '';
  key = '';
  readonly busy = signal(false);
  readonly error = signal('');

  submit() {
    this.error.set('');
    this.busy.set(true);
    this.api.login(this.base, this.key);
    // Verify the key with a cheap authenticated call (unknown session id: 404 means the key was accepted).
    this.api.session('00000000-0000-0000-0000-000000000000').subscribe({
      next: () => this.done(),
      error: (e) => {
        if (e?.status === 404) return this.done();
        this.api.logout();
        this.busy.set(false);
        this.error.set(e?.status === 401 ? 'Invalid API key.' : describeError(e));
      },
    });
  }
  private done() { this.key = ''; this.busy.set(false); this.router.navigateByUrl('/sessions'); }
}
