'use client';

import type { ReactNode } from 'react';
import { AlertTriangle, Inbox, RefreshCw } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { useFeatureFlags } from '../../contexts/FeatureFlagContext';
import { LEARNING_FLAG } from '../../lib/learning';

/**
 * Renders a learning screen only for tenants with the `learning` opt-in flag on. A direct link from a tenant without
 * it gets one plain sentence. The API refuses the same tenant (403 feature_not_enabled); this is the courtesy layer.
 */
export function LearningGate({ children }: { children: ReactNode }) {
  const { t } = useLocale();
  const { isLoading, isFeatureEnabled } = useFeatureFlags();
  if (isLoading) return <div className="h-24 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />;
  if (!isFeatureEnabled(LEARNING_FLAG)) {
    return (
      <div className="rounded-2xl border border-slate-200 bg-white p-6 text-sm text-slate-600 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-300" data-testid="learning-disabled">
        {t('Learning is not switched on for your organisation yet.')}
      </div>
    );
  }
  return <>{children}</>;
}

export function LoadingRows({ label, rows = 3 }: { label: string; rows?: number }) {
  return (
    <div className="space-y-3" aria-busy="true" aria-label={label} role="status">
      {Array.from({ length: rows }, (_, i) => <div key={i} className="h-16 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" />)}
    </div>
  );
}

export function ErrorBlock({ message, onRetry, retryLabel }: { message: string; onRetry?: () => void; retryLabel?: string }) {
  const { t } = useLocale();
  return (
    <div role="alert" className="flex flex-col items-center gap-3 rounded-2xl border border-amber-200 bg-amber-50 p-6 text-center dark:border-amber-500/20 dark:bg-amber-500/[0.06]">
      <AlertTriangle className="h-7 w-7 text-amber-500" aria-hidden="true" />
      <p className="max-w-lg text-sm font-medium text-amber-800 dark:text-amber-300">{message}</p>
      {onRetry && (
        <button type="button" onClick={onRetry} className="flex items-center gap-1.5 rounded-lg bg-amber-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-700">
          <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" />{retryLabel ?? t('Try again')}
        </button>
      )}
    </div>
  );
}

export function EmptyBlock({ title, body, action }: { title: string; body?: string; action?: ReactNode }) {
  return (
    <div className="flex flex-col items-center gap-2 rounded-2xl border border-dashed border-slate-300 p-8 text-center dark:border-white/15">
      <Inbox className="h-7 w-7 text-slate-400" aria-hidden="true" />
      <p className="text-sm font-semibold text-slate-700 dark:text-slate-200">{title}</p>
      {body && <p className="max-w-md text-xs text-slate-500 dark:text-slate-400">{body}</p>}
      {action}
    </div>
  );
}

/** A labelled progress bar; the number is always visible as text, never colour alone. */
export function ProgressBar({ percent, label }: { percent: number; label: string }) {
  const value = Math.max(0, Math.min(100, Math.round(percent)));
  return (
    <div className="flex items-center gap-2">
      <div className="h-2 flex-1 overflow-hidden rounded-full bg-slate-100 dark:bg-white/10" role="progressbar" aria-valuenow={value} aria-valuemin={0} aria-valuemax={100} aria-label={label}>
        <div className="h-full rounded-full bg-sapphire dark:bg-cyanAccent" style={{ width: `${value}%` }} />
      </div>
      <span className="w-10 text-end text-xs font-semibold tabular-nums text-slate-600 dark:text-slate-300">{value}%</span>
    </div>
  );
}

/** A field with a persistent label, optional hint and an error tied to it (aria-describedby). */
export function Field({ id, label, hint, error, children, optional }: { id: string; label: string; hint?: string; error?: string | null; optional?: boolean; children: ReactNode }) {
  const { t } = useLocale();
  return (
    <div className="space-y-1">
      <label htmlFor={id} className="block text-xs font-semibold text-slate-700 dark:text-slate-200">
        {label}{optional && <span className="ms-1 font-normal text-slate-400">{t('(optional)')}</span>}
      </label>
      {hint && <p id={`${id}-hint`} className="text-[11px] text-slate-500 dark:text-slate-400">{hint}</p>}
      {children}
      {error && <p id={`${id}-error`} role="alert" className="text-[11px] font-medium text-rose-600 dark:text-rose-400">{error}</p>}
    </div>
  );
}
