'use client';

import { isolateLtr, isolateName } from '../lib/bidi';
import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { Loader2, RefreshCw, WalletCards } from 'lucide-react';
import { payApprovalsApi, type PayApprovalList } from '../api/payApprovals';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';
import { maskedAccount } from '../lib/payApprovals';
import { useAuth } from '../contexts/AuthContext';

/** Pay approvals: the new-hire pay packages the signed-in approver may decide, narrowest view of each (OD4). */
export function PayApprovalsPage() {
  const { t } = useLocale();
  const fmt = useFormat();
  const { hasPermission } = useAuth();
  const [data, setData] = useState<PayApprovalList | null>(null);
  const [error, setError] = useState(false);
  const [loading, setLoading] = useState(true);
  const when = (iso: string) => fmt.date(iso);

  const load = useCallback(async () => {
    setLoading(true); setError(false);
    try { setData(await payApprovalsApi.list()); } catch { setError(true); } finally { setLoading(false); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  return (
    <div className="mx-auto max-w-5xl space-y-4">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h1 className="flex items-center gap-2 text-xl font-semibold text-slate-900 dark:text-white"><WalletCards className="h-5 w-5 shrink-0" aria-hidden="true" />{t('Pay approvals')}</h1>
          <p className="mt-1 text-sm text-slate-600 dark:text-slate-300">{t('New-hire pay packages waiting for your approval. You see only the figures and bank details needed to decide.')}</p>
          <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{t('Packages you entered yourself are not listed: someone else must approve them.')}</p>
        </div>
        <button type="button" className="btn-secondary" onClick={() => void load()} disabled={loading}>
          <RefreshCw className="h-4 w-4" aria-hidden="true" />{t('Reload')}
        </button>
      </header>

      <div aria-live="polite">
        {data && !loading && (data.requiresOtherReviewerCount ?? 0) > 0 && <p className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-900 dark:border-amber-800 dark:bg-amber-900/20 dark:text-amber-200">
          {t('{count} packages need another reviewer because you are not independent of them.', { count: data.requiresOtherReviewerCount ?? 0 })}
          {hasPermission('security.manage') && <Link className="ms-2 underline" href="/setup?tab=users">{t('Check reviewer setup')}</Link>}
        </p>}
        {loading && <p className="flex items-center gap-2 text-sm text-slate-500"><Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />{t('Loading…')}</p>}
        {error && <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-sm text-rose-800 dark:border-rose-400/30 dark:bg-rose-500/10 dark:text-rose-200">{t('Could not load the pay approvals.')}</p>}
        {data && !loading && data.items.length === 0 && <p data-testid="pay-approvals-empty" className="rounded-lg border border-slate-200 p-4 text-sm text-slate-600 dark:border-white/10 dark:text-slate-300">{t('No pay packages are waiting for you.')}</p>}
      </div>

      {data && data.items.length > 0 && (
        <section aria-labelledby="pay-approvals-heading">
          <h2 id="pay-approvals-heading" className="sr-only">{t('Pay packages awaiting approval')}</h2>
          <ul className="space-y-2" data-testid="pay-approvals-list">
            {data.items.map(item => (
              <li key={item.employeeId} className="rounded-xl border border-slate-200 bg-white p-4 dark:border-white/10 dark:bg-white/[0.04]">
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div className="min-w-0">
                    <p className="break-words text-base font-semibold text-slate-900 dark:text-white">{item.name}</p>
                    <p className="text-xs text-slate-500 dark:text-slate-400"><bdi>{item.employeeCode}</bdi>{item.designation ? ` · ${item.designation}` : ''}{item.companyName ? ` · ${item.companyName}` : ''}</p>
                  </div>
                  <span className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${item.status === 'rejected' ? 'bg-amber-100 text-amber-900 dark:bg-amber-500/20 dark:text-amber-200' : 'bg-sky-100 text-sky-900 dark:bg-sky-500/20 dark:text-sky-200'}`}>
                    {item.status === 'rejected' ? t('Sent back') : t('Awaiting approval')}
                  </span>
                </div>
                <dl className="mt-3 grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-3">
                  <div><dt className="text-xs text-slate-500 dark:text-slate-400">{t('Gross pay')}</dt><dd className="font-medium" dir="ltr">{fmt.plain.money(item.gross, item.currency)}</dd></div>
                  <div><dt className="text-xs text-slate-500 dark:text-slate-400">{t('Account')}</dt><dd className="font-medium" dir="ltr">{maskedAccount(item.ibanLast4) || '—'}</dd></div>
                  <div><dt className="text-xs text-slate-500 dark:text-slate-400">{t('Entered by')}</dt><dd className="font-medium">{item.enteredBy ? t('{name} on {when}', { name: isolateName(item.enteredBy.name), when: isolateLtr(when(item.enteredBy.atUtc)) }) : '—'}</dd></div>
                </dl>
                <div className="mt-3">
                  <Link className="btn-primary" href={`/pay-approvals/${item.employeeId}`} aria-label={t('Review pay package for {name}', { name: item.name })}>{t('Review')}</Link>
                </div>
              </li>
            ))}
          </ul>
          {data.hasMore && <p className="mt-2 text-xs text-slate-500">{t('Only the first 100 are shown. Approve some to see the rest.')}</p>}
        </section>
      )}
    </div>
  );
}
