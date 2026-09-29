export const WORKSPACE_QUERY_ALIASES = ['workspace', 'tenant', 'tenantSlug', 'w'] as const;

export interface SearchParamsLike {
  getAll(name: string): string[];
}

export function normalizeWorkspace(value: string | null | undefined): string {
  return (value ?? '').trim().toLowerCase();
}

export function requireWorkspace(value: string | null | undefined): string {
  const normalized = normalizeWorkspace(value);
  if (!normalized) throw new Error('Workspace is required.');
  return normalized;
}

export function resolveWorkspaceAlias(params: SearchParamsLike): string {
  for (const alias of WORKSPACE_QUERY_ALIASES) {
    for (const candidate of params.getAll(alias)) {
      const normalized = normalizeWorkspace(candidate);
      if (normalized) return normalized;
    }
  }
  return '';
}

export function normalizeEmail(value: string): string {
  return value.trim();
}

export function safeLocalReturnPath(value: string | null | undefined): string {
  if (!value || !value.startsWith('/') || value.startsWith('//') || value.includes('\\')) {
    return '/dashboard';
  }
  return value;
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
