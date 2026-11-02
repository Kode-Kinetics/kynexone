'use client';

import { useCallback, useEffect, useState } from 'react';
import { payApprovalsApi, type PayApprovalSetup } from '../../api/payApprovals';
import { useLocale } from '../../contexts/LocaleContext';

export function PayApprovalSetupCard({ companyId, revision, onCreate }: {
  companyId: string | null; revision: unknown; onCreate: () => void;
}) {
  const { t } = useLocale();
  const [data, setData] = useState<PayApprovalSetup | null>(null);
  const [error, setError] = useState(false);
  const [loading, setLoading] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const reload = useCallback(() => setRefresh(n => n + 1), []);
  useEffect(() => {
    let current = true;
    setData(null); setError(false);
    if (!companyId) return;
    setLoading(true);
    payApprovalsApi.setup(companyId).then(value => { if (current) setData(value); })
      .catch(() => { if (current) setError(true); })
      .finally(() => { if (current) setLoading(false); });
    return () => { current = false; };
  }, [companyId, revision, refresh]);
  useEffect(() => {
    window.addEventListener('focus', reload);
    return () => window.removeEventListener('focus', reload);
  }, [reload]);
  if (!companyId) return <p className="text-sm text-slate-600 dark:text-slate-300">{t('Select a company to check independent pay reviewers.')}</p>;
  return (
    <section className="rounded-xl border border-slate-200 bg-white p-4 dark:border-white/10 dark:bg-slate-900" aria-labelledby="pay-reviewer-setup">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id="pay-reviewer-setup" className="font-semibold">{t('Independent pay approval')}</h2>
        <button type="button" className="btn-secondary" disabled={loading} onClick={reload}>{t('Reload')}</button>
      </div>
      <p className="mt-2 text-sm">{t('Create a separate user with Finance Approver access to this company. They must finish password setup and required two-step sign-in. Their login does not need an active employee record.')}</p>
      <p className="mt-2 text-sm">{t('Each pay package still needs a reviewer who did not enter or change it and is not its employee. Password resets do not approve pay or activate employment.')}</p>
      <div aria-live="polite" className="mt-3 text-sm">
        {loading && <p>{t('Loading…')}</p>}
        {error && <p role="alert">{t('Could not check independent reviewers. Reload to try again.')}</p>}
        {data && <>
          <p className={data.readyForNewHires ? 'text-emerald-700 dark:text-emerald-300' : 'text-amber-800 dark:text-amber-300'}>
            {data.readyForNewHires ? t('Separate pay entry and approval users are ready.') : t('Set up separate pay entry and approval users before onboarding employees.')}
          </p>
          <p>{t('Pending pay packages: {count}', { count: data.pendingCount })}</p>
          {data.strandedCount > 0 && <p className="font-semibold text-amber-800 dark:text-amber-300">{t('No eligible reviewer for {count} checked packages.', { count: data.strandedCount })}</p>}
          {data.items.length > 0 && <details className="mt-2">
            <summary className="cursor-pointer">{t('See who can review each pending package')}</summary>
            <ul className="mt-2 space-y-1">
              {data.items.map(item => <li key={item.employeeId}><bdi>{item.name}</bdi>{': '}
                {item.eligibleReviewers.length ? item.eligibleReviewers.map(r => r.name).join(', ') : t('Another independent reviewer is needed')}
              </li>)}
            </ul>
          </details>}
          {data.hasMore && <p>{t('Reviewer checks show the first 100 pending packages. Review these to see the rest.')}</p>}
        </>}
      </div>
      <button type="button" className="btn-secondary mt-3" onClick={onCreate}>{t('Create User')}</button>
    </section>
  );
}
