'use client';

import { useLocale } from '../../contexts/LocaleContext';
import type { ApprovalDetailRendererProps } from '../approvals/approvalDetailRenderers';

/**
 * What an approver sees for a renewal offer (Release A slice R5 owns this file): before → after per component,
 * cost, reasons and evidence, and "changed since you approved". Registered in approvalDetailRenderers by R0 for
 * ContractRenewal and ContractRenewalBatch; the slice replaces this body.
 */
export function RenewalOfferApprovalCard({ request }: ApprovalDetailRendererProps) {
  const { t } = useLocale();
  return (
    <div data-approval-id={request.id} className="rounded-lg border border-dashed border-slate-300 p-4 text-sm text-slate-500 dark:border-white/15 dark:text-slate-400">
      {t('Renewal offer details appear here.')}
    </div>
  );
}
