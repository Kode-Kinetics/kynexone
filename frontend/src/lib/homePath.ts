/**
 * Where someone lands after signing in when no page asked for them (`?from=`).
 *
 * An employee who only has self-service lands on Self-Service (/ess): the dashboard is the HR
 * workspace, and its first screen is not theirs. Everyone holding anything beyond the Employee
 * role's baseline (HR, admin, a manager's approvals, finance…) keeps landing on /dashboard.
 *
 * The baseline mirrors the Employee role in backend AuthSeeder.cs (level 16): dashboard.read,
 * profile.read, ess.read, ess.write, performance.read, loans.self. Any `ess.*` or `*.self`
 * permission is self-service by construction, so a later self-service grant does not flip anyone
 * back to the dashboard.
 */
const EMPLOYEE_BASELINE = new Set(['dashboard.read', 'profile.read', 'ess.read', 'ess.write', 'performance.read', 'loans.self']);

export interface HomePathUser {
  permissions?: readonly string[] | null;
  roles?: readonly string[] | null;
  accessMode?: string | null;
}

export function isEmployeeOnly(user: HomePathUser | null | undefined): boolean {
  const permissions = user?.permissions ?? [];
  if (!permissions.includes('ess.read')) return false;
  if (user?.accessMode === 'ESSOnly') return true;
  return permissions.every((p) => EMPLOYEE_BASELINE.has(p) || p.startsWith('ess.') || p.endsWith('.self'));
}

export function homePathFor(user: HomePathUser | null | undefined): '/ess' | '/dashboard' {
  return isEmployeeOnly(user) ? '/ess' : '/dashboard';
}
