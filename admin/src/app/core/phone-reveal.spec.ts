import { Subject, of, throwError } from 'rxjs';
import { PhoneReveal, REVEAL_MS } from './phone-reveal';

describe('PhoneReveal', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('is hidden by default, reveals on click and hides on the second click', () => {
    const r = new PhoneReveal(() => of('+201000000880'));
    expect(r.revealed()).toBe(false);
    r.toggle('s1');
    expect(r.revealed()).toBe(true);
    expect(r.full()).toBe('+201000000880');
    r.toggle('s1');
    expect(r.revealed()).toBe(false);
    expect(r.full()).toBe('');
  });

  it('re-masks automatically after the timeout', () => {
    const r = new PhoneReveal(() => of('+201000000880'));
    r.toggle('s1');
    vi.advanceTimersByTime(REVEAL_MS - 1);
    expect(r.revealed()).toBe(true);
    vi.advanceTimersByTime(2);
    expect(r.revealed()).toBe(false);
    expect(r.full()).toBe('');
  });

  it('hide() (QR close, session change, logout) clears it, even while loading', () => {
    const pending = new Subject<string>();
    const r = new PhoneReveal(() => pending);
    r.toggle('s1');
    expect(r.loading()).toBe(true);
    r.hide();
    pending.next('+201000000880');
    expect(r.revealed()).toBe(false);
    expect(r.full()).toBe('');
  });

  it('reports a fetch error and stays hidden', () => {
    const r = new PhoneReveal(() => throwError(() => new Error('x')));
    let err: unknown;
    r.toggle('s1', (e) => (err = e));
    expect(err).toBeTruthy();
    expect(r.revealed()).toBe(false);
    expect(r.loading()).toBe(false);
  });
});
