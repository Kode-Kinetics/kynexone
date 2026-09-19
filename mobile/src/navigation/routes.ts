// ============================================================
// KynexOne Mobile — Cross-tab navigation
// ============================================================
//
// Screens live in per-tab stacks, so navigation.navigate('Notifications') from
// the Home stack is silently unhandled: no navigator above Home knows that name.
// Every cross-tab jump goes through navigateTo(), which knows which tab (and
// which nested stack) owns each screen for the current user's tab layout.

import { createNavigationContainerRef } from '@react-navigation/native';
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
  | 'Team';

/** Managers, supervisors, HR and admins get the manager tab layout (Team + Approvals). */
export function isManagerUser(user: AuthUser | null | undefined): boolean {
  if (!user) return false;
  if (['MANAGER', 'SUPERVISOR', 'HR', 'SUPER_ADMIN'].includes(user.role)) return true;
  return user.permissions.some(
    (p) => p.module === 'approvals' && (p.actions.includes('decide') || p.actions.includes('*'))
  );
}

type Target = { name: string; params?: object };

export function resolveRoute(
  route: AppRoute,
  params: Record<string, unknown> | undefined,
  manager: boolean
): Target {
  const more = (screen: string): Target => ({ name: 'More', params: { screen, params, initial: false } });
  switch (route) {
    case 'Home':
      return { name: 'Home' };
    case 'AttendanceHistory':
      return { name: 'Attendance' };
    case 'Approvals':
      return manager ? { name: 'Approvals', params } : more('Notifications');
    case 'Team':
      return manager ? { name: 'Team' } : { name: 'Home' };
    case 'ApplyLeave':
      return manager ? more('ApplyLeave') : { name: 'Leave' };
    case 'Payslips':
      return manager ? more('PayslipsList') : { name: 'Payslips', params: { screen: 'PayslipsList' } };
    case 'PayslipDetail':
      return manager
        ? more('PayslipDetail')
        : { name: 'Payslips', params: { screen: 'PayslipDetail', params, initial: false } };
    default:
      return more(route);
  }
}

/** Navigate from inside any screen. */
export function navigateTo(
  navigation: { navigate: (name: string, params?: object) => void },
  route: AppRoute,
  manager: boolean,
  params?: Record<string, unknown>
) {
  const target = resolveRoute(route, params, manager);
  navigation.navigate(target.name, target.params);
}

/** Navigate from outside the React tree (push notification taps). */
export function navigateFromRoot(route: AppRoute, manager: boolean, params?: Record<string, unknown>): boolean {
  if (!navigationRef.isReady()) return false;
  const target = resolveRoute(route, params, manager);
  navigationRef.navigate(target.name, target.params);
  return true;
}
