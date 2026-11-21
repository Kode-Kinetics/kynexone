/** The roles the Group Overview API (GroupDashboardController) answers; anyone else would open a page that says "no access". */
export const GROUP_OVERVIEW_ROLES = ['Admin', 'HR Director', 'HR Manager', 'Finance Approver', 'Compliance Officer', 'Auditor'] as const;

/** A menu entry with a role list shows only to someone holding one of them; an entry without one is unaffected. */
export function navItemRoleAllowed(requiredRoles: readonly string[] | undefined, hasRole: (role: string) => boolean): boolean {
  if (!requiredRoles || requiredRoles.length === 0) return true;
  return requiredRoles.some(hasRole);
}
