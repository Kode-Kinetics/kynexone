'use client';

import Link from 'next/link';
import { useEffect, useState } from 'react';
import { useLocale } from '@/src/contexts/LocaleContext';
import { employeeHireApi, type HireReview } from '@/src/api/employeeHire';
import { useAuth } from '@/src/contexts/AuthContext';
import { reviewItemLabel } from '@/src/lib/hire/reviewLabels';
import { PAY_APPROVE_PERMISSION, PAY_GAP_KEY } from '@/src/lib/payApprovals';

export function SuccessStep({ id, name, rehired = false }: { id: number; name: string; rehired?: boolean }) {
  const { t } = useLocale();
  const { hasPermission } = useAuth();
  const [review, setReview] = useState<HireReview | null | 'failed'>(null);
  useEffect(() => { employeeHireApi.review(id).then(setReview).catch(() => setReview('failed')); }, [id]);
  const blockers = review && review !== 'failed' ? review.blockers : [];
  const warnings = review && review !== 'failed' ? review.warnings : [];
  return (
    <section aria-labelledby="hire-success-title" className="rounded-2xl border border-emerald-200 bg-emerald-50/60 p-5 dark:border-emerald-400/30 dark:bg-emerald-500/10">
      <h2 id="hire-success-title" tabIndex={-1} ref={el => el?.focus()} className="text-lg font-semibold text-slate-900 outline-none dark:text-white">{rehired ? t('{name} has been rehired.', { name }) : t('{name} has been added as a draft.', { name })}</h2>
      <div data-testid="readiness" className="mt-3 text-sm">
        <p className="font-medium">{t('Readiness')}</p>
        {review === null && <p className="text-slate-500">{t('Checking what is left…')}</p>}
        {review === 'failed' && <p className="text-slate-500">{t('Readiness could not be loaded. Open the record to see it.')}</p>}
        {review && review !== 'failed' && (blockers.length
          ? <ul className="mt-1 list-disc ps-5">{blockers.map(b => (
            <li key={b.key}>
              {reviewItemLabel(b, t)}
              {/* OD4: a Finance approver decides the pay package from its own scoped view, without employees.sensitive. */}
              {b.key === PAY_GAP_KEY && hasPermission(PAY_APPROVE_PERMISSION) && (
                <> · <Link className="font-semibold text-sapphire underline dark:text-blue-300" href={`/pay-approvals/${id}`}>{t('Review the pay package')}</Link></>
              )}
            </li>
          ))}</ul>
          : <p>{t('Nothing is blocking activation.')}</p>)}
        {warnings.length > 0 && (
          <>
            <p className="mt-3 font-medium">{t('To confirm')}</p>
            <ul className="mt-1 list-disc ps-5">{warnings.map(w => <li key={w.key}>{reviewItemLabel(w, t)}</li>)}</ul>
          </>
        )}
      </div>
      <p className="mt-3 text-sm text-slate-600 dark:text-slate-300">{rehired ? t('Next: review the record, update anything that changed, then request activation.') : t('Next: complete the details, then request activation. The employee signs in only after activation.')}</p>
      <div className="mt-4 flex flex-wrap gap-2">
        <Link className="btn-primary" href={`/people/${id}`}>{t('Open the record')}</Link>
        <a className="btn-secondary" href="/people/new">{t('Add another')}</a>
        <Link className="btn-secondary" href="/people">{t('Back to people')}</Link>
      </div>
    </section>
  );
}
