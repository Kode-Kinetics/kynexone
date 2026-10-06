'use client';

import { useEffect, useState } from 'react';
import { useRouter } from 'next/navigation';
import { useAuth } from '@/src/contexts/AuthContext';
import { AppLayout } from '@/src/layouts/AppLayout';
import { ModuleGate } from '@/src/components/ModuleGate';
import { ServerUnreachable } from '@/src/components/ServerUnreachable';
import { TenantSettingsProvider } from '@/src/contexts/TenantSettingsContext';
import { CurrentCompanyProvider } from '@/src/contexts/CompanyContext';
import { applyTheme, getStoredTheme } from '@/src/utils/theme';
import type { ThemeMode } from '@/src/types/ui';

function Shell({ children }: { children: React.ReactNode }) {
  const { user, isLoading, authError, retryAuth, logout } = useAuth();
  const router = useRouter();
  const [theme, setTheme] = useState<ThemeMode>(() => getStoredTheme());

  useEffect(() => { applyTheme(theme); }, [theme]);

  // A failed session check that was NOT a 401 (offline, a deploy's 502s) keeps the session and
  // shows the offline state below; only a signed-out user is sent to /login.
  useEffect(() => {
    if (!isLoading && !user && !authError) router.replace('/login');
  }, [isLoading, user, authError, router]);

  if (!isLoading && !user && authError) return <ServerUnreachable reason={authError} onRetry={retryAuth} onSignOut={logout} />;

  if (isLoading || !user) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-lightBg dark:bg-midnight">
        <div className="h-8 w-8 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
      </div>
    );
  }

  return (
    <TenantSettingsProvider>
      <CurrentCompanyProvider>
        <AppLayout theme={theme} onToggleTheme={() => setTheme(t => t === 'dark' ? 'light' : 'dark')}>
          {/* Inside AppLayout so a switched-off module keeps the chrome and the user can navigate
              away, rather than dropping them on a bare page. */}
          <ModuleGate>{children}</ModuleGate>
        </AppLayout>
      </CurrentCompanyProvider>
    </TenantSettingsProvider>
  );
}

export default function DashboardLayout({ children }: { children: React.ReactNode }) {
  return <Shell>{children}</Shell>;
}
