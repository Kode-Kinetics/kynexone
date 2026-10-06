'use client';

import type { ReactNode } from 'react';
import { useLocale } from '../../contexts/LocaleContext';
import { useFeatureFlags } from '../../contexts/FeatureFlagContext';
import { useReleaseA } from '../../lib/releaseA';

/**
 * Renders a Release A screen only for tenants with the release_a flag on. A direct link from a tenant without
 * it gets one plain sentence, never an empty or half-working screen. The API refuses the same tenant (403
 * feature_not_enabled), so this is the courtesy layer, not the gate.
 */
export function ReleaseAGate({ children }: { children: ReactNode }) {
  const { t } = useLocale();
  const { isLoading } = useFeatureFlags();
  const enabled = useReleaseA();
  if (isLoading) return <div className="h-24 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />;
  if (!enabled) {
    return (
      <div className="rounded-2xl border border-slate-200 bg-white p-6 text-sm text-slate-600 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-300">
        {t('This feature is not enabled for your account yet.')}
      </div>
    );
  }
  return <>{children}</>;
}

/** The body a Release A screen shows until its slice ships: what the screen is for, and that it opens here. */
export function ReleaseAPlaceholder({ title, purpose }: { title: string; purpose: string }) {
  const { t } = useLocale();
  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-lg font-bold text-slate-800 dark:text-slate-100">{t(title)}</h1>
        <p className="text-xs text-slate-500 dark:text-slate-400">{t(purpose)}</p>
      </div>
      <div className="rounded-2xl border border-dashed border-slate-300 p-6 text-sm text-slate-500 dark:border-white/15 dark:text-slate-400">
        {t('This screen is being prepared and will open here.')}
      </div>
    </div>
  );
}
