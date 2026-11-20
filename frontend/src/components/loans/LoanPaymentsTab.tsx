'use client';

import { useCallback, useEffect, useState } from 'react';
import { loanPaymentBatchesApi, loansApi, type EmployeeLoan, type LoanPaymentBatch } from '../../api/loans';
import { useAuth } from '../../contexts/AuthContext';
import { loanErrorMessage, localDateToday, repaymentMethodLabels } from '../../lib/loanWorkflow';
import { Modal } from '../Modal';
import { useT } from '../../hooks/useT';
import { useLocale } from '../../contexts/LocaleContext';
import { enumLabel } from '../../i18n/enumLabel';
import { useFormat } from '../../hooks/useFormat';

function batchAmounts(batch: LoanPaymentBatch) {
  const sum = (...statuses: string[]) => batch.lines.reduce((total, line) => {
    const status = line.status ?? (batch.status === 'Paid' ? 'Paid' : batch.status === 'Cancelled' ? 'Cancelled' : 'Pending');
    return total + (statuses.includes(status) ? line.amount : 0);
  }, 0);
  return {
    paid: batch.paidAmount ?? sum('Paid'), remaining: batch.remainingAmount ?? sum('Pending', 'Failed'),
    cancelled: batch.cancelledAmount ?? sum('Cancelled'), reversed: batch.reversedAmount ?? sum('Reversed'),
  };
}

