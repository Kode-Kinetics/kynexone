/**
 * Welcome codes: the 8-digit code HR hands an employee (on a printed slip, or by email) so they can
 * choose their own password on /welcome. The server is the authority on every rule here; the client
 * applies the same normalisation so the person sees what will be sent, and so a code typed with an
 * Arabic phone keyboard ("٤٨٢١ ٧٧٣٠") or copied from the slip ("4821-7730") works first time.
 *
 * The code is a credential. It lives only in component memory: never in storage, logs or a query
 * string. The QR on the slip carries it in the URL FRAGMENT, which never reaches a server, and
 * /welcome scrubs that fragment from the address bar before first paint.
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

function safeDecode(value: string): string {
  try { return decodeURIComponent(value); } catch { return ''; }
}

export interface WelcomeFragment {
  email: string;
  code: string;
  /** The company's sign-in ID (tenant slug). The slip's QR carries it so an ambiguous email domain never stops anyone. */
  workspace: string;
}

/**
 * Reads the slip QR's `#e=<email>&c=<code>&w=<company id>`. Unknown names
 * are ignored; a malformed value reads as empty; the first occurrence of a name wins.
 */
export function welcomeFromFragment(fragment: string): WelcomeFragment {
  const value = fragment.startsWith('#') ? fragment.slice(1) : fragment;
  const out: WelcomeFragment = { email: '', code: '', workspace: '' };
  for (const pair of value.split('&')) {
    const separator = pair.indexOf('=');
    if (separator < 0) continue;
    const name = safeDecode(pair.slice(0, separator));
    // decodeURIComponent, not URLSearchParams: a raw '+' in an email (a+b@x.com) must stay a '+'.
    const raw = safeDecode(pair.slice(separator + 1));
    if (name === 'e' && !out.email) out.email = raw.trim();
    if (name === 'c' && !out.code) out.code = normalizeWelcomeCode(raw);
    if (name === 'w' && !out.workspace) out.workspace = raw.trim().toLowerCase();
  }
  return out;
}

interface FragmentLocation { hash: string; pathname: string; search: string }
interface FragmentHistory {
  state: unknown;
  replaceState(data: unknown, unused: string, url?: string | URL | null): void;
}

/**
 * Capture the email and code, then scrub the WHOLE fragment from the address bar in the same step.
 * `stashed` is what /welcome's inline boot script already took out of the address bar, if it ran.
 */
export function consumeWelcomeFragment(location: FragmentLocation, history: FragmentHistory, stashed = ''): WelcomeFragment {
  const captured = welcomeFromFragment(stashed || location.hash);
  if (location.hash) history.replaceState(history.state, '', `${location.pathname}${location.search}`);
  return captured;
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
