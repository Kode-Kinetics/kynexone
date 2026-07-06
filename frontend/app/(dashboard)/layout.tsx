'use client';

import { useEffect, useState } from 'react';
import { useRouter } from 'next/navigation';
import { useAuth } from '@/src/contexts/AuthContext';
import { AppLayout } from '@/src/layouts/AppLayout';
import { TenantSettingsProvider } from '@/src/contexts/TenantSettingsContext';
import { CurrentCompanyProvider } from '@/src/contexts/CompanyContext';
import { applyTheme, getStoredTheme } from '@/src/utils/theme';
import type { ThemeMode } from '@/src/types/ui';

function Shell({ children }: { children: React.ReactNode }) {
  const { user, isLoading } = useAuth();
  const router = useRouter();
  const [theme, setTheme] = useState<ThemeMode>(() => getStoredTheme());

  useEffect(() => { applyTheme(theme); }, [theme]);

  useEffect(() => {
    if (!isLoading && !user) router.replace('/login');
  }, [isLoading, user, router]);

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
          {children}
        </AppLayout>
      </CurrentCompanyProvider>
    </TenantSettingsProvider>
  );
}

export default function DashboardLayout({ children }: { children: React.ReactNode }) {
  return <Shell>{children}</Shell>;
}
