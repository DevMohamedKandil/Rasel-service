/** Masks anything that looks like a phone number (7+ digits, optional +/separators): keeps the last 2 digits. */
export function maskPhones(text: string | null | undefined): string {
  if (!text) return '';
  return text.replace(/\+?\d[\d\s().-]{5,}\d/g, (m) => {
    const digits = m.replace(/\D/g, '');
    return digits.length < 7 ? m : '*'.repeat(digits.length - 2) + digits.slice(-2);
  });
}
