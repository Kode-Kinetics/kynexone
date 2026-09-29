import type { AccessMode, AuthUser, Permission } from '../types';

export const BACKEND_ACCESS_MODES: readonly AccessMode[] = [
  'FullPortal',
  'ESSOnly',
  'ManagerPortal',
  'HRPortal',
  'PayrollPortal',
  'FinancePortal',
  'SupervisorPortal',
  'ReadOnlyAuditor',
  'Mobile',
  'KioskOnly',
  'NoLogin',
] as const;

const ACCESS_MODE_BY_KEY = new Map(
  BACKEND_ACCESS_MODES.map((mode) => [mode.toLowerCase(), mode] as const)
);

/** Unknown, missing, or future server modes deliberately fail closed. */
export function normalizeAccessMode(value: unknown): AccessMode {
  if (typeof value !== 'string') return 'NoLogin';
  return ACCESS_MODE_BY_KEY.get(value.trim().toLowerCase()) ?? 'NoLogin';
}

function permissionKey(permission: Permission): string[] {
  const module = permission.module.trim().toLowerCase();
  return permission.actions.map((action) => `${module}.${action.trim().toLowerCase()}`);
}

export function effectivePermissionKeys(user: AuthUser | null | undefined): Set<string> {
  return new Set((user?.permissions ?? []).flatMap(permissionKey));
}

export function hasEffectivePermission(
  user: AuthUser | null | undefined,
  ...required: string[]
): boolean {
  if (!user) return false;
  const keys = effectivePermissionKeys(user);
  return required.some((permission) => {
    const normalized = permission.toLowerCase();
    const module = normalized.split('.')[0];
    if (normalized.endsWith('.*')) {
      return Array.from(keys).some((key) => key.startsWith(`${module}.`));
    }
    return keys.has(normalized) || keys.has(`${module}.*`) || keys.has('*.*');
  });
}

export type MobileSurface =
  | 'employeeHome'
  | 'managerHome'
  | 'team'
  | 'approvals'
  | 'attendance'
  | 'attendanceCorrection'
  | 'kioskPunch'
  | 'leave'
  | 'payslips'
  | 'profile'
  | 'documents'
  | 'hrRequests'
  | 'notifications'
  | 'aiAssistant'
  | 'overtime'
  | 'settings'
  | 'account';

export interface MobileAccessPolicy {
  mode: AccessMode;
  readOnly: boolean;
  surfaces: ReadonlySet<MobileSurface>;
  landing: 'Home' | 'Team' | 'Approvals' | 'Punch' | 'Attendance' | 'Account';
}

const ESS_READ_SURFACES: readonly MobileSurface[] = [
  'employeeHome',
  'attendance',
  'leave',
  'payslips',
  'profile',
  'documents',
  'hrRequests',
  'notifications',
  'aiAssistant',
  'overtime',
  'settings',
];

const MANAGER_MODES: readonly AccessMode[] = [
  'ManagerPortal',
  'HRPortal',
  'SupervisorPortal',
  'FullPortal',
];

/**
 * The access mode is a ceiling, never a permission grant. A surface is exposed
 * only when both the mode and the caller's effective permission permit it.
 */
export function deriveMobileAccess(user: AuthUser | null | undefined): MobileAccessPolicy {
  const mode = normalizeAccessMode(user?.accessMode);
  const surfaces = new Set<MobileSurface>(['account']);
  const readOnly = mode === 'ReadOnlyAuditor' || mode === 'NoLogin';
  if (!user || mode === 'NoLogin') return { mode, readOnly, surfaces, landing: 'Account' };

  const essRead = hasEffectivePermission(user, 'ess.read', 'ess.write');
  const essWrite = hasEffectivePermission(user, 'ess.write');
  const managerRead = hasEffectivePermission(user, 'manager.read');
  const approvalsRead = hasEffectivePermission(user, 'approvals.read', 'approvals.decide');
  const attendanceRead = hasEffectivePermission(user, 'attendance.read', 'ess.read', 'ess.write');
  const attendanceKiosk = hasEffectivePermission(user, 'attendance.kiosk');

  if (mode === 'KioskOnly') {
    if (attendanceKiosk) {
      surfaces.add('kioskPunch');
      surfaces.add('attendance');
      return { mode, readOnly, surfaces, landing: 'Punch' };
    }
    return { mode, readOnly, surfaces, landing: 'Account' };
  }

  if (mode === 'ReadOnlyAuditor') {
    if (attendanceRead) surfaces.add('attendance');
    if (managerRead) surfaces.add('team');
    if (approvalsRead) surfaces.add('approvals');
    const landing = surfaces.has('approvals')
      ? 'Approvals'
      : surfaces.has('attendance')
        ? 'Attendance'
        : surfaces.has('team')
          ? 'Team'
          : 'Account';
    return { mode, readOnly, surfaces, landing };
  }

  const allowsEss = ['FullPortal', 'ESSOnly', 'ManagerPortal', 'HRPortal', 'PayrollPortal',
    'FinancePortal', 'SupervisorPortal', 'Mobile'].includes(mode);
  if (allowsEss && essRead) ESS_READ_SURFACES.forEach((surface) => surfaces.add(surface));
  if (allowsEss && essWrite) surfaces.add('attendanceCorrection');
  if (allowsEss && attendanceRead) surfaces.add('attendance');

  if (MANAGER_MODES.includes(mode) && managerRead) {
    surfaces.add('managerHome');
    surfaces.add('team');
    surfaces.delete('employeeHome');
  }
  if (['FullPortal', 'ManagerPortal', 'HRPortal', 'PayrollPortal', 'FinancePortal',
    'SupervisorPortal'].includes(mode) && approvalsRead) {
    surfaces.add('approvals');
  }

  const specialist = mode === 'PayrollPortal' || mode === 'FinancePortal'
    || user.role === 'PAYROLL' || user.role === 'FINANCE_APPROVER';
  if (specialist && surfaces.has('approvals')) {
    return { mode, readOnly, surfaces, landing: 'Approvals' };
  }
  if (surfaces.has('managerHome')) return { mode, readOnly, surfaces, landing: 'Home' };
  if (surfaces.has('employeeHome')) return { mode, readOnly, surfaces, landing: 'Home' };
  if (surfaces.has('approvals')) return { mode, readOnly, surfaces, landing: 'Approvals' };
  if (surfaces.has('attendance')) return { mode, readOnly, surfaces, landing: 'Attendance' };
  return { mode, readOnly, surfaces, landing: 'Account' };
}

export function canOpenSurface(
  user: AuthUser | null | undefined,
  surface: MobileSurface
): boolean {
  return deriveMobileAccess(user).surfaces.has(surface);
}
