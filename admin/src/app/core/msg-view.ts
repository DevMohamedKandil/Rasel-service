export type StepState = 'done' | 'active' | 'todo' | 'bad';
export interface Step { key: 'queued' | 'sending' | 'sent' | 'failed'; state: StepState; }
export interface MsgView { steps: Step[]; summaryKey: string; tone: 'info' | 'ok' | 'bad' | 'warn'; }

/** Maps the API status (Queued/Sent/Failed) + attempts to a 3-step timeline. */
export function messageView(status: string, attempts: number): MsgView {
  if (status === 'Sent') {
    return { tone: 'ok', summaryKey: 'msg.sum.Sent', steps: [s('queued', 'done'), s('sending', 'done'), s('sent', 'done')] };
  }
  if (status === 'Failed') {
    return { tone: 'bad', summaryKey: 'msg.sum.Failed', steps: [s('queued', 'done'), s('sending', 'done'), s('failed', 'bad')] };
  }
  if (attempts > 0) {
    return { tone: 'warn', summaryKey: 'msg.sum.Retry', steps: [s('queued', 'done'), s('sending', 'active'), s('sent', 'todo')] };
  }
  return { tone: 'info', summaryKey: 'msg.sum.Queued', steps: [s('queued', 'active'), s('sending', 'todo'), s('sent', 'todo')] };
}
function s(key: Step['key'], state: StepState): Step { return { key, state }; }

const CODES = ['no_session', 'provider_error', 'provider_transient_error', 'timeout', 'group_not_found_on_session', 'group_ambiguous', 'session_not_ready', 'send_failed', 'invalid_request', 'provider_timeout', 'sidecar_unreachable'];
const KNOWN: Record<string, string> = Object.fromEntries(CODES.map(c => [c, 'err.' + c]));
/** Translation key for a backend error code; unknown codes fall back to a generic sentence. */
export function errorKey(code: string | null | undefined): string | null {
  if (!code) return null;
  return KNOWN[code.trim().toLowerCase()] ?? 'err.generic';
}
