// ============================================================
// ZAYRA MOBILE — Permission Hooks & Guards
// ============================================================

import { useAuthStore } from '@/auth/authStore';
import type { UserRole, AccessMode } from '@/types';

/** Returns true if current user has a specific permission */
export function usePermission(module: string, action: string): boolean {
  return useAuthStore((s) => s.hasPermission(module, action));
}

/** Returns true if current user can access a module at all */
export function useCanAccess(module: string): boolean {
  return useAuthStore((s) => s.canAccess(module));
}

/** Returns current user's role */
export function useUserRole(): UserRole | null {
  return useAuthStore((s) => s.user?.role ?? null);
}

/** Returns current user's access mode */
export function useAccessMode(): AccessMode | null {
  return useAuthStore((s) => s.user?.accessMode ?? null);
}

/** Role-based guards */
export function useIsEmployee(): boolean {
  return useAuthStore((s) => s.user?.role === 'EMPLOYEE' || s.user?.role === 'GROUND_STAFF');
}

export function useIsSupervisor(): boolean {
  return useAuthStore((s) => s.user?.role === 'SUPERVISOR');
}

export function useIsManager(): boolean {
  return useAuthStore(
    (s) =>
      s.user?.role === 'MANAGER' ||
      s.user?.role === 'HR' ||
      s.user?.role === 'PAYROLL' ||
      s.user?.role === 'FINANCE_APPROVER'
  );
}

export function useIsApprover(): boolean {
  return useAuthStore((s) =>
    ['MANAGER', 'SUPERVISOR', 'HR', 'PAYROLL', 'FINANCE_APPROVER'].includes(s.user?.role ?? '')
  );
}

export function useCanApprovePayroll(): boolean {
  return useAuthStore(
    (s) =>
      (s.user?.role === 'PAYROLL' || s.user?.role === 'FINANCE_APPROVER') &&
      s.hasPermission('payroll', 'approve')
  );
}

export function useCanViewPayrollData(): boolean {
  return useAuthStore(
    (s) =>
      s.user?.role === 'PAYROLL' ||
      s.user?.role === 'FINANCE_APPROVER' ||
      s.hasPermission('payroll', 'read')
  );
}

/** Check if user is in kiosk/attendance-only mode */
export function useIsKioskMode(): boolean {
  return useAuthStore(
    (s) =>
      s.user?.accessMode === 'KioskOnly'
  );
}

// Module-level access checks
export const MODULE_PERMISSIONS = {
  ATTENDANCE: 'attendance',
  LEAVE: 'leave',
  OVERTIME: 'overtime',
  PAYSLIPS: 'payslips',
  PROFILE: 'profile',
  DOCUMENTS: 'documents',
  HR_REQUESTS: 'hr_requests',
  APPROVALS: 'approvals',
  NOTIFICATIONS: 'notifications',
  AI_ASSISTANT: 'ai_assistant',
  TEAM: 'team',
  REPORTS: 'reports',
} as const;
