export type QrView = { kind: 'image'; src: string } | { kind: 'text'; text: string } | { kind: 'none' };

/**
 * The Fake provider returns a plain string; the Bridge provider returns HTML `<img src="data:image/png;base64,...">`.
 * Only a data:image PNG URL is ever extracted; the HTML itself is never rendered, so a hostile answer cannot inject markup.
 */
export function parseQr(qr: string | null | undefined): QrView {
  if (!qr || !qr.trim()) return { kind: 'none' };
  const m = /^\s*<img\s+src="(data:image\/png;base64,[A-Za-z0-9+/=]+)"\s*\/?>\s*$/.exec(qr);
  if (m) return { kind: 'image', src: m[1] };
  if (qr.trimStart().startsWith('<')) return { kind: 'none' };
  return { kind: 'text', text: qr };
}
