'use client';

import { usePathname } from 'next/navigation';
import { AuthProvider } from '@/src/contexts/AuthContext';
import { FeatureFlagProvider } from '@/src/contexts/FeatureFlagContext';
import { HelpTextProvider } from '@/src/contexts/HelpTextContext';
import { AppToastProvider } from '@/src/components/ui/AppToast';
import { ChunkErrorReloader } from '@/src/components/ChunkErrorReloader';
import { LicenceGraceBanner } from '@/src/components/LicenceGraceBanner';
import { MaintenanceNotice } from '@/src/components/MaintenanceNotice';

/**
 * Public credential routes mount no auth stack; /login and /welcome mount only AuthProvider; every
 * other route gets the full tenant stack. ChunkErrorReloader + AppToastProvider stay global (truly
 * app-wide concerns).
 */
export function Providers({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  // /welcome signs the employee in straight after they choose a password, so it needs the same
  // minimal auth stack as /login, and nothing that fires protected bootstrap requests.
  const isLogin = pathname === '/login' || pathname === '/welcome';
  const isPublicCredentialRoute = pathname === '/reset-password' || pathname === '/accept-invitation';

  return (
    <AppToastProvider>
      <ChunkErrorReloader />
      <MaintenanceNotice />
      {isPublicCredentialRoute ? (
        children
      ) : isLogin ? (
        // Login needs AuthProvider for its local sign-in/MFA state, but must not
        // mount feature/help providers that issue protected bootstrap requests
        // from an old session and can refresh, clear storage, or redirect.
        <AuthProvider>{children}</AuthProvider>
      ) : (
        <AuthProvider>
          <FeatureFlagProvider>
            <HelpTextProvider>
              <LicenceGraceBanner />
              {children}
            </HelpTextProvider>
          </FeatureFlagProvider>
        </AuthProvider>
      )}
    </AppToastProvider>
  );
}
