'use client';

import { usePathname } from 'next/navigation';
import { AuthProvider } from '@/src/contexts/AuthContext';
import { FeatureFlagProvider } from '@/src/contexts/FeatureFlagContext';
import { HelpTextProvider } from '@/src/contexts/HelpTextContext';
import { AppToastProvider } from '@/src/components/ui/AppToast';
import { ChunkErrorReloader } from '@/src/components/ChunkErrorReloader';

/**
 * The platform console (/platform/*) is a SEPARATE application: it has its own
 * axios client, its own token (platform_access_token), and its own auth flow
 * (platform.ts + PlatformShell). It must NOT mount the tenant auth stack.
 *
 * Mounting AuthProvider/FeatureFlagProvider globally caused them to bootstrap
 * tenant API calls (authApi.me(), feature flags) on platform routes too. With an
 * inactive tenant subscription those calls returned 402, and the tenant client
 * interceptor redirected the platform console to /tenant-admin — hijacking it.
 * Scoping the tenant providers to non-platform routes fixes that at the root: on
 * /platform, no tenant API calls fire at all.
 *
 * ChunkErrorReloader + AppToastProvider stay global (truly app-wide concerns).
 */
export function Providers({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const isPlatform = pathname?.startsWith('/platform') ?? false;

  return (
    <AppToastProvider>
      <ChunkErrorReloader />
      {isPlatform ? (
        children
      ) : (
        <AuthProvider>
          <FeatureFlagProvider>
            <HelpTextProvider>{children}</HelpTextProvider>
          </FeatureFlagProvider>
        </AuthProvider>
      )}
    </AppToastProvider>
  );
}
