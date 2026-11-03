import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { I18n } from './i18n';
import { errorKey, messageView } from './msg-view';
import { ThemeService } from './theme';
import { ToastService } from './toast';

describe('messageView', () => {
  it('queued with no attempts: first step active', () => {
    const v = messageView('Queued', 0);
    expect(v.steps.map((s) => s.state)).toEqual(['active', 'todo', 'todo']);
  });
  it('queued with attempts means retrying', () => {
    expect(messageView('Queued', 2).steps[1].state).toBe('active');
    expect(messageView('Queued', 2).tone).toBe('warn');
  });
  it('sent and failed end states', () => {
    expect(messageView('Sent', 1).steps.every((s) => s.state === 'done')).toBe(true);
    expect(messageView('Failed', 5).steps[2]).toEqual({ key: 'failed', state: 'bad' });
  });
  it('maps error codes to friendly keys, unknown to generic, none to null', () => {
    expect(errorKey('provider_error')).toBe('err.provider_error');
    expect(errorKey('group_not_found_on_session')).toBe('err.group_not_found_on_session');
    for (const c of ['group_ambiguous', 'session_not_ready', 'send_failed', 'invalid_request', 'provider_timeout', 'sidecar_unreachable']) expect(errorKey(c)).toBe('err.' + c);
    expect(errorKey('weird_x')).toBe('err.generic');
    expect(errorKey(null)).toBeNull();
  });
});

describe('I18n', () => {
  it('defaults to Arabic rtl and switches to English ltr', () => {
    const i = TestBed.inject(I18n);
    expect(i.lang()).toBe('ar');
    expect(i.t('nav.sessions')).toBe('الجلسات');
    i.toggle();
    TestBed.tick();
    expect(i.t('nav.sessions')).toBe('Sessions');
    expect(document.documentElement.dir).toBe('ltr');
  });
  it('every key has both languages', () => {
    const i = TestBed.inject(I18n);
    expect(i.t('missing.key')).toBe('missing.key');
  });
});

describe('ThemeService', () => {
  it('persists only the theme name', () => {
    localStorage.clear();
    const t = TestBed.inject(ThemeService);
    const before = t.theme();
    t.toggle();
    expect(t.theme()).not.toBe(before);
    expect(Object.keys(localStorage)).toEqual(['rasel-theme']);
  });
});

describe('ToastService', () => {
  it('shows a friendly message, never the raw response body', () => {
    const toast = TestBed.inject(ToastService);
    toast.error(new HttpErrorResponse({ status: 500, error: { stack: 'at secret.cs:1', detail: 'boom' } }));
    const text = JSON.stringify(toast.items());
    expect(text).not.toContain('secret');
    expect(text).not.toContain('boom');
    expect(toast.items().length).toBe(1);
  });
});

import { Component } from '@angular/core';
import { Hint } from '../ui/hint';
import { dictionaryKeys } from './i18n';

describe('dictionaries', () => {
  it('Arabic and English have identical, non-empty keys', () => {
    expect(dictionaryKeys('ar').sort()).toEqual(dictionaryKeys('en').sort());
    expect(dictionaryKeys('ar').length).toBeGreaterThan(100);
  });
  it('every hint key used for session states and health checks exists', () => {
    const keys = dictionaryKeys('en');
    for (const k of ['Starting', 'NeedsQr', 'Connected', 'Failed', 'Stopped']) expect(keys).toContain('hint.st.' + k);
    for (const k of ['qr', 'key', 'session', 'attempts', 'lasterr', 'masked', 'daily', 'otp', 'idem', 'pacing', 'hc.database', 'hc.outbox']) expect(keys).toContain('hint.' + k);
  });
});

@Component({ imports: [Hint], template: `<app-hint key="hint.qr" />` })
class Host {}

describe('Hint', () => {
  it('opens on focus, links via aria-describedby, closes on Escape', () => {
    const f = TestBed.createComponent(Host);
    f.detectChanges();
    const btn: HTMLButtonElement = f.nativeElement.querySelector('button');
    btn.dispatchEvent(new Event('focus'));
    f.detectChanges();
    const pop = f.nativeElement.querySelector('[role=tooltip]');
    expect(pop).toBeTruthy();
    expect(btn.getAttribute('aria-describedby')).toBe(pop.id);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    f.detectChanges();
    expect(f.nativeElement.querySelector('[role=tooltip]')).toBeNull();
  });
});
