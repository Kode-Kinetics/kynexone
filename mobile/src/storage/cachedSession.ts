// What the app keeps about the signed-in user between launches. Deliberately tiny: ids the API
// layer needs to scope requests, and nothing a stolen backup could use (no name, email,
// permissions, department or manager). /auth/me is the authority on launch; this is not an
// offline copy of the profile. Import-free so it runs under node --test.

export interface CachedSession {
  id: string;
  tenantId: string;
  tenantSlug?: string;
  employeeId: string;
  role?: string;
}

export function toCachedSession(user: {
  id?: unknown;
  tenantId?: unknown;
  tenantSlug?: unknown;
  employeeId?: unknown;
  role?: unknown;
}): CachedSession {
  const text = (v: unknown) => (v === null || v === undefined ? '' : String(v));
  const session: CachedSession = {
    id: text(user.id),
    tenantId: text(user.tenantId),
    employeeId: text(user.employeeId),
  };
  if (typeof user.tenantSlug === 'string' && user.tenantSlug) session.tenantSlug = user.tenantSlug;
  if (typeof user.role === 'string' && user.role) session.role = user.role;
  return session;
}

/** Reads a stored value (new or legacy full-profile JSON) and keeps only the cached fields. */
export function parseCachedSession(raw: string | null): CachedSession | null {
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== 'object' || !parsed.id) return null;
    return toCachedSession(parsed);
  } catch {
    return null;
  }
}
