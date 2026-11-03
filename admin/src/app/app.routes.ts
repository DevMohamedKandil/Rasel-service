import { Routes } from '@angular/router';
import { authGuard } from './core/auth.guard';

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./pages/login').then((m) => m.LoginPage) },
  { path: 'sessions', canActivate: [authGuard], loadComponent: () => import('./pages/sessions').then((m) => m.SessionsPage) },
  { path: 'messages', canActivate: [authGuard], loadComponent: () => import('./pages/messages').then((m) => m.MessagesPage) },
  { path: 'health', canActivate: [authGuard], loadComponent: () => import('./pages/health').then((m) => m.HealthPage) },
  { path: '', pathMatch: 'full', redirectTo: 'sessions' },
  { path: '**', redirectTo: 'sessions' },
];
