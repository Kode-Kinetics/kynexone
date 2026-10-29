'use client';

import { useEffect, useState } from 'react';
import { approvalsApi, type EmployeeChangeDetail } from '../../api/approvals';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import type { ApprovalDetailRendererProps } from './approvalDetailRenderers';

/**
 * What an employee change will do, field by field: the value on file now and the value proposed. Before this the
 * approver saw only the field names ("IBAN, date of birth") and approved a value they could not see.
 *
 * The values come from GET /approval-requests/{id}/change-detail, which answers only someone who may decide the
 * request now (the same rule the decision runs), so the card asks only when the caller can decide. Identity and
 * bank numbers arrive masked to their last 4 characters; pay arrives masked unless the viewer has pay access.
 */
export function EmployeeChangeApprovalCard({ request }: ApprovalDetailRendererProps) {
  const { t } = useLocale();
  const format = useFormat();
  const [detail, setDetail] = useState<EmployeeChangeDetail | null>(null);
  const [failed, setFailed] = useState(false);
  const canSee = request.status === 'Pending' && request.canDecide;

  useEffect(() => {
    setDetail(null);
    setFailed(false);
    if (!canSee) return;
    let live = true;
    approvalsApi.changeDetail(request.id)
      .then(result => { if (live) setDetail(result); })
      .catch(() => { if (live) setFailed(true); });
    return () => { live = false; };
  }, [request.id, canSee]);

  if (!canSee) {
    return request.status === 'Pending'
      ? <p className="text-xs text-slate-500 dark:text-slate-400">{t('The proposed values are shown to the person who can decide this step.')}</p>
      : null;
  }
  if (failed) {
    return <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300">
      {t('Could not load the proposed values. Close and open the request again.')}
    </p>;
  }
  if (!detail) {
    return <div aria-busy="true" aria-label={t('Loading the proposed values…')} className="h-24 animate-pulse rounded-lg bg-slate-100 dark:bg-white/5" />;
  }

  const anyMasked = detail.fields.some(f => f.masked);
  return (
    <section data-testid="employee-change-detail" className="rounded-lg border border-slate-200 p-4 dark:border-white/10">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{t('What this change does')}</h3>
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('Effective {date}', { date: format.date(detail.effectiveDate) })}</p>
      </div>
      <table className="mt-3 w-full text-sm">
        <thead>
          <tr className="text-start text-xs font-semibold uppercase tracking-wide text-slate-400">
            <th scope="col" className="py-1 pe-3 text-start">{t('Field')}</th>
            <th scope="col" className="py-1 pe-3 text-start">{t('On file now')}</th>
            <th scope="col" className="py-1 text-start">{t('Proposed')}</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">
          {detail.fields.map(field => (
            <tr key={field.key} data-field={field.key}>
              <th scope="row" className="py-2 pe-3 text-start font-medium text-slate-700 dark:text-slate-200">{t(field.label)}</th>
              <td className="py-2 pe-3 text-slate-500 dark:text-slate-400" data-role="current">{field.currentValue ?? t('Not set')}</td>
              <td className="py-2 font-semibold text-slate-900 dark:text-white" data-role="proposed">{field.proposedValue ?? t('Not set')}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {anyMasked && (
        <p className="mt-3 text-xs text-slate-500 dark:text-slate-400">
          {t('Identity and bank numbers show their last 4 characters only. Pay is shown only to people with pay access.')}
        </p>
      )}
    </section>
  );
}
