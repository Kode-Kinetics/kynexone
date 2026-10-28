'use client';

import { isolateLtr, isolateName } from '../lib/bidi';
import Link from 'next/link';
import { useParams } from 'next/navigation';
import { useCallback, useEffect, useRef, useState } from 'react';
import { AlertTriangle, ArrowLeft, CheckCircle2, Loader2 } from 'lucide-react';
import { payApprovalsApi, type PayApprovalDecision, type PayApprovalDetail } from '../api/payApprovals';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';
import { maskedAccount, payApprovalFailure, reasonError, REASON_MAX, type PayApprovalFailure } from '../lib/payApprovals';

/**
 * ONE pay package, narrowest view (OD4): who and where, the salary components, the bank account masked to its last four
 * characters, who entered it, and an opaque fingerprint. The decision binds to that fingerprint; if the package moved the
 * server answers package_changed and this page asks for a reload instead of approving figures nobody saw.
 */
export function PayApprovalDetailPage() {
  const { t } = useLocale();
  const fmt = useFormat();
  const params = useParams<{ employeeId: string }>();
  const employeeId = Number(params?.employeeId);
  const [detail, setDetail] = useState<PayApprovalDetail | null>(null);
  const [loadFailure, setLoadFailure] = useState<PayApprovalFailure | null>(null);
  const [loading, setLoading] = useState(true);
  const [reason, setReason] = useState('');
  const [reasonProblem, setReasonProblem] = useState<string | null>(null);
  const [busy, setBusy] = useState<PayApprovalDecision | null>(null);
  const [failure, setFailure] = useState<PayApprovalFailure | null>(null);
  const [outcome, setOutcome] = useState<'approved' | 'rejected' | null>(null);
  const outcomeRef = useRef<HTMLDivElement>(null);
  const when = (iso: string) => fmt.dateTime(iso);

  const load = useCallback(async () => {
    if (!Number.isInteger(employeeId)) { setLoadFailure({ message: 'This package is not available to you.', reload: false }); setLoading(false); return; }
    setLoading(true); setLoadFailure(null); setFailure(null);
    try { setDetail(await payApprovalsApi.detail(employeeId)); } catch (e) { setDetail(null); setLoadFailure(payApprovalFailure(e)); } finally { setLoading(false); }
  }, [employeeId]);
  useEffect(() => { void load(); }, [load]);
  useEffect(() => { if (outcome) outcomeRef.current?.focus(); }, [outcome]);

  async function decide(decision: PayApprovalDecision) {
    if (!detail) return;
    const problem = reasonError(reason);
    setReasonProblem(problem);
    if (problem) return;
    setBusy(decision); setFailure(null);
    try {
      const r = await payApprovalsApi.decide(employeeId, { fingerprint: detail.fingerprint, decision, reason: reason.trim() });
      setOutcome(r.decision);
    } catch (e) {
      setFailure(payApprovalFailure(e));
    } finally { setBusy(null); }
  }

  const back = <Link href="/pay-approvals" className="inline-flex items-center gap-1 text-sm font-semibold text-sapphire hover:underline dark:text-blue-300"><ArrowLeft className="h-4 w-4 rtl:rotate-180" aria-hidden="true" />{t('Back to pay approvals')}</Link>;

  if (loading) return <p className="flex items-center gap-2 text-sm text-slate-500"><Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />{t('Loading…')}</p>;
  if (loadFailure || !detail) {
    return (
      <div className="mx-auto max-w-3xl space-y-3">
        {back}
        <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-sm text-rose-800 dark:border-rose-400/30 dark:bg-rose-500/10 dark:text-rose-200">{t(loadFailure?.message ?? 'This package is not available to you.')}</p>
      </div>
    );
  }

  if (outcome) {
    return (
      <div className="mx-auto max-w-3xl space-y-3">
        <div ref={outcomeRef} tabIndex={-1} role="status" data-testid="pay-approval-outcome" className="rounded-xl border border-emerald-200 bg-emerald-50/70 p-4 text-sm text-slate-900 outline-none dark:border-emerald-400/30 dark:bg-emerald-500/10 dark:text-white">
          <p className="flex items-center gap-2 font-semibold"><CheckCircle2 className="h-4 w-4 shrink-0" aria-hidden="true" />{detail.employee.name}</p>
          <p className="mt-1">{outcome === 'approved' ? t('Approved. The pay package no longer blocks activation.') : t('Sent back. The pay package still blocks activation until it is corrected and approved.')}</p>
        </div>
        {back}
      </div>
    );
  }

  const c = detail.components;
  const rows: Array<[string, number]> = [
    ['Basic salary', c.basic], ['Housing allowance', c.housing], ['Transport allowance', c.transport], ['Food allowance', c.food],
    ['Mobile allowance', c.mobile], ['Other allowances', c.other], ['Fixed deduction', c.fixedDeduction],
  ];
  const label = 'text-xs text-slate-500 dark:text-slate-400';
  return (
    <div className="mx-auto max-w-3xl space-y-4">
      {back}
      <header>
        <h1 className="break-words text-xl font-semibold text-slate-900 dark:text-white">{t('Pay package')}: {detail.employee.name}</h1>
        <p className="text-sm text-slate-600 dark:text-slate-300"><bdi>{detail.employee.code}</bdi>{detail.employee.designation ? ` · ${detail.employee.designation}` : ''}{detail.employee.companyName ? ` · ${detail.employee.companyName}` : ''}</p>
      </header>

      {detail.sharedIbanCount > 0 && (
        <div role="alert" data-testid="shared-iban-warning" className="rounded-xl border border-amber-300 bg-amber-50 p-3 text-sm text-amber-950 dark:border-amber-400/40 dark:bg-amber-500/10 dark:text-amber-100">
          <p className="flex items-start gap-2 font-semibold"><AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />{t('Warning: this IBAN is also on {count, plural, one {another employee} two {2 other employees} few {# other employees} many {# other employees} other {# other employees}} in your organization.', { count: detail.sharedIbanCount })}</p>
          <p className="mt-1 ps-6">{t('Check that the account really belongs to this person before you approve. For privacy, the other records are not shown here.')}</p>
        </div>
      )}

      {detail.lastRejection && (
        <p className="rounded-lg border border-slate-200 p-3 text-sm dark:border-white/10">{t('Sent back on {when} by {name}: {reason}', { when: isolateLtr(when(detail.lastRejection.atUtc)), name: isolateName(detail.lastRejection.byName), reason: detail.lastRejection.reason })}</p>
      )}

      <section aria-labelledby="pa-salary" className="rounded-xl border border-slate-200 p-4 dark:border-white/10">
        <h2 id="pa-salary" className="text-sm font-semibold">{t('Salary')}</h2>
        <dl className="mt-2 grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
          {rows.filter(([, v]) => v !== 0).map(([name, v]) => (
            <div key={name}><dt className={label}>{t(name)}</dt><dd className="font-medium" dir="ltr">{fmt.plain.money(v, c.currency)}</dd></div>
          ))}
          <div className="sm:col-span-2 border-t border-slate-200 pt-2 dark:border-white/10"><dt className={label}>{t('Gross (basic and allowances)')}</dt><dd className="text-base font-semibold" dir="ltr" data-testid="pa-gross">{fmt.plain.money(c.gross, c.currency)}</dd></div>
        </dl>
      </section>

      <section aria-labelledby="pa-bank" className="rounded-xl border border-slate-200 p-4 dark:border-white/10">
        <h2 id="pa-bank" className="text-sm font-semibold">{t('Bank account')}</h2>
        <dl className="mt-2 grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
          <div><dt className={label}>{t('Bank')}</dt><dd className="font-medium">{detail.bank.bankName || '—'}</dd></div>
          <div><dt className={label}>{t('Account holder name')}</dt><dd className="break-words font-medium">{detail.bank.accountHolderName}</dd></div>
          {detail.bank.ibanLast4 && <div><dt className={label}>{t('IBAN (last 4 characters)')}</dt><dd className="font-medium" dir="ltr" data-testid="pa-iban">{maskedAccount(detail.bank.ibanLast4)}</dd></div>}
          {!detail.bank.ibanLast4 && detail.bank.accountLast4 && <div><dt className={label}>{t('Account number (last 4 characters)')}</dt><dd className="font-medium" dir="ltr">{maskedAccount(detail.bank.accountLast4)}</dd></div>}
        </dl>
      </section>

      <section aria-labelledby="pa-trail" className="rounded-xl border border-slate-200 p-4 dark:border-white/10">
        <h2 id="pa-trail" className="text-sm font-semibold">{t('Entered')}</h2>
        <dl className="mt-2 grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
          <div><dt className={label}>{t('Entered by')}</dt><dd className="font-medium">{detail.enteredBy ? t('{name} on {when}', { name: isolateName(detail.enteredBy.name), when: isolateLtr(when(detail.enteredBy.atUtc)) }) : '—'}</dd></div>
          <div><dt className={label}>{t('Last changed by')}</dt><dd className="font-medium">{detail.lastChangedBy ? t('{name} on {when}', { name: isolateName(detail.lastChangedBy.name), when: isolateLtr(when(detail.lastChangedBy.atUtc)) }) : '—'}</dd></div>
          <div className="sm:col-span-2"><dt className={label}>{t('Package fingerprint')}</dt><dd className="break-all font-mono text-xs" dir="ltr" data-testid="pa-fingerprint">{detail.fingerprint.slice(0, 16)}</dd></div>
        </dl>
        <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">{t('Your decision applies to exactly this package. If it changes before you decide, you will be asked to review it again.')}</p>
      </section>

      <form className="space-y-3" onSubmit={e => e.preventDefault()} noValidate>
        <div>
          <label htmlFor="pa-reason" className="block text-sm font-semibold">{t('Reason')}</label>
          <p id="pa-reason-hint" className="text-xs text-slate-500 dark:text-slate-400">{t('What did you check? This is kept in the audit trail. Do not write the account number.')}</p>
          <textarea id="pa-reason" rows={3} maxLength={REASON_MAX} value={reason} onChange={e => { setReason(e.target.value); if (reasonProblem) setReasonProblem(null); }}
            aria-describedby={reasonProblem ? 'pa-reason-hint pa-reason-error' : 'pa-reason-hint'} aria-invalid={reasonProblem ? true : undefined}
            className="input mt-1 w-full" />
          {reasonProblem && <p id="pa-reason-error" role="alert" className="mt-1 text-sm text-rose-700 dark:text-rose-300">{t(reasonProblem)}</p>}
        </div>
        {failure && (
          <div role="alert" data-testid="pay-approval-failure" className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-sm text-rose-800 dark:border-rose-400/30 dark:bg-rose-500/10 dark:text-rose-200">
            <p>{t(failure.message)}</p>
            {failure.reload && <button type="button" className="btn-secondary mt-2" onClick={() => { setReason(''); void load(); }}>{t('Reload the package')}</button>}
          </div>
        )}
        <div className="flex flex-wrap gap-2">
          <button type="button" className="btn-primary min-h-11" disabled={busy !== null} onClick={() => void decide('approve')}>{busy === 'approve' ? t('Saving…') : t('Approve')}</button>
          <button type="button" className="btn-secondary min-h-11" disabled={busy !== null} onClick={() => void decide('reject')}>{busy === 'reject' ? t('Saving…') : t('Send back')}</button>
        </div>
      </form>
    </div>
  );
}
