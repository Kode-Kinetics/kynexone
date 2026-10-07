import { test, expect } from '@playwright/test';
import type { AccessCeiling } from '../src/api/identity';
import { assignBlock, canGrantPermission, editBlock, isSelf, localizedRefusal } from '../src/lib/accessCeiling';

// The Access screen greys out exactly what the server's privilege ceiling refuses, in the viewer's language.
// The reasons are the server's sentences, so the screen and the 403 never disagree.

const ceiling: AccessCeiling = {
  userId: 'AAAA-1',
  isAdmin: false,
  heldPermissions: ['security.manage', 'employees.read'],
  roles: [
    {
      roleId: 'admin', name: 'Admin', canAssign: false, assignRefusalCode: 'access_admin_only_role',
      assignRefusalEn: "Only an Admin can give or remove the 'Admin' role.", assignRefusalAr: "لا يمكن منح دور 'Admin' أو إزالته إلا من قِبل مسؤول النظام (Admin).",
      canEdit: false, editRefusalCode: 'access_protected_role', editRefusalEn: 'protected', editRefusalAr: 'محمي',
    },
    { roleId: 'emp', name: 'Employee', canAssign: true, canEdit: true },
  ],
};

test('a role above the ceiling is blocked with the server reason, in the viewer language', () => {
  expect(assignBlock(ceiling, 'admin', 'en')).toBe("Only an Admin can give or remove the 'Admin' role.");
  expect(assignBlock(ceiling, 'admin', 'ar')).toContain('مسؤول');
  expect(editBlock(ceiling, 'admin', 'ar')).toBe('محمي');
  expect(assignBlock(ceiling, 'emp', 'en')).toBeNull();
  expect(editBlock(ceiling, 'emp', 'en')).toBeNull();
});

test('an unloaded ceiling blocks nothing: the server stays the gate and explains its 403', () => {
  expect(assignBlock(null, 'admin', 'en')).toBeNull();
  expect(canGrantPermission(null, 'payroll.approve')).toBe(true);
  expect(isSelf(null, 'AAAA-1')).toBe(false);
});

test('permissions and self are judged like the server judges them', () => {
  expect(canGrantPermission(ceiling, 'Employees.Read')).toBe(true);
  expect(canGrantPermission(ceiling, 'payroll.approve')).toBe(false);
  expect(isSelf(ceiling, 'aaaa-1')).toBe(true);
});

test('a refusal reads in Arabic for an Arabic viewer, English otherwise', () => {
  const err = { response: { status: 403, data: { error: 'access_self_change', message: 'You cannot change your own roles.', messageAr: 'لا يمكنك تغيير أدوارك.' } } };
  expect(localizedRefusal(err, 'ar')).toBe('لا يمكنك تغيير أدوارك.');
  expect(localizedRefusal(err, 'en')).toBe('You cannot change your own roles.');
  expect(localizedRefusal({ response: { data: {} } }, 'en')).toBeNull();
});
