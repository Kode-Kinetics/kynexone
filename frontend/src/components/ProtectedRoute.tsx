'use client';

import { useEffect } from 'react';
import { useRouter } from 'next/navigation';
import { useAuth } from '@/src/contexts/AuthContext';
import { ServerUnreachable } from '@/src/components/ServerUnreachable';

interface ProtectedRouteProps {
  children: React.ReactNode;
  requiredPermissions?: string[];
}

export function ProtectedRoute({ children, requiredPermissions }: ProtectedRouteProps) {
  const { user, isLoading, authError, retryAuth, logout, hasPermission } = useAuth();
  const router = useRouter();

  // Not a 401 (offline, a deploy's 502s): keep the session and show the offline state, never /login.
  useEffect(() => {
    if (!isLoading && !user && !authError) router.replace('/login');
  }, [isLoading, user, authError, router]);

  useEffect(() => {
    if (user && requiredPermissions?.length) {
      const hasAccess = requiredPermissions.some(p => hasPermission(p));
      if (!hasAccess) router.replace('/dashboard');
    }
  }, [user, requiredPermissions, hasPermission, router]);

  if (!isLoading && !user && authError) return <ServerUnreachable reason={authError} onRetry={retryAuth} onSignOut={logout} />;

  if (isLoading || !user) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-lightBg dark:bg-midnight">
        <div className="h-8 w-8 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
      </div>
    );
  }

  if (requiredPermissions?.length && !requiredPermissions.some(p => hasPermission(p))) {
    return null;
  }

  return <>{children}</>;
}
