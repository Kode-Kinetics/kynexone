'use client';

import { useCallback, type ReactNode } from 'react';
import { AlertTriangle, RefreshCw } from 'lucide-react';
import Link from 'next/link';
import { essActionsApi } from '@/src/api/ess';
import { useAuth } from '@/src/contexts/AuthContext';
import { useLocale } from '@/src/contexts/LocaleContext';
import { formatCalendarDate } from '@/src/lib/calendarDate';
import { requestFailureReason } from '@/src/lib/requestFailure';

/** Shared pieces of the employee self-service pages (/ess/leave, /ess/overtime, /ess/requests). */

/**
 * The caller's own employee id: from the login when it carries one, otherwise as the server's ESS
 * context resolves it (a login linked to its employee by email). Only ever the caller's own id; the
 * server refuses any other for an Employee.
 */
/** A calendar date in the viewer's language (Gregorian, Western digits in Arabic, as the rest of the app). */
export function useEssDate(): (value: string | null | undefined) => string {
  const { locale } = useLocale();
  return useCallback((value) => (locale === 'ar' ? formatCalendarDate(value, 'ar-u-nu-latn') : formatCalendarDate(value)), [locale]);
}

/** Submitting needs ess.write; ess.read alone (an HR Assistant, for one) can look but not submit. */
export function useCanWriteEss(): boolean {
  const { hasPermission } = useAuth();
  return hasPermission('ess.write');
}

/** Shown in place of a form when the caller can view self-service but not submit. */
export function EssReadOnly() {
  const { t } = useLocale();
  return <EssEmpty text={t('Your account can view self-service but cannot send requests. Ask HR if you need to.')} />;
}

export function useOwnEmployeeId(): () => Promise<number> {
  const { user } = useAuth();
  const fromLogin = user?.employeeId;
  return useCallback(
    async () => (typeof fromLogin === 'number' && fromLogin > 0 ? fromLogin : essActionsApi.ownEmployeeId()),
    [fromLogin],
  );
}

export const essInput =
  'w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-800 focus:border-sapphire focus:outline-none dark:border-white/[0.12] dark:bg-white/[0.04] dark:text-slate-100';

export const essPrimaryButton =
  'inline-flex items-center justify-center gap-1.5 rounded-xl bg-sapphire px-4 py-2 text-sm font-semibold text-white hover:bg-sapphire/90 disabled:opacity-60 dark:bg-cyanAccent dark:text-slate-900';

export function EssPageHeader({ title, subtitle }: { title: string; subtitle: string }) {
  const { t } = useLocale();
  return (
    <div className="flex flex-wrap items-end justify-between gap-2">
      <div>
        <h1 className="text-lg font-bold text-slate-800 dark:text-slate-100">{title}</h1>
        <p className="text-xs text-slate-500 dark:text-slate-400">{subtitle}</p>
      </div>
      <Link href="/ess" className="text-xs font-semibold text-sapphire hover:underline dark:text-cyanAccent">{t('Back to Self-Service')}</Link>
    </div>
  );
}

export function EssCard({ title, children, testId }: { title: string; children: ReactNode; testId?: string }) {
  return (
    <section data-testid={testId} className="rounded-2xl border border-slate-200 bg-white p-4 dark:border-white/[0.06] dark:bg-white/[0.03]">
      <h2 className="mb-3 text-sm font-bold text-slate-800 dark:text-slate-100">{title}</h2>
      {children}
    </section>
  );
}

export function EssField({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block space-y-1">
      <span className="text-xs font-semibold text-slate-600 dark:text-slate-300">{label}</span>
      {children}
    </label>
  );
}

/** A failed load, with the reason and a way to retry. Never an empty list pretending nothing exists. */
export function EssLoadError({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  const { t } = useLocale();
  return (
    <div role="alert" className="flex flex-col items-center gap-3 rounded-2xl border border-amber-200 bg-amber-50 p-6 text-center dark:border-amber-500/20 dark:bg-amber-500/[0.06]">
      <AlertTriangle className="h-7 w-7 text-amber-500" />
      <p className="max-w-lg text-sm font-medium text-amber-800 dark:text-amber-300">{t(requestFailureReason(error))}</p>
      <button type="button" onClick={onRetry} className="flex items-center gap-1.5 rounded-lg bg-amber-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-700">
        <RefreshCw className="h-3.5 w-3.5" /> {t('Retry')}
      </button>
    </div>
  );
}

export function EssEmpty({ text }: { text: string }) {
  return <p className="rounded-xl bg-slate-50 p-4 text-center text-xs text-slate-500 dark:bg-white/[0.03] dark:text-slate-400">{text}</p>;
}

export function EssNotice({ tone, children }: { tone: 'ok' | 'error'; children: ReactNode }) {
  return (
    <p role={tone === 'error' ? 'alert' : 'status'}
      className={`rounded-xl p-3 text-xs font-medium ${tone === 'ok'
        ? 'bg-emerald-50 text-emerald-800 dark:bg-emerald-500/[0.08] dark:text-emerald-300'
        : 'bg-rose-50 text-rose-700 dark:bg-rose-500/[0.08] dark:text-rose-300'}`}>
      {children}
    </p>
  );
}
