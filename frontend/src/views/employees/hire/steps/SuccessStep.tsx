'use client';

import Link from 'next/link';
import { useEffect, useState } from 'react';
import { useLocale } from '@/src/contexts/LocaleContext';
import { employeeHireApi, type HireReview } from '@/src/api/employeeHire';
import { reviewItemLabel } from '@/src/lib/hire/reviewLabels';

export function SuccessStep({ id, name }: { id: number; name: string }) {
  const { t } = useLocale();
  const [review, setReview] = useState<HireReview | null | 'failed'>(null);
  useEffect(() => { employeeHireApi.review(id).then(setReview).catch(() => setReview('failed')); }, [id]);
  const blockers = review && review !== 'failed' ? review.blockers : [];
  return (
    <section aria-labelledby="hire-success-title" className="rounded-2xl border border-emerald-200 bg-emerald-50/60 p-5 dark:border-emerald-400/30 dark:bg-emerald-500/10">
      <h2 id="hire-success-title" tabIndex={-1} ref={el => el?.focus()} className="text-lg font-semibold text-slate-900 outline-none dark:text-white">{t('{name} has been added as a draft.', { name })}</h2>
      <div data-testid="readiness" className="mt-3 text-sm">
        <p className="font-medium">{t('Readiness')}</p>
        {review === null && <p className="text-slate-500">{t('Checking what is left…')}</p>}
        {review === 'failed' && <p className="text-slate-500">{t('Readiness could not be loaded. Open the record to see it.')}</p>}
        {review && review !== 'failed' && (blockers.length
          ? <ul className="mt-1 list-disc ps-5">{blockers.map(b => <li key={b.key}>{reviewItemLabel(b, t)}</li>)}</ul>
          : <p>{t('Nothing is blocking activation.')}</p>)}
      </div>
      <p className="mt-3 text-sm text-slate-600 dark:text-slate-300">{t('Next: complete the details, then request activation. The employee signs in only after activation.')}</p>
      <div className="mt-4 flex flex-wrap gap-2">
        <Link className="btn-primary" href={`/people/${id}`}>{t('Open the record')}</Link>
        <a className="btn-secondary" href="/people/new">{t('Add another')}</a>
        <Link className="btn-secondary" href="/people">{t('Back to people')}</Link>
      </div>
    </section>
  );
}
