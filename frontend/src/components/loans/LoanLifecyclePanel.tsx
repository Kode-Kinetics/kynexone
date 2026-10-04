'use client';

import { useCallback, useEffect, useState } from 'react';
import { loanGovernanceApi, type LoanChangeRequest, type LoanCorrectionRequest } from '../../api/loanGovernance';
import type { LoanDetail } from '../../api/loans';
import { useAuth } from '../../contexts/AuthContext';
import { loanErrorMessage, localDateToday } from '../../lib/loanWorkflow';

export function LoanLifecyclePanel({ detail, onChanged }: { detail: LoanDetail; onChanged: () => Promise<void> }) {
  const { user } = useAuth();
  const roles = user?.roles ?? [];
  const finance = roles.some(role => ['Admin', 'Finance'].includes(role));
  const hr = roles.some(role => ['Admin', 'HR Manager', 'HR Director'].includes(role));
  const exceptionApprover = roles.some(role => ['Admin', 'HR Director'].includes(role));
  const [changes, setChanges] = useState<LoanChangeRequest[]>([]);
  const [corrections, setCorrections] = useState<LoanCorrectionRequest[]>([]);
  const [form, setForm] = useState<'review' | 'Reschedule' | 'PolicyException' | 'CollectionMethod' | 'ReceiptReversal' | 'DisbursementReversal' | null>(null);
  const [reconciled, setReconciled] = useState(false);
  const [reason, setReason] = useState('');
  const [reviewDecision, setReviewDecision] = useState<'Continue' | 'Hold' | 'Cancel'>('Continue');
  const [installments, setInstallments] = useState(1);
  const [date, setDate] = useState(localDateToday());
  const [codes, setCodes] = useState('');
  const [reference, setReference] = useState('');
  const [repaymentId, setRepaymentId] = useState('');
  const [decisionTarget, setDecisionTarget] = useState<{ id: string; correction: boolean } | null>(null);
  const [decision, setDecision] = useState<'Approved' | 'Rejected'>('Approved');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const load = useCallback(async () => {
    try {
      const [newChanges, newCorrections] = await Promise.all([loanGovernanceApi.changes(detail.loan.id), finance ? loanGovernanceApi.corrections(detail.loan.id) : Promise.resolve([])]);
      setChanges(newChanges.filter(change => ['Reschedule', 'PolicyException', 'CollectionMethod'].includes(change.changeType))); setCorrections(newCorrections);
    } catch (e) { setError(loanErrorMessage(e, 'Unable to load loan review history.')); }
  }, [detail.loan.id, finance]);
  useEffect(() => { void load(); }, [load]);
  const run = async (action: () => Promise<unknown>, success: string) => {
    setSaving(true); setError(''); setNotice('');
    try { await action(); await onChanged(); await load(); setForm(null); setDecisionTarget(null); setNotice(success); }
    catch (e) { setError(loanErrorMessage(e, 'Unable to process this loan request.')); }
    finally { setSaving(false); }
  };
  const begin = (value: typeof form) => { setForm(value); setDecisionTarget(null); setReason(''); setCodes(''); setReference(''); setReconciled(false); setDate(localDateToday()); setRepaymentId(detail.repayments.find(receipt => !receipt.isReversed)?.id ?? ''); setError(''); };
  const submit = () => {
    if (reason.trim().length < 3 || reason.length > 2000) { setError('Enter a reason of 3–2000 characters for the audit history.'); return; }
    if (decisionTarget) { void run(() => decisionTarget.correction ? loanGovernanceApi.decideCorrection(detail.loan.id, decisionTarget.id, { decision, reason }) : loanGovernanceApi.decideChange(detail.loan.id, decisionTarget.id, { decision, reason }), 'Decision recorded.'); return; }
    if (form === 'review') { void run(() => loanGovernanceApi.reviewLifecycle(detail.loan.id, { decision: reviewDecision, reason }), 'Lifecycle review recorded.'); return; }
    if (form === 'CollectionMethod') {
      if (!reconciled || !reference.trim()) { setError('Confirm the reconciled manual-only collection history and provide its evidence reference.'); return; }
      void run(() => loanGovernanceApi.requestChange(detail.loan.id, { changeType: 'CollectionMethod', reason, repaymentMethod: 'BankTransfer', reconciliationReference: reference, confirmNoPayrollCollection: true }), 'Conversion requested. Independent Finance approval and a separate HR hold release are required.'); return;
    }
    if (form === 'Reschedule' || form === 'PolicyException') {
      void run(() => loanGovernanceApi.requestChange(detail.loan.id, { changeType: form, reason, ...(form === 'Reschedule' ? { installments, startDate: date } : { exceptionCodes: codes.split(',').map(code => code.trim()).filter(Boolean) }) }), 'Change submitted for an independent approval.'); return;
    }
    if (form === 'ReceiptReversal' || form === 'DisbursementReversal') {
      if (!reference.trim() || !date || (form === 'ReceiptReversal' && !repaymentId)) { setError('Choose the original receipt, effective date and correction reference.'); return; }
      void run(() => loanGovernanceApi.requestCorrection(detail.loan.id, { changeType: form, reason, effectiveDate: date, reference, repaymentId: form === 'ReceiptReversal' ? repaymentId : undefined }), 'Correction submitted. A different Finance user must approve it.');
    }
  };
  const canDecide = (change: LoanChangeRequest) => change.status === 'Pending' && change.createdBy !== user?.id && detail.loan.createdBy !== user?.id && (change.changeType === 'PolicyException' ? exceptionApprover : finance);
  return <div className="space-y-4">
    <div className="rounded-lg bg-slate-50 p-3 text-sm dark:bg-white/5"><p>Collection status: <strong>{detail.loan.collectionStatus || 'Normal'}</strong> · Policy {detail.loan.policyVersion ? `v${detail.loan.policyVersion}` : 'not assigned'}</p>{detail.loan.reviewRequired && <p className="mt-2 text-amber-700 dark:text-amber-300">HR review required: {detail.loan.reviewReason || 'Employment details have changed.'}</p>}</div>
    {error && <p role="alert" className="text-sm text-red-600">{error}</p>}{notice && <p role="status" className="text-sm text-emerald-700">{notice}</p>}
    {!form && !decisionTarget && <div className="flex flex-wrap gap-2">
      {(hr || finance) && <button className="btn-secondary" disabled={saving} onClick={() => run(() => loanGovernanceApi.refreshLifecycle(detail.loan.id), 'Employment context refreshed.')}>Refresh Employment Review</button>}
      {hr && ['Pending', 'Approved', 'Active', 'Overdue'].includes(detail.loan.status) && detail.loan.createdBy !== user?.id && <button className="btn-secondary" onClick={() => begin('review')}>Record HR Review</button>}
      {(hr || finance) && ['Active', 'Overdue'].includes(detail.loan.status) && detail.loan.repaymentMethod !== 'PayrollDeduction' && <button className="btn-secondary" onClick={() => begin('Reschedule')}>Request Reschedule</button>}
      {(hr || finance) && ['Pending', 'Approved'].includes(detail.loan.status) && <button className="btn-secondary" onClick={() => begin('PolicyException')}>Request Policy Exception</button>}
      {finance && detail.loan.repaymentMethod === 'PayrollDeduction' && !detail.loan.policyVersion && ['Active', 'Overdue'].includes(detail.loan.status) && <button className="btn-secondary" onClick={() => begin('CollectionMethod')}>Review Legacy Collection Method</button>}
      {finance && detail.repayments.some(receipt => !receipt.isReversed) && <button className="btn-secondary" onClick={() => begin('ReceiptReversal')}>Request Receipt Reversal</button>}
      {finance && detail.loan.disbursementDate && detail.loan.totalRepaid === 0 && <button className="btn-secondary" onClick={() => begin('DisbursementReversal')}>Request Disbursement Reversal</button>}
    </div>}
    {(form || decisionTarget) && <div className="space-y-3 rounded-lg border border-slate-200 p-3 dark:border-white/10">
      <h3 className="text-sm font-semibold">{decisionTarget ? 'Independent approval decision' : form === 'review' ? 'HR lifecycle review' : form?.replace(/([A-Z])/g, ' $1').trim()}</h3>
      {decisionTarget && <label className="block text-sm">Decision<select className="select mt-1 w-full" value={decision} onChange={e => setDecision(e.target.value as typeof decision)}><option>Approved</option><option>Rejected</option></select></label>}
      {form === 'review' && <label className="block text-sm">HR action<select className="select mt-1 w-full" value={reviewDecision} onChange={e => setReviewDecision(e.target.value as typeof reviewDecision)}><option value="Continue">Continue collection / release hold</option><option value="Hold">Hold for review</option><option value="Cancel">Cancel undisbursed loan</option></select></label>}
      {form === 'Reschedule' && <><p className="text-xs text-slate-500">Only the remaining unpaid principal is rescheduled. Paid history is preserved.</p><label className="block text-sm">Remaining installments<input type="number" min="1" step="1" className="input mt-1 w-full" value={installments} onChange={e => setInstallments(Number(e.target.value))} /></label><label className="block text-sm">First due date<input type="date" className="input mt-1 w-full" min={localDateToday()} value={date} onChange={e => setDate(e.target.value)} /></label></>}
      {form === 'PolicyException' && <label className="block text-sm">Eligibility reason codes (comma-separated)<input className="input mt-1 w-full" value={codes} onChange={e => setCodes(e.target.value)} /><span className="mt-1 block text-xs text-slate-500">Use the policy reason codes shown by the eligibility check. Exceptions require HR Director approval.</span></label>}
      {form === 'CollectionMethod' && <><p className="text-sm text-slate-600">For historical manual-only loans, after HR places collection on hold. Finance must reconcile the original journal, paid history and balance. Loans with payroll collection evidence cannot use this conversion. Approval switches future collection to bank transfer without changing debt; HR must then release the hold.</p><label className="block text-sm">Reconciliation evidence reference<input className="input mt-1 w-full" maxLength={160} value={reference} onChange={e => setReference(e.target.value)} /></label><label className="flex items-start gap-2 text-sm"><input type="checkbox" checked={reconciled} onChange={e => setReconciled(e.target.checked)} />I verified the history is manual-only, with no payroll collections.</label></>}
      {form === 'ReceiptReversal' && <label className="block text-sm">Original receipt<select className="select mt-1 w-full" value={repaymentId} onChange={e => setRepaymentId(e.target.value)}><option value="">Choose receipt</option>{detail.repayments.filter(receipt => !receipt.isReversed).map(receipt => <option key={receipt.id} value={receipt.id}>{receipt.paidDate} · {receipt.reference} · {receipt.amount} {detail.loan.currency}</option>)}</select></label>}
      {(form === 'ReceiptReversal' || form === 'DisbursementReversal') && <><p className="text-xs text-slate-500">This records a correction with an independent approval. Original transactions remain visible.</p><label className="block text-sm">Effective date<input type="date" max={localDateToday()} className="input mt-1 w-full" value={date} onChange={e => setDate(e.target.value)} /></label><label className="block text-sm">Correction reference<input className="input mt-1 w-full" maxLength={160} value={reference} onChange={e => setReference(e.target.value)} /></label></>}
      <label className="block text-sm">Reason<textarea className="input mt-1 w-full" rows={3} maxLength={2000} value={reason} onChange={e => setReason(e.target.value)} /></label>
      <div className="flex gap-2"><button className="btn-primary" disabled={saving} onClick={submit}>{saving ? 'Saving…' : decisionTarget ? 'Submit Review Decision' : 'Submit Request'}</button><button className="btn-secondary" disabled={saving} onClick={() => { setForm(null); setDecisionTarget(null); }}>Cancel</button></div>
    </div>}
    <div className="space-y-2"><h3 className="text-sm font-semibold">Changes and approvals</h3>{changes.length === 0 && <p className="text-sm text-slate-500">No loan change requests.</p>}{changes.map(change => <div key={change.id} className="surface space-y-1 p-3 text-sm"><div className="flex flex-wrap justify-between gap-2"><strong>{change.changeType} · {change.status}</strong>{canDecide(change) && <button className="text-sapphire" onClick={() => { setDecisionTarget({ id: change.id, correction: false }); setForm(null); setReason(''); setDecision('Approved'); }}>Review Change</button>}</div><p>{change.reason}</p>{change.requestedStartDate && <p className="text-xs text-slate-500">{change.requestedInstallments} installments starting {change.requestedStartDate}</p>}
      {change.changeType === 'CollectionMethod' && <p className="text-sm">Target collection: {change.requestedRepaymentMethod || 'Not recorded'} · Reconciliation evidence: {change.reference || 'Not recorded'}</p>}
      {change.decisionReason && <p className="text-xs text-slate-500">Decision: {change.decisionReason}</p>}</div>)}</div>
    {finance && <div className="space-y-2"><h3 className="text-sm font-semibold">Financial corrections</h3>{corrections.length === 0 && <p className="text-sm text-slate-500">No correction requests.</p>}{corrections.map(correction => <div key={correction.id} className="surface p-3 text-sm"><div className="flex flex-wrap justify-between gap-2"><strong>{correction.changeType} · {correction.status}</strong>{correction.status === 'Pending' && correction.createdBy !== user?.id && <button className="text-sapphire" onClick={() => { setDecisionTarget({ id: correction.id, correction: true }); setForm(null); setReason(''); setDecision('Approved'); }}>Review Correction</button>}</div><p>{correction.reason}</p><p className="text-xs text-slate-500">{correction.effectiveDate} · {correction.reference}</p></div>)}</div>}
  </div>;
}
