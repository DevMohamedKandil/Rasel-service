import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, MessageStatusDto, describeError } from '../core/api.service';
import { maskPhones } from '../core/mask';

@Component({
  selector: 'app-messages',
  imports: [FormsModule],
  template: `
    <h2>Message lookup</h2>
    <form (ngSubmit)="lookup()">
      <input name="id" [(ngModel)]="id" placeholder="message id (GUID)" size="40" required />
      <button type="submit">Look up</button>
    </form>
    <p class="hint">Only delivery state is shown. The text, the destination and OTP codes are never returned by the API.</p>
    @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    @if (msg(); as m) {
      <dl>
        <dt>Id</dt><dd><code>{{ m.id }}</code></dd>
        <dt>Status</dt><dd>{{ m.status }}</dd>
        <dt>Attempts</dt><dd>{{ m.attempts }}</dd>
        <dt>Created</dt><dd>{{ m.createdAt }}</dd>
        <dt>Sent</dt><dd>{{ m.sentAt ?? '-' }}</dd>
        <dt>Next attempt</dt><dd>{{ m.nextAttemptAt ?? '-' }}</dd>
        <dt>Last error</dt><dd>{{ lastError() || '-' }}</dd>
      </dl>
    }
  `,
  styles: `.err { color: #b00020; } .hint { color: #666; } dt { font-weight: 700; margin-top: .5rem; }`,
})
export class MessagesPage {
  private readonly api = inject(ApiService);
  id = '';
  readonly msg = signal<MessageStatusDto | null>(null);
  readonly error = signal('');

  lastError() { return maskPhones(this.msg()?.lastErrorCode); }

  lookup() {
    this.error.set('');
    this.msg.set(null);
    if (!/^[0-9a-fA-F-]{36}$/.test(this.id.trim())) return this.error.set('Enter a valid message id (GUID).');
    this.api.message(this.id.trim()).subscribe({
      next: (m) => this.msg.set(m),
      error: (e) => this.error.set(e?.status === 404 ? 'Message not found.' : describeError(e)),
    });
  }
}
