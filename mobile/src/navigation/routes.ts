import { createNavigationContainerRef } from '@react-navigation/native';
import { deriveMobileAccess, type MobileSurface } from '@/auth/accessPolicy';
import type { AuthUser } from '@/types';

export const navigationRef = createNavigationContainerRef<any>();

export type AppRoute =
  | 'Home'
  | 'Notifications'
  | 'AIAssistant'
  | 'Documents'
  | 'HRRequests'
  | 'HRRequestDetail'
  | 'Profile'
  | 'Settings'
  | 'Overtime'
  | 'ApplyLeave'
  | 'Payslips'
  | 'PayslipDetail'
  | 'AttendanceHistory'
  | 'AttendanceCorrection'
  | 'Approvals'
  | 'Team'
  | 'KioskPunch'
  | 'Account';

const ROUTE_SURFACE: Record<AppRoute, MobileSurface> = {
  Home: 'employeeHome',
  Notifications: 'notifications',
  AIAssistant: 'aiAssistant',
  Documents: 'documents',
  HRRequests: 'hrRequests',
  HRRequestDetail: 'hrRequests',
  Profile: 'profile',
  Settings: 'settings',
  Overtime: 'overtime',
  ApplyLeave: 'leave',
  Payslips: 'payslips',
  PayslipDetail: 'payslips',
  AttendanceHistory: 'attendance',
  AttendanceCorrection: 'attendanceCorrection',
  Approvals: 'approvals',
  Team: 'team',
  KioskPunch: 'kioskPunch',
  Account: 'account',
};

export function isManagerUser(user: AuthUser | null | undefined): boolean {
  return deriveMobileAccess(user).surfaces.has('managerHome');
}

type Target = { name: string; params?: object };

function more(screen: string, params?: Record<string, unknown>): Target {
  return { name: 'More', params: { screen, params, initial: false } };
}

function accountTarget(user: AuthUser | null | undefined): Target {
  const policy = deriveMobileAccess(user);
  const specialist = policy.mode === 'PayrollPortal' || policy.mode === 'FinancePortal'
    || user?.role === 'PAYROLL' || user?.role === 'FINANCE_APPROVER';
  const hasPrimaryHome = policy.surfaces.has('employeeHome') || policy.surfaces.has('managerHome');
  if (policy.mode === 'KioskOnly' || policy.mode === 'ReadOnlyAuditor' || (!hasPrimaryHome && !specialist)) {
    return { name: 'Account' };
  }
  return more('Account');
}

/** Resolves only registered routes; denied deep links are redirected to Account. */
export function resolveRoute(
  route: AppRoute,
  params: Record<string, unknown> | undefined,
  user: AuthUser | null | undefined
): Target {
  const policy = deriveMobileAccess(user);
  const required = route === 'Home' && policy.surfaces.has('managerHome')
    ? 'managerHome'
    : ROUTE_SURFACE[route];
  if (!policy.surfaces.has(required)) return accountTarget(user);

  const manager = policy.surfaces.has('managerHome');
  const specialist = policy.mode === 'PayrollPortal' || policy.mode === 'FinancePortal'
    || user?.role === 'PAYROLL' || user?.role === 'FINANCE_APPROVER';
  switch (route) {
    case 'Home': return { name: 'Home' };
    case 'KioskPunch': return { name: 'Punch' };
    case 'Account': return accountTarget(user);
    case 'AttendanceHistory': return { name: 'Attendance' };
    case 'Approvals': return { name: 'Approvals', params };
    case 'Team': return { name: 'Team', params };
    case 'ApplyLeave': return manager || specialist ? more('ApplyLeave', params) : { name: 'Leave' };
    case 'Payslips': return manager ? more('PayslipsList', params) : { name: 'Payslips', params: { screen: 'PayslipsList' } };
    case 'PayslipDetail':
      return manager
        ? more('PayslipDetail', params)
        : { name: 'Payslips', params: { screen: 'PayslipDetail', params, initial: false } };
    default: return more(route, params);
  }
}

export function navigateTo(
  navigation: { navigate: (name: string, params?: object) => void },
  route: AppRoute,
  user: AuthUser | null | undefined,
  params?: Record<string, unknown>
) {
  const target = resolveRoute(route, params, user);
  navigation.navigate(target.name, target.params);
}

export function navigateFromRoot(
  route: AppRoute,
  user: AuthUser | null | undefined,
  params?: Record<string, unknown>
): boolean {
  if (!navigationRef.isReady()) return false;
  const target = resolveRoute(route, params, user);
  navigationRef.navigate(target.name, target.params);
  return true;
}