export function LoanPaymentsTab({ onChanged }: { onChanged: () => void }) {
  const { user } = useAuth();
  const t = useT();
  const { locale } = useLocale();
  const canOperate = user?.roles.some(role => ['Admin', 'Finance'].includes(role)) ?? false;
  const [loans, setLoans] = useState<EmployeeLoan[]>([]);
  const [batches, setBatches] = useState<LoanPaymentBatch[]>([]);
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [detail, setDetail] = useState<LoanPaymentBatch | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [loadFailed, setLoadFailed] = useState(false);
  const [notice, setNotice] = useState('');
  const [action, setAction] = useState<'approve' | 'cancel' | 'pay' | 'line' | null>(null);
  const [lineId, setLineId] = useState('');
  const [lineOutcome, setLineOutcome] = useState<'Paid' | 'Failed' | 'Cancelled'>('Paid');
  const [lineReason, setLineReason] = useState('');
  const [selfReason, setSelfReason] = useState('');
  const [payment, setPayment] = useState({ paidDate: localDateToday(), reference: '', repaymentStartDate: '' });
  // A null currency (a loan carried in before it was stamped) shows a bare amount: the tenant-wide currency could
  // be another company's, or a placeholder before settings load. The shared formatter never throws, where
  // toLocaleString took the whole tab down.
  const fx = useFormat();
  const fmt = (value: number, currency?: string | null) => fx.plain.money(value, currency || null);

  const load = useCallback(async () => {
    setLoading(true); setLoadFailed(false); setError('');
    try {
      const [batchList, first] = await Promise.all([loanPaymentBatchesApi.list(), loansApi.list({ status: 'Approved', page: 1, pageSize: 100 })]);
      const approvedLoans = [...first.items];
      for (let page = 2; approvedLoans.length < first.total; page++) {
        const next = await loansApi.list({ status: 'Approved', page, pageSize: 100 });
        if (next.items.length === 0) break;
        approvedLoans.push(...next.items);
      }
      const reservedIds = new Set(batchList.filter(batch => batch.status !== 'Cancelled').flatMap(batch => batch.lines.filter(line => !['Cancelled', 'Reversed'].includes(line.status ?? '')).map(line => line.loanId)));
      setBatches(batchList);
      setLoans(approvedLoans.filter(loan => !reservedIds.has(loan.id)));
      setSelectedIds([]);
    } catch (e) { setLoadFailed(true); setError(loanErrorMessage(e, t('Unable to load loan payments.'), locale)); }
    finally { setLoading(false); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  const run = async (task: () => Promise<void>) => {
    setSaving(true); setError(''); setNotice('');
    try { await task(); onChanged(); await load(); }
    catch (e) { setError(loanErrorMessage(e, t('Unable to process this loan payment.'), locale)); }
    finally { setSaving(false); }
  };

  const create = () => run(async () => {
    const batch = await loanPaymentBatchesApi.create(selectedIds);
    setDetail(batch); setNotice(t('Draft batch created. A different Finance user must approve it before payment.'));
  });

  const submitAction = () => {
    if (!detail || !action) return;
    const selfApproving = action === 'approve' && detail.createdBy === user?.id;
    if (selfApproving && selfReason.trim().length < 10) { setError(t('Give the reason you are approving this batch yourself (at least 10 characters). It is kept in the audit trail.')); return; }
    if ((action === 'pay' || (action === 'line' && lineOutcome === 'Paid')) && (!payment.paidDate || !payment.reference.trim())) { setError(t('Enter the completed payment date and bank reference.')); return; }
    if (action === 'line' && lineOutcome !== 'Paid' && !lineReason.trim()) { setError(t('Give a reason for the failed or cancelled payment.')); return; }
    void run(async () => {
      if (action === 'approve') await loanPaymentBatchesApi.approve(detail.id, selfApproving ? selfReason.trim() : undefined);
      else if (action === 'cancel') await loanPaymentBatchesApi.cancel(detail.id);
      else if (action === 'line') await loanPaymentBatchesApi.lineOutcome(detail.id, lineId, { outcome: lineOutcome, reason: lineReason || undefined, ...(lineOutcome === 'Paid' ? { ...payment, repaymentStartDate: payment.repaymentStartDate || undefined } : {}) });
      else await loanPaymentBatchesApi.confirmPaid(detail.id, { ...payment, repaymentStartDate: payment.repaymentStartDate || undefined });
      const refreshed = await loanPaymentBatchesApi.get(detail.id);
      setDetail(refreshed); setAction(null);
      setNotice(action === 'line' ? t(lineOutcome === 'Paid' ? 'Loan payment marked paid. The other loans retain their current status.' : lineOutcome === 'Failed' ? 'Loan payment marked failed. The other loans retain their current status.' : 'Loan payment marked cancelled. The other loans retain their current status.') : action === 'pay' ? t('Payment recorded. These loans are now active and ready for repayment.') : action === 'cancel' ? t('Batch cancelled. The approved loans are available for a new payment batch.') : t('Batch approved. Download instructions and complete the transfer in your bank before recording payment.'));
    });
  };

  const download = async (batch: LoanPaymentBatch) => {
    setSaving(true); setError('');
    try {
      const blob = await loanPaymentBatchesApi.export(batch.id);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a'); link.href = url; link.download = `${batch.batchNumber}-payment-instructions.csv`;
      document.body.appendChild(link); link.click(); link.remove();
      window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (e) { setError(loanErrorMessage(e, t('Unable to download payment instructions.'), locale)); }
    finally { setSaving(false); }
  };

  return <div className="space-y-5">
    <div className="rounded-lg border border-blue-200 bg-blue-50 p-4 text-sm text-blue-900 dark:border-blue-900 dark:bg-blue-900/20 dark:text-blue-200">
      <p className="font-semibold">{t('Separate loan disbursement')}</p>
      <p className="mt-1">{t('Select approved loans → Create batch → Approve the batch (by a different Finance user, unless the company loan policy allows self-approval) → Pay through your bank → Record completed payment.')}</p>
      <p className="mt-1">{t('This screen records payments; it does not send transfers. Loans become active only after payment confirmation.')}</p>
    </div>
    {error && <div role="alert" className="flex flex-wrap items-center justify-between gap-3 rounded-lg bg-red-50 p-3 text-sm text-red-700 dark:bg-red-900/20 dark:text-red-300"><p>{error}</p>{loadFailed && <button type="button" className="btn-secondary" disabled={loading} onClick={() => void load()}>{t(loading ? 'Retrying…' : 'Retry')}</button>}</div>}
    {notice && <p role="status" className="rounded-lg bg-emerald-50 p-3 text-sm text-emerald-800 dark:bg-emerald-900/20 dark:text-emerald-300">{notice}</p>}
    <section className="space-y-3" aria-labelledby="loan-payment-queue">
      <div className="flex flex-wrap items-center justify-between gap-3"><div><h2 id="loan-payment-queue" className="font-semibold">{t('Approved loans awaiting payment')}</h2><p className="text-xs text-slate-500">{t('Loans already in a payment batch appear below.')}</p></div>
        {canOperate && <button type="button" className="btn-primary disabled:opacity-50" disabled={saving || loading || selectedIds.length === 0} onClick={create}>{t('Create Payment Batch ({count})', { count: selectedIds.length })}</button>}</div>
      <div className="surface overflow-x-auto"><table className="w-full text-sm"><thead><tr>{[t('Select'), t('Loan'), t('Employee'), t('Repayment method'), t('Approved amount')].map(label => <th key={label} className="px-4 py-3 text-start text-xs text-slate-500">{label}</th>)}</tr></thead>
        <tbody className="divide-y divide-slate-100 dark:divide-white/10">{loading ? <tr><td colSpan={5} className="p-6 text-center">{t('Loading payments…')}</td></tr> : loans.length === 0 ? <tr><td colSpan={5} className="p-6 text-center text-slate-500">{t('No approved loans waiting for a batch.')}</td></tr> : loans.map(loan => <tr key={loan.id}>
          <td className="px-4 py-3"><input type="checkbox" aria-label={t('Select {loan}', { loan: loan.loanNumber })} checked={selectedIds.includes(loan.id)} disabled={saving || !canOperate || loan.reviewRequired || loan.collectionStatus === 'OnHold'} onChange={e => setSelectedIds(ids => e.target.checked ? [...ids, loan.id] : ids.filter(id => id !== loan.id))} /></td>
          <td className="px-4 py-3 font-mono text-xs">{loan.loanNumber}</td><td className="px-4 py-3">{loan.employeeName}{(loan.reviewRequired || loan.collectionStatus === 'OnHold') && <p className="text-xs text-amber-700">{t('HR review required before payment')}</p>}</td><td className="px-4 py-3">{repaymentMethodLabels[loan.repaymentMethod]}</td><td className="px-4 py-3 font-semibold">{fmt(loan.approvedAmount, loan.currency)}</td>
        </tr>)}</tbody></table></div>
    </section>
    <section className="space-y-3" aria-labelledby="loan-batches"><h2 id="loan-batches" className="font-semibold">{t('Loan payment batches')}</h2>
      <div className="surface overflow-x-auto"><table className="w-full text-sm"><thead><tr>{[t('Batch'), t('Loans'), t('Instruction total'), t('Paid'), t('Remaining'), t('Status'), t('Payment reference'), ''].map((label, index) => <th key={index} className="px-4 py-3 text-start text-xs text-slate-500">{label}</th>)}</tr></thead>
        <tbody className="divide-y divide-slate-100 dark:divide-white/10">{!loading && batches.length === 0 ? <tr><td colSpan={8} className="p-6 text-center text-slate-500">{t('No loan payment batches yet.')}</td></tr> : batches.map(batch => <tr key={batch.id}><td className="px-4 py-3 font-mono text-xs">{batch.batchNumber}</td><td className="px-4 py-3">{batch.lines.length}</td><td className="px-4 py-3">{fmt(batch.totalAmount, batch.currency)}</td><td className="px-4 py-3">{fmt(batchAmounts(batch).paid, batch.currency)}</td><td className="px-4 py-3">{fmt(batchAmounts(batch).remaining, batch.currency)}</td><td className="px-4 py-3">{enumLabel(t, 'LoanStatus', batch.status)}</td><td className="px-4 py-3">{batch.paymentReference || '—'}</td><td className="px-4 py-3"><button type="button" className="text-sapphire" disabled={saving} aria-label={t('View batch {batch}', { batch: batch.batchNumber })} onClick={() => { setDetail(batch); setAction(null); setError(''); }}>{t('View →')}</button></td></tr>)}</tbody></table></div>
    </section>
    <Modal isOpen={!!detail} title={t('Loan Payment — {batch}', { batch: detail?.batchNumber ?? '' })} onClose={() => { if (!saving) { setDetail(null); setAction(null); } }} size="lg" footer={<button className="btn-secondary" disabled={saving} onClick={() => { setDetail(null); setAction(null); }}>{t('Close')}</button>}>
      {detail && <div className="space-y-4">
        {error && <p role="alert" className="text-sm text-red-600">{error}</p>}
        <div className="flex flex-wrap justify-between gap-2"><p>{t('Status')}: <strong>{enumLabel(t, 'LoanStatus', detail.status)}</strong></p><p>{t('Instruction total')}: <strong>{fmt(detail.totalAmount, detail.currency)}</strong></p></div>
        <dl aria-label={t('Batch payment totals')} className="grid grid-cols-2 gap-3 rounded-lg bg-slate-50 p-3 dark:bg-white/5">{([[t('Paid amount'), batchAmounts(detail).paid], [t('Remaining to pay'), batchAmounts(detail).remaining], [t('Cancelled amount'), batchAmounts(detail).cancelled], [t('Reversed amount'), batchAmounts(detail).reversed]] as const).map(([label, amount]) => <div key={label}><dt className="text-xs text-slate-500">{label}</dt><dd className="font-semibold">{fmt(amount, detail.currency)}</dd></div>)}</dl>
        <div className="divide-y divide-slate-100 dark:divide-white/10">{detail.lines.map(line => <div key={line.id} className="flex flex-wrap justify-between gap-2 py-2 text-sm"><div><p>{line.loanNumber} · {line.employeeName} {line.employeeCode && `(${line.employeeCode})`}</p><p className="mt-1 text-xs text-slate-500">{line.bankName || t('Bank not provided')}</p><p className="break-all font-mono text-xs text-slate-500">{line.iban || t('Bank account not provided')}</p><p className="mt-1 text-xs text-slate-500">{t('First repayment')}: {line.repaymentStartDate || t('One repayment period after payment')} · {line.repaymentFrequency || t('Scheduled')}</p><p className="mt-1 text-xs">{t('Payment')}: {enumLabel(t, 'LoanStatus', line.status || (detail.status === 'Paid' ? 'Paid' : 'Pending'))}{line.paymentReference ? ` · ${line.paymentReference}` : ''}{line.failureReason ? ` · ${line.failureReason}` : ''}</p></div><div className="space-y-2 text-end"><p>{fmt(line.amount, detail.currency)}</p>{canOperate && ['Approved', 'PartiallyPaid'].includes(detail.status) && !['Paid', 'Cancelled', 'Reversed'].includes(line.status ?? '') && !action && <button className="text-xs text-sapphire" disabled={saving} onClick={() => { setLineId(line.id); setLineOutcome('Paid'); setLineReason(''); setPayment({ paidDate: localDateToday(), reference: '', repaymentStartDate: '' }); setAction('line'); }}>{t('Record Outcome — {loan}', { loan: line.loanNumber })}</button>}</div></div>)}</div>
        {detail.status === 'Draft' && <p className="text-sm text-slate-500">{detail.createdBy === user?.id ? t(detail.selfApprovalAllowed ? "You created this batch. Your company's loan policy lets you approve it yourself; you will be asked for a reason that is kept in the audit trail." : 'You created this batch. Another Finance user must approve it.') : t('Review every loan and amount before approving this batch.')}</p>}
        {['Approved', 'PartiallyPaid'].includes(detail.status) && <p className="text-sm text-slate-500">{t('Record each loan payment separately when bank results differ. Failed payments remain available for retry; cancelled unpaid lines return to the payment queue. Confirm the full batch only when every loan has been paid.')}</p>}
        {detail.status === 'Paid' && <p className="text-sm text-emerald-700">{t('Paid on {date} · Reference: {reference}', { date: detail.paidDate ?? '', reference: detail.paymentReference ?? '' })}</p>}
        {!action && <div className="flex flex-wrap gap-2">
          {detail.status === 'Draft' && (detail.createdBy !== user?.id || detail.selfApprovalAllowed) && <button type="button" disabled={saving} onClick={() => { setSelfReason(''); setAction('approve'); }} className="btn-primary">{t('Approve Batch')}</button>}
          {['Approved', 'Paid', 'PartiallyPaid', 'Completed'].includes(detail.status) && <button type="button" disabled={saving} onClick={() => download(detail)} className="btn-secondary">{t('Download Payment Instructions')}</button>}
          {canOperate && detail.status === 'Approved' && <button type="button" disabled={saving} onClick={() => { setPayment({ paidDate: localDateToday(), reference: '', repaymentStartDate: '' }); setAction('pay'); }} className="btn-primary">{t('Record Completed Payment')}</button>}
          {canOperate && ['Draft', 'Approved'].includes(detail.status) && <button type="button" disabled={saving} onClick={() => setAction('cancel')} className="btn-secondary">{t('Cancel Batch')}</button>}
        </div>}
        {action && <div className="space-y-3 rounded-lg border border-slate-200 p-3 dark:border-white/10">
          <p className="text-sm">{action === 'line' ? t('Record bank outcome for {loan}. Only this loan is affected.', { loan: detail.lines.find(line => line.id === lineId)?.loanNumber ?? '' }) : action === 'pay' ? t('Record a completed bank payment for the full batch. This activates the loans and posts the disbursement entries.') : action === 'approve' ? t('Approve this batch for separate loan payment?') : t('Cancel this unpaid batch and release the loans for another batch? Do not cancel if any transfer has already been sent.')}</p>
          {action === 'approve' && detail.createdBy === user?.id && <label className="block text-sm">{t('Why are you approving your own batch?')}<textarea className="input mt-1 w-full" minLength={10} maxLength={500} value={selfReason} onChange={e => setSelfReason(e.target.value)} /></label>}
          {action === 'line' && <><label className="block text-sm">{t('Payment outcome')}<select className="select mt-1 w-full" value={lineOutcome} onChange={e => setLineOutcome(e.target.value as typeof lineOutcome)}><option value="Paid">{t('Paid — payment confirmed')}</option><option value="Failed">{t('Failed — retry later')}</option><option value="Cancelled">{t('Cancelled — release unpaid loan')}</option></select></label>{lineOutcome !== 'Paid' && <p className="text-xs text-amber-700">{t('Confirm that no funds reached the employee before marking this payment failed or cancelled. Record a completed transfer as paid.')}</p>}<label className="block text-sm">{t('Outcome reason')}<textarea className="input mt-1 w-full" maxLength={1000} value={lineReason} onChange={e => setLineReason(e.target.value)} /></label></>}
          {(action === 'pay' || (action === 'line' && lineOutcome === 'Paid')) && <><label className="block text-sm">{t('Payment date')}<input type="date" className="input mt-1 w-full" max={localDateToday()} value={payment.paidDate} onChange={e => setPayment(form => ({ ...form, paidDate: e.target.value }))} /></label><label className="block text-sm">{t('Bank reference')}<input className="input mt-1 w-full" maxLength={160} value={payment.reference} onChange={e => setPayment(form => ({ ...form, reference: e.target.value }))} /></label><label className="block text-sm">{t('Defer first repayment date (optional)')}<input type="date" className="input mt-1 w-full" min={payment.paidDate} value={payment.repaymentStartDate} onChange={e => setPayment(form => ({ ...form, repaymentStartDate: e.target.value }))} /></label><p className="text-xs text-slate-500">{t('Leave blank to keep each approved schedule. If payment was delayed past an approved first repayment date, choose a later date for affected loans.')}</p></>}
          <div className="flex gap-2"><button type="button" className="btn-primary disabled:opacity-50" disabled={saving} onClick={submitAction}>{saving ? t('Processing…') : action === 'line' ? t('Confirm Loan Payment Outcome') : action === 'pay' ? t('Confirm Payment Received by Employees') : action === 'approve' ? t('Confirm Approval') : t('Confirm Cancellation')}</button><button type="button" className="btn-secondary" disabled={saving} onClick={() => setAction(null)}>{t('Back')}</button></div>
        </div>}
      </div>}
    </Modal>
  </div>;
}
