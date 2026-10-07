import type { AccessCeiling } from '../api/identity';

/**
 * The Access screen's view of the server's privilege ceiling (backend PrivilegeCeiling): nobody hands out more
 * than they hold, nobody changes their own access, and the Admin role is an Admin's to give.
 *
 * The SERVER is the gate; this only stops the screen offering what the server would refuse. So when the ceiling
 * has not loaded (or failed to), nothing is blocked here and the server's 403 explains itself.
 */

const pick = (locale: string, en?: string | null, ar?: string | null): string | null =>
  (locale === 'ar' ? ar || en : en || ar) || null;

/** Why this caller cannot give or take away the role (localized), or null when they can. */
export function assignBlock(ceiling: AccessCeiling | null, roleId: string, locale: string): string | null {
  const role = ceiling?.roles.find((r) => r.roleId === roleId);
  if (!role || role.canAssign) return null;
  return pick(locale, role.assignRefusalEn, role.assignRefusalAr);
}

/** Why this caller cannot edit the role's definition (localized), or null when they can. */
export function editBlock(ceiling: AccessCeiling | null, roleId: string, locale: string): string | null {
  const role = ceiling?.roles.find((r) => r.roleId === roleId);
  if (!role || role.canEdit) return null;
  return pick(locale, role.editRefusalEn, role.editRefusalAr);
}

/** True when the caller may grant this permission (they hold it). Unknown ceiling: the server decides. */
export function canGrantPermission(ceiling: AccessCeiling | null, key: string): boolean {
  if (!ceiling) return true;
  const k = key.toLowerCase();
  return ceiling.heldPermissions.some((p) => p.toLowerCase() === k);
}

/** True when the target is the caller: the subject never decides their own access. */
export function isSelf(ceiling: AccessCeiling | null, userId: string): boolean {
  return !!ceiling && ceiling.userId.toLowerCase() === userId.toLowerCase();
}

/** The server's refusal sentence in the viewer's language (`message` / `messageAr`), or null. */
export function localizedRefusal(err: unknown, locale: string): string | null {
  const data = (err as { response?: { data?: { message?: unknown; messageAr?: unknown } } } | null)?.response?.data;
  const en = typeof data?.message === 'string' ? data.message : null;
  const ar = typeof data?.messageAr === 'string' ? data.messageAr : null;
  return pick(locale, en, ar);
}
