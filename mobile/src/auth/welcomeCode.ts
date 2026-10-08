/**
 * Welcome codes on the phone: the 8-digit code HR hands an employee for their first sign-in.
 * Same rules as the web (frontend/src/lib/welcomeCode.ts): Arabic-Indic and Persian digits become
 * ASCII, spaces and dashes are ignored, and the server stays the authority. Import-free so the node
 * test runner can load it.
 *
 * The code is a credential: it lives in screen state only, never in storage or logs.
 */

/** Arabic-Indic (٠-٩) and Persian / Extended Arabic-Indic (۰-۹) digits to ASCII. */
export function toAsciiDigits(value: string): string {
  return value.replace(/[\u0660-\u0669\u06F0-\u06F9]/g, (ch) => {
    const code = ch.charCodeAt(0);
    return String(code >= 0x06F0 ? code - 0x06F0 : code - 0x0660);
  });
}

/** Digits to ASCII; spaces (including non-breaking) and every kind of dash removed. */
export function normalizeWelcomeCode(value: string | null | undefined): string {
  return toAsciiDigits(value ?? '').replace(/[\s\u00A0\u200B-\u200F\u2066-\u2069\u2010-\u2015\u2212\uFE58\uFE63\uFF0D-]/g, '');
}

/** True when the value, once normalised, is exactly eight digits. */
export function isWelcomeCode(value: string | null | undefined): boolean {
  return /^[0-9]{8}$/.test(normalizeWelcomeCode(value));
}

/** "48217730" → "4821 7730", the way the slip prints it. Anything else is returned normalised. */
export function formatWelcomeCode(value: string): string {
  const code = normalizeWelcomeCode(value);
  return /^[0-9]{8}$/.test(code) ? `${code.slice(0, 4)} ${code.slice(4)}` : code;
}

/** Arabic letters (not digits) in a password: worth a hint, because many phones and PCs cannot type them. */
export function hasArabicLetters(value: string): boolean {
  return /[\u0621-\u064A\u066E-\u06D3\u06FA-\u06FF\u0750-\u077F\uFB50-\uFDFF\uFE70-\uFEFF]/.test(value);
}

/**
 * The two live checks under "Choose a password". The tenant's own policy is enforced by the server
 * (`password_policy`); these are the two rules every tenant shares and a person can see at a glance.
 *
 * "Not your name or email": before sign-in we only know the email, and a work email is almost always
 * built from the name (noah.williams@…), so its parts stand in for the name.
 */
export const DEFAULT_MIN_PASSWORD_LENGTH = 10;

export function passwordChecks(password: string, email: string, minLength = DEFAULT_MIN_PASSWORD_LENGTH) {
  const pw = toAsciiDigits(password).toLowerCase();
  const local = email.trim().toLowerCase().split('@')[0] ?? '';
  const parts = local.split(/[^a-z\u0600-\u06FF]+/).filter((part) => part.length >= 3);
  const personal = [email.trim().toLowerCase(), local, ...parts].filter((part) => part.length >= 3);
  return {
    longEnough: [...password].length >= minLength,
    notPersonal: pw.length > 0 && !personal.some((part) => pw.includes(part)),
  };
}

/** What a refused redeem means, as a key under `signin.errors`. Never the server's text. */
export type WelcomeErrorKey =
  | 'codeInvalid' | 'codeGone' | 'passwordRules' | 'companyIdNeeded' | 'tooManyTries'
  | 'signOutFirst' | 'planFull' | 'generic' | 'network';

export function welcomeErrorKey(status: number | undefined, code: unknown): WelcomeErrorKey {
  if (status === undefined) return 'network';
  if (status === 429) return 'tooManyTries';
  if (status === 400) {
    switch (code) {
      case 'code_invalid': return 'codeInvalid';
      case 'code_expired':
      case 'code_used': return 'codeGone';
      case 'password_policy': return 'passwordRules';
      case 'workspace_required': return 'companyIdNeeded';
      case 'try_later': return 'tooManyTries';
      case 'sign_out_first': return 'signOutFirst';
      case 'seat_limit': return 'planFull';
      default: return 'generic';
    }
  }
  return 'generic';
}

/** A lost or failed reply: the single-use code may already be spent, so try signing in instead of redeeming again. */
export function isAmbiguousFailure(status: number | undefined): boolean {
  return status === undefined || status === 408 || status >= 500;
}
