// Route resolution — pure. Import-free (types only) so node --test can drive it with a policy from
// auth/accessPolicy.ts; routes.ts wraps it with deriveMobileAccess for the app.
import type { MobileAccessPolicy, MobileSurface } from '../auth/accessPolicy';
import type { AuthUser } from '../types';

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
  | 'SelfieConsent'
  | 'Letters'
  | 'Loans'
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
  // Consent to selfie attendance lives beside Settings (More stack); withdrawal must always be reachable.
  SelfieConsent: 'settings',
  // HR letters and My loans live in the More stack (lane C). Other lanes navigate here with
  // navigateTo(navigation, 'Letters' | 'Loans', user).
  Letters: 'letters',
  Loans: 'loans',
  Account: 'account',
};

export type Target = { name: string; params?: object };

export function isRouteAllowed(
  route: AppRoute,
  policy: MobileAccessPolicy,
): boolean {
  const required = route === 'Home' && policy.surfaces.has('managerHome')
    ? 'managerHome'
    : ROUTE_SURFACE[route];
  return policy.surfaces.has(required);
}

function more(screen: string, params?: Record<string, unknown>): Target {
  return { name: 'More', params: { screen, params, initial: false } };
}

function accountTarget(user: AuthUser | null | undefined, policy: MobileAccessPolicy): Target {
  const specialist = policy.mode === 'PayrollPortal' || policy.mode === 'FinancePortal'
    || user?.role === 'PAYROLL' || user?.role === 'FINANCE_APPROVER';
  const hasPrimaryHome = policy.surfaces.has('employeeHome') || policy.surfaces.has('managerHome');
  if (policy.mode === 'KioskOnly' || policy.mode === 'ReadOnlyAuditor' || (!hasPrimaryHome && !specialist)) {
    return { name: 'Account' };
  }
  return more('Account');
}

/**
 * Resolves only registered routes; denied deep links are redirected to Account. Params always travel
 * with the target — a tab screen reads them from its own route (e.g. Leave { segment }).
 */
export function resolveRouteFor(
  route: AppRoute,
  params: Record<string, unknown> | undefined,
  user: AuthUser | null | undefined,
  policy: MobileAccessPolicy,
): Target {
  if (!isRouteAllowed(route, policy)) return accountTarget(user, policy);

  const manager = policy.surfaces.has('managerHome');
  const specialist = policy.mode === 'PayrollPortal' || policy.mode === 'FinancePortal'
    || user?.role === 'PAYROLL' || user?.role === 'FINANCE_APPROVER';
  switch (route) {
    case 'Home': return { name: 'Home' };
    case 'KioskPunch': return { name: 'Punch' };
    case 'Account': return accountTarget(user, policy);
    case 'AttendanceHistory': return { name: 'Attendance' };
    case 'Approvals': return { name: 'Approvals', params };
    case 'Team': return { name: 'Team', params };
    // Employees have a Leave tab; managers and specialists open the leave screen from More.
    case 'ApplyLeave': return manager || specialist ? more('ApplyLeave', params) : { name: 'Leave', params };
    case 'Payslips': return manager ? more('PayslipsList', params) : { name: 'Payslips', params: { screen: 'PayslipsList', params } };
    case 'PayslipDetail':
      return manager
        ? more('PayslipDetail', params)
        : { name: 'Payslips', params: { screen: 'PayslipDetail', params, initial: false } };
    default: return more(route, params);
  }
}
