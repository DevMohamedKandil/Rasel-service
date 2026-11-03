import { Component, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ApiService } from './core/api.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header>
      <strong>Rasel admin</strong>
      @if (api.loggedIn()) {
        <nav>
          <a routerLink="/sessions" routerLinkActive="on">Sessions</a>
          <a routerLink="/messages" routerLinkActive="on">Messages</a>
          <a routerLink="/health" routerLinkActive="on">Health</a>
        </nav>
        <button type="button" (click)="logout()">Sign out</button>
      }
    </header>
    <main><router-outlet /></main>
  `,
  styles: `
    header { display: flex; gap: 1rem; align-items: center; padding: .75rem 1rem; border-bottom: 1px solid #ccc; }
    nav { display: flex; gap: 1rem; flex: 1; }
    nav a { text-decoration: none; color: inherit; } nav a.on { font-weight: 700; text-decoration: underline; }
    main { padding: 1rem; max-width: 56rem; }
  `,
})
export class App {
  protected readonly api = inject(ApiService);
  private readonly router = inject(Router);
  protected logout() { this.api.logout(); this.router.navigateByUrl('/login'); }
}
