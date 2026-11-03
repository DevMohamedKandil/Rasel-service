import { Routes } from '@angular/router';
import { authGuard } from './core/auth.guard';

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./pages/login').then((m) => m.LoginPage) },
  { path: 'dashboard', canActivate: [authGuard], loadComponent: () => import('./pages/dashboard').then((m) => m.DashboardPage) },
  { path: 'sessions', canActivate: [authGuard], loadComponent: () => import('./pages/sessions').then((m) => m.SessionsPage) },
  { path: 'messages', canActivate: [authGuard], loadComponent: () => import('./pages/messages').then((m) => m.MessagesPage) },
  { path: 'health', canActivate: [authGuard], loadComponent: () => import('./pages/health').then((m) => m.HealthPage) },
  { path: 'help', canActivate: [authGuard], loadComponent: () => import('./pages/help').then((m) => m.HelpPage) },
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  { path: '**', redirectTo: 'dashboard' },
];
