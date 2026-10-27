export function normalizeEmail(value: string): string {
  return value.trim();
}

/** Whitespace, ASCII control characters, and the backslash browsers read as '/'. */
const UNSAFE_RETURN_CHARS = /[\s\u0000-\u001f\u007f\\]/;
/** Stands in for the page origin when there is no window (server render, unit tests). */
const FALLBACK_RETURN_ORIGIN = 'https://return-path.invalid';

/**
 * A same-origin path to send someone back to after an action, or null when the value could leave
 * this site. A prefix check is not enough: the URL parser strips tabs and newlines and reads '\' as
 * '/', so "/\t/evil.example" and "/\\evil.example" both resolve to https://evil.example/. Instead the
 * value is resolved the way the browser will resolve it and must land on this origin. Its decoded
 * path is checked too ("/%09/evil.example"), so a consumer that decodes it cannot be steered off-site.
 */
export function localReturnPathOrNull(value: string | null | undefined): string | null {
  if (!value || !value.startsWith('/') || value.startsWith('//') || UNSAFE_RETURN_CHARS.test(value)) return null;
  const origin = typeof window !== 'undefined' && window.location?.origin ? window.location.origin : FALLBACK_RETURN_ORIGIN;
  let target: URL;
  try {
    target = new URL(value, origin);
  } catch {
    return null;
  }
  if (target.origin !== origin || target.username || target.password) return null;
  let decodedPath: string;
  try {
    decodedPath = decodeURIComponent(target.pathname);
  } catch {
    return null;
  }
  if (decodedPath.startsWith('//') || UNSAFE_RETURN_CHARS.test(decodedPath)) return null;
  return `${target.pathname}${target.search}${target.hash}`;
}

/** {@link localReturnPathOrNull}, falling back to the dashboard. */
export function safeLocalReturnPath(value: string | null | undefined): string {
  return localReturnPathOrNull(value) ?? '/dashboard';
}

export function tokenFromFragment(fragment: string): string {
  const value = fragment.startsWith('#') ? fragment.slice(1) : fragment;
  for (const pair of value.split('&')) {
    const separator = pair.indexOf('=');
    const rawName = separator >= 0 ? pair.slice(0, separator) : pair;
    if (safeDecode(rawName) !== 'token') continue;
    const rawToken = separator >= 0 ? pair.slice(separator + 1) : '';
    // decodeURIComponent preserves a raw '+'. URLSearchParams would rewrite it
    // to a space, corrupting otherwise valid one-use credentials.
    return safeDecode(rawToken);
  }
  return '';
}

function safeDecode(value: string): string {
  try { return decodeURIComponent(value); }
  catch { return ''; }
}

interface FragmentLocation {
  hash: string;
  pathname: string;
  search: string;
}

interface FragmentHistory {
  state: unknown;
  replaceState(data: unknown, unused: string, url?: string | URL | null): void;
}

/** Capture the one-use credential and scrub the complete fragment as one operation. */
export function consumeFragmentToken(location: FragmentLocation, history: FragmentHistory): string {
  const token = tokenFromFragment(location.hash);
  if (location.hash) {
    history.replaceState(history.state, '', `${location.pathname}${location.search}`);
  }
  return token;
}
