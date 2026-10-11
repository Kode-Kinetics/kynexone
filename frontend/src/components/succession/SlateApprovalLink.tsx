'use client';

import Link from 'next/link';
import type { ApprovalRequest } from '../../api/approvals';
import { useLocale } from '../../contexts/LocaleContext';

/**
 * A succession slate's approval carries ids only (no names, by design): the decision is taken on the slate review
 * page, which shows the frozen snapshot. Renders nothing for any other kind of approval.
 */
export function SlateApprovalLink({ request }: { request: Pick<ApprovalRequest, 'entityName' | 'entityId'> }) {
  const { t } = useLocale();
  if (request.entityName !== 'SuccessionSlate') return null;
  return (
    <Link href={`/performance/succession/slates/${request.entityId}`} className="btn-secondary h-8 px-3 text-xs" data-testid="suc-approval-review-slate">
      {t('suc.approvals.reviewSlate')}
    </Link>
  );
}

/** The Approval Center's detail card for a slate: where to read it before deciding. */
export function SlateApprovalCard({ request }: { request: ApprovalRequest }) {
  const { t } = useLocale();
  return (
    <div className="space-y-2 rounded-lg border border-slate-200 p-3 text-xs dark:border-white/10">
      <p className="text-slate-600 dark:text-slate-300">{t('suc.approvals.cardIntro')}</p>
      <SlateApprovalLink request={request} />
    </div>
  );
}
