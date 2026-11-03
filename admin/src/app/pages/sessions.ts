import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, SessionDto, describeError } from '../core/api.service';
import { QrView, parseQr } from '../core/qr';

@Component({
  selector: 'app-sessions',
  imports: [FormsModule],
  template: `
    <h2>Sessions</h2>
    <form (ngSubmit)="create()">
      <input name="name" [(ngModel)]="name" placeholder="session name" required />
      <button type="submit">Create</button>
    </form>
    @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    <p class="hint">The API has no list endpoint: sessions created in this tab are listed here.</p>
    <table>
      <thead><tr><th>Name</th><th>Id</th><th>Status</th><th></th></tr></thead>
      <tbody>
        @for (s of sessions(); track s.id) {
          <tr>
            <td>{{ s.name }}</td><td><code>{{ s.id }}</code></td><td>{{ s.status }}</td>
            <td><button type="button" (click)="refresh(s)">Status</button> <button type="button" (click)="showQr(s)">QR</button></td>
          </tr>
        }
      </tbody>
    </table>
    @if (qr().kind === 'image') { <img [src]="imageSrc()" alt="WhatsApp QR code" width="260" height="260" /> }
    @if (qr().kind === 'text') { <p>QR payload: <code>{{ textQr() }}</code></p> }
    @if (qrNote()) { <p>{{ qrNote() }}</p> }
  `,
  styles: `.err { color: #b00020; } .hint { color: #666; } td, th { padding: .25rem .75rem; text-align: left; }`,
})
export class SessionsPage {
  private readonly api = inject(ApiService);
  name = 'main';
  readonly sessions = signal<SessionDto[]>([]);
  readonly error = signal('');
  readonly qr = signal<QrView>({ kind: 'none' });
  readonly qrNote = signal('');

  imageSrc() { const q = this.qr(); return q.kind === 'image' ? q.src : ''; }
  textQr() { const q = this.qr(); return q.kind === 'text' ? q.text : ''; }

  create() {
    this.error.set('');
    this.api.createSession(this.name).subscribe({
      next: (s) => this.sessions.update((l) => [s, ...l]),
      error: (e) => this.error.set(describeError(e)),
    });
  }
  refresh(s: SessionDto) {
    this.api.session(s.id).subscribe({
      next: (n) => this.sessions.update((l) => l.map((x) => (x.id === n.id ? n : x))),
      error: (e) => this.error.set(describeError(e)),
    });
  }
  showQr(s: SessionDto) {
    this.qr.set({ kind: 'none' });
    this.qrNote.set('');
    this.api.qr(s.id).subscribe({
      next: (r) => {
        this.sessions.update((l) => l.map((x) => (x.id === s.id ? { ...x, status: r.status } : x)));
        const view = parseQr(r.qr);
        this.qr.set(view);
        if (view.kind === 'none') this.qrNote.set(r.status === 'Connected' ? 'Already connected, no QR needed.' : 'No QR available yet, try again shortly.');
      },
      error: (e) => this.error.set(describeError(e)),
    });
  }
}
