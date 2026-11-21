'use client';

import { useId, useState } from 'react';
import { ChevronDown, RefreshCw, ShieldAlert, ShieldCheck } from 'lucide-react';
import { usePayApprovalSetup } from '../../hooks/usePayApprovalSetup';
import { useLocale } from '../../contexts/LocaleContext';

/**
 * One slim status line for independent pay approval (is a separate reviewer in place, how many packages are pending),
 * with the explanation and the "who can review each package" list behind "Details", closed until asked for. The toolbar
 * below owns Create User, so this does not repeat it. `onCreate` is kept for callers; it is no longer drawn here.
 */
export function PayApprovalSetupCard({ companyId, revision }: {
  companyId: string | null; revision: unknown; onCreate?: () => void;
}) {
  const { t } = useLocale();
  const { data, error, loading, reload } = usePayApprovalSetup(companyId, revision);
  const [open, setOpen] = useState(false);
  const detailsId = useId();
  if (!companyId) return <p className="text-sm text-slate-600 dark:text-slate-300">{t('Select a company to check independent pay reviewers.')}</p>;
  const ready = !!data?.readyForNewHires;
  const Icon = data && !ready ? ShieldAlert : ShieldCheck;
  return (
    <section className="rounded-lg border border-slate-200 bg-white px-3 py-2 dark:border-white/10 dark:bg-slate-900" aria-labelledby="pay-reviewer-setup">
      <div className="flex items-center gap-x-3 text-sm">
        <Icon className={`h-4 w-4 shrink-0 ${data && !ready ? 'text-amber-600 dark:text-amber-300' : 'text-emerald-600 dark:text-emerald-300'}`} aria-hidden="true" />
        <h2 id="pay-reviewer-setup" className="shrink-0 font-semibold">{t('Independent pay approval')}</h2>
        <div aria-live="polite" className="flex min-w-0 flex-1 flex-wrap items-center gap-x-3 text-xs">
          {loading && <span>{t('Loading…')}</span>}
          {error && <span role="alert">{t('Could not check independent reviewers. Reload to try again.')}</span>}
          {data && <>
            <span className={ready ? 'text-emerald-700 dark:text-emerald-300' : 'text-amber-800 dark:text-amber-300'}>
              {ready ? t('Separate pay entry and approval users are ready.') : t('Set up separate pay entry and approval users before onboarding employees.')}
            </span>
            <span>{t('Pending pay packages: {count}', { count: data.pendingCount })}</span>
            {data.strandedCount > 0 && <span className="font-semibold text-amber-800 dark:text-amber-300">{t('No eligible reviewer for {count} checked packages.', { count: data.strandedCount })}</span>}
          </>}
        </div>
        <div className="flex shrink-0 items-center gap-1">
          <button type="button" className="inline-flex min-h-8 items-center gap-1 rounded-lg px-2 text-xs font-medium text-slate-600 hover:bg-slate-100 disabled:opacity-50 dark:text-slate-300 dark:hover:bg-white/5"
            disabled={loading} onClick={reload}>
            <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" /><span className="sr-only">{t('Reload')}</span>
          </button>
          <button type="button" aria-expanded={open} aria-controls={detailsId} onClick={() => setOpen(value => !value)}
            className="inline-flex min-h-8 items-center gap-1 rounded-lg px-2 text-xs font-semibold text-violet-700 hover:bg-violet-50 dark:text-violet-300 dark:hover:bg-violet-900/30">
            {t('Details')} <ChevronDown className={`h-3.5 w-3.5 transition-transform motion-reduce:transition-none ${open ? 'rotate-180' : ''}`} aria-hidden="true" />
          </button>
        </div>
      </div>
      <div id={detailsId} hidden={!open} className="mt-2 border-t border-slate-100 pt-2 text-sm dark:border-white/10">
        <p>{t('Create a separate user with Finance Approver access to this company. They must finish password setup and required two-step sign-in. Their login does not need an active employee record.')}</p>
        <p className="mt-2">{t('Each pay package still needs a reviewer who did not enter or change it and is not its employee. Password resets do not approve pay or activate employment.')}</p>
        {data && <>
          {data.items.length > 0 && <details className="mt-2">
            <summary className="cursor-pointer">{t('See who can review each pending package')}</summary>
            <ul className="mt-2 space-y-1">
              {data.items.map(item => <li key={item.employeeId}><bdi>{item.name}</bdi>{': '}
                {item.eligibleReviewers.length ? item.eligibleReviewers.map(r => r.name).join(', ') : t('Another independent reviewer is needed')}
              </li>)}
            </ul>
          </details>}
          {data.hasMore && <p className="mt-2">{t('Reviewer checks show the first 100 pending packages. Review these to see the rest.')}</p>}
        </>}
      </div>
    </section>
  );
}
