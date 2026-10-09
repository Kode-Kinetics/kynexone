'use client';

import { useEffect, useState } from 'react';
import Link from 'next/link';
import { benefitsApi, benefitsErrorMessage, type AdditionalBenefitRequest } from '@/src/api/benefits';
import { useLocale } from '@/src/contexts/LocaleContext';
import { useFormat } from '@/src/hooks/useFormat';
import type { ApprovalDetailRendererProps } from '@/src/components/approvals/approvalDetailRenderers';
import { AdditionalBenefitSummary } from './AdditionalBenefitSummary';
import { FormError, Modal, SECONDARY, StatusPill } from './benefitUi';

export function AdditionalBenefitRequestContent({ requestId, showApprovalLink = true }: { requestId: string; showApprovalLink?: boolean }) {
  const { t } = useLocale();
  const format = useFormat();
  const [request, setRequest] = useState<AdditionalBenefitRequest | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    let active = true;
    setRequest(null); setError(null);
    benefitsApi.additionalRequest(requestId).then(result => { if (active) setRequest(result); })
      .catch(err => { if (active) setError(benefitsErrorMessage(err, t('Could not load the benefit request.'))); });
    return () => { active = false; };
  }, [requestId, retry, t]);
  if (error) return <div className="space-y-2"><FormError message={error} /><button type="button" className={SECONDARY} onClick={() => setRetry(value => value + 1)}>{t('Retry')}</button></div>;
  if (!request) return <div aria-busy="true" aria-label={t('Loading benefit request')} className="h-36 animate-pulse rounded-xl bg-slate-100 dark:bg-white/5" />;
  return <div className="space-y-4" data-testid="additional-benefit-request-detail">
    <div className="flex flex-wrap items-center justify-between gap-2"><StatusPill active={request.status === 'Approved'} label={request.status === 'Pending' ? 'Pending approval' : request.status} /><p className="text-xs text-slate-500 dark:text-slate-400">{format.dateTime(request.createdAtUtc)}{request.requestedByName ? ` · ${request.requestedByName}` : ''}</p></div>
    <AdditionalBenefitSummary terms={request.terms} currency={request.currency ?? ''} baseline={request.baseline} employeeName={request.employeeName} planName={request.planName} />
    {request.approval && <div className="rounded-lg border border-slate-200 p-3 text-xs dark:border-white/10">
      {request.status === 'Pending' && <p className="text-slate-600 dark:text-slate-300">{t('Awaiting {approver}', { approver: request.approval.currentApproverName || request.approval.currentApproverRole || t('Assigned approver') })}</p>}
      {request.approval.decisionBlockedReason && <p className="mt-1 text-slate-500 dark:text-slate-400">{request.approval.decisionBlockedReason}</p>}
      {request.approval.decisions.length > 0 && <ol className="mt-2 space-y-1">{request.approval.decisions.map(decision => <li key={decision.id} className="text-slate-600 dark:text-slate-300">{t(decision.decision)} · {format.dateTime(decision.decidedAtUtc)}{decision.comments ? ` · ${decision.comments}` : ''}</li>)}</ol>}
    </div>}
    {showApprovalLink && request.approval && <Link href="/approvals" className={`${SECONDARY} w-fit`}>{t('Open Approvals')}</Link>}
  </div>;
}

export function AdditionalBenefitRequestDetail({ requestId, onClose }: { requestId: string; onClose: () => void }) {
  const { t } = useLocale();
  return <Modal title={t('Additional benefit request')} onClose={onClose} wide><AdditionalBenefitRequestContent requestId={requestId} /></Modal>;
}

export function AdditionalBenefitApprovalCard({ request }: ApprovalDetailRendererProps) {
  return <AdditionalBenefitRequestContent requestId={request.entityId} showApprovalLink={false} />;
}
