import { expect, test } from '@playwright/test';
import { navItemRoleAllowed, GROUP_OVERVIEW_ROLES } from '../src/lib/navRoles';
import { navigationGroups } from '../src/routes/navigation';

/** The Group Overview menu entry is hidden from roles the Group Overview API refuses (visibility only: the API is unchanged). */
test.describe('Group Overview is offered only to roles the API serves', () => {
  test('the role list mirrors GroupDashboardController', () => {
    expect([...GROUP_OVERVIEW_ROLES].sort()).toEqual(['Admin', 'Auditor', 'Compliance Officer', 'Finance Approver', 'HR Director', 'HR Manager']);
  });
  test('a plain Employee does not see it, an HR Manager does; items without roles stay visible', () => {
    expect(navItemRoleAllowed(GROUP_OVERVIEW_ROLES, r => r === 'Employee')).toBe(false);
    expect(navItemRoleAllowed(GROUP_OVERVIEW_ROLES, r => r === 'HR Manager')).toBe(true);
    expect(navItemRoleAllowed(undefined, () => false)).toBe(true);
  });
  test('the Group Overview menu entry carries that role list', () => {
    const item = navigationGroups.flatMap(g => g.items).find(i => i.path === '/group');
    expect(item?.requiredRoles).toEqual([...GROUP_OVERVIEW_ROLES]);
  });
});
