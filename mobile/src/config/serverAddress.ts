// ============================================================
// KynexOne server address — validation and the QR format
// ============================================================
//
// One store app serves every KynexOne installation: the server is chosen at runtime, typed in or scanned from
// the QR code an administrator shows in Organisation Admin (kynexone://server?url=<https url>). The rules are the ones a
// release build applies to a configured URL: HTTPS only, never a loopback address.
// Keep this file import-free so tests can load it under node --test.

export type ServerAddressError =
  | 'empty'
  | 'invalid'
  | 'httpsRequired'
  | 'loopback'
  | 'credentials'
  | 'notKynexOneQr';

export type ServerAddressResult =
  | { ok: true; origin: string }
  | { ok: false; error: ServerAddressError };

/** The scheme and host of the QR code Organisation Admin shows. */
export const SERVER_QR_PREFIX = 'kynexone://server';

const LOOPBACK_HOST = /^(localhost|0\.0\.0\.0|127(?:\.\d{1,3}){3}|\[::1\]|::1|\[::\])$/i;

/**
 * The server's origin (https://host[:port]) from what someone typed: a bare host gets https://, a trailing /api or
 * any other path is dropped. `allowInsecure` admits http:// for a development build only.
 */
export function normalizeServerAddress(input: string, options: { allowInsecure?: boolean } = {}): ServerAddressResult {
  const raw = (input ?? '').trim();
  if (!raw) return { ok: false, error: 'empty' };

  const withScheme = /^[a-z][a-z0-9+.-]*:\/\//i.test(raw) ? raw : `https://${raw}`;
  let url: URL;
  try {
    url = new URL(withScheme);
  } catch {
    return { ok: false, error: 'invalid' };
  }
  if (!url.hostname) return { ok: false, error: 'invalid' };
  if (url.protocol !== 'https:' && !(options.allowInsecure && url.protocol === 'http:')) {
    return { ok: false, error: 'httpsRequired' };
  }
  if (url.username || url.password) return { ok: false, error: 'credentials' };
  if (LOOPBACK_HOST.test(url.hostname) && !options.allowInsecure) return { ok: false, error: 'loopback' };
  return { ok: true, origin: url.origin };
}

/** The server address carried by a scanned QR code, validated like a typed one. */
export function parseServerQr(data: string, options: { allowInsecure?: boolean } = {}): ServerAddressResult {
  const value = (data ?? '').trim();
  if (!value.toLowerCase().startsWith(SERVER_QR_PREFIX)) return { ok: false, error: 'notKynexOneQr' };
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return { ok: false, error: 'notKynexOneQr' };
  }
  const target = url.searchParams.get('url');
  if (!target) return { ok: false, error: 'notKynexOneQr' };
  return normalizeServerAddress(target, options);
}

/** The QR payload for a server origin (what Organisation Admin encodes). */
export function serverQrPayload(origin: string): string {
  return `${SERVER_QR_PREFIX}?url=${encodeURIComponent(origin)}`;
}

/** The API base the app's clients use for a server origin. */
export function apiBaseUrlFor(origin: string): string {
  return `${origin.replace(/\/+$/, '')}/api`;
}

/** GET /api/public/installation: what the app needs to know about a server before anyone signs in. */
export interface InstallationInfo {
  setupComplete: boolean;
  organisationName: string | null;
  pushEnabled: boolean;
  licence: { state: string };
}

/** Whether an answer is a KynexOne installation's (and not some other site's JSON). */
export function isInstallationInfo(value: unknown): value is InstallationInfo {
  if (!value || typeof value !== 'object') return false;
  const v = value as Record<string, unknown>;
  return typeof v.setupComplete === 'boolean'
    && (v.organisationName === null || typeof v.organisationName === 'string')
    && typeof v.pushEnabled === 'boolean'
    && !!v.licence && typeof (v.licence as Record<string, unknown>).state === 'string';
}
