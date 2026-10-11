import { createNavigationContainerRef } from '@react-navigation/native';
import { deriveMobileAccess } from '@/auth/accessPolicy';
import { resolveRouteFor, type AppRoute, type Target } from './routeResolver';
import type { AuthUser } from '@/types';

export const navigationRef = createNavigationContainerRef<any>();

export type { AppRoute, Target } from './routeResolver';

export function isManagerUser(user: AuthUser | null | undefined): boolean {
  return deriveMobileAccess(user).surfaces.has('managerHome');
}

/** Resolves only registered routes; denied deep links are redirected to Account. */
export function resolveRoute(
  route: AppRoute,
  params: Record<string, unknown> | undefined,
  user: AuthUser | null | undefined
): Target {
  return resolveRouteFor(route, params, user, deriveMobileAccess(user));
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
