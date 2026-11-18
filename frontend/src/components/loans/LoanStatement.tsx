'use client';

import { useEffect, useRef, useState } from 'react';
import { loansApi, type EmployeeLoan, type LoanDetail } from '../../api/loans';
import { loanErrorMessage, localDateToday } from '../../lib/loanWorkflow';
import { useFormat } from '../../hooks/useFormat';
import { useLocale } from '../../contexts/LocaleContext';

function totals(loans: EmployeeLoan[]) {
  return Object.values(loans.reduce<Record<string, { currency: string; requested: number; approved: number; disbursed: number; repaid: number; outstanding: number }>>((groups, loan) => {
    const group = groups[loan.currency] ??= { currency: loan.currency, requested: 0, approved: 0, disbursed: 0, repaid: 0, outstanding: 0 };
    group.requested += loan.requestedAmount; group.approved += loan.approvedAmount;
    group.disbursed += loan.disbursementDate ? loan.approvedAmount : 0;
    group.repaid += loan.totalRepaid; group.outstanding += loan.outstandingBalance;
    return groups;
  }, {}));
}

const LABELS = { requested: 'Requested', approved: 'Approved', disbursed: 'Disbursed', repaid: 'Repaid', outstanding: 'Outstanding' } as const;

export function LoanAccountSummary({ mine, version }: { mine: boolean; version: number }) {
  const { t } = useLocale();
  const tRef = useRef(t);
  tRef.current = t;
  // A loan carried in with no currency (before #188 stamped it) used to throw here and blank the Loans page.
  const fx = useFormat();
  const [loans, setLoans] = useState<EmployeeLoan[]>([]);
  const [error, setError] = useState('');
  useEffect(() => {
    let active = true;
    void (async () => {
      try {
        const first = await loansApi.list({ mine, page: 1, pageSize: 100 });
        const all = [...first.items];
        for (let page = 2; all.length < first.total; page++) { const next = await loansApi.list({ mine, page, pageSize: 100 }); if (!next.items.length) break; all.push(...next.items); }
        if (active) { setLoans(all); setError(''); }
      } catch (e) { if (active) setError(loanErrorMessage(e, tRef.current('Unable to load account totals.'))); }
    })();
    return () => { active = false; };
  }, [mine, version]);
  if (error) return <p role="alert" className="text-sm text-red-600">{error}</p>;
  return <div className="space-y-3">{totals(loans).map(group => <section key={group.currency} className="surface flex flex-wrap items-center gap-x-8 gap-y-2 px-4 py-3" aria-label={t('{currency} loan account totals', { currency: fx.currencyLabel(group.currency) })}><h2 className="text-sm font-semibold">{t('{currency} loan account', { currency: fx.currencyLabel(group.currency) })}</h2><dl className="grid min-w-0 flex-1 grid-cols-2 gap-x-6 gap-y-2 md:grid-cols-5">{(['requested', 'approved', 'disbursed', 'repaid', 'outstanding'] as const).map(key => <div key={key}><dt className="text-xs text-slate-500">{t(LABELS[key])}</dt><dd className="mt-0.5 font-semibold tabular-nums">{fx.plain.money(group[key], group.currency || null)}</dd></div>)}</dl></section>)}</div>;
}

export function LoanStatement({ detail }: { detail: LoanDetail }) {
  const { t } = useLocale();
  // The shared formatter: a loan carried in before its currency was stamped (null) shows a bare amount
  // instead of throwing "Invalid currency code" and taking the whole panel down.
  const fx = useFormat();
  const { loan, installments } = detail;
  const today = localDateToday();
  const remaining = (due: number, paid: number) => Math.max(0, due - paid);
  const collectible = installments.filter(row => !['Cancelled', 'Waived'].includes(row.status));
  const due = collectible.filter(row => row.dueDate <= today).reduce((sum, row) => sum + remaining(row.amountDue, row.amountPaid), 0);
  const overdue = collectible.filter(row => row.dueDate < today).reduce((sum, row) => sum + remaining(row.amountDue, row.amountPaid), 0);
  const values = [
    ['Requested', loan.requestedAmount], ['Approved', loan.approvedAmount], ['Disbursed', loan.disbursementDate ? loan.approvedAmount : 0],
    ['Repaid', loan.totalRepaid], ['Outstanding', loan.outstandingBalance], ['Due to date', due], ['Overdue', overdue],
  ] as const;
  return <section className="space-y-3" aria-label={t('Loan statement')}><div className="flex flex-wrap justify-between gap-2"><h3 className="text-sm font-semibold">{t('Loan statement · {currency}', { currency: fx.currencyLabel(loan.currency) })}</h3><p className="text-xs text-slate-500">{t('As of {date}', { date: fx.plain.date(today) })}</p></div><dl className="grid grid-cols-2 gap-3 sm:grid-cols-3">{values.map(([label, amount]) => <div key={label} className="surface rounded-lg p-3"><dt className="text-xs text-slate-500">{t(label)}</dt><dd className="mt-1 font-semibold">{fx.plain.money(amount, loan.currency || null)}</dd></div>)}</dl><p className="text-xs text-slate-500">{t('Approval is a commitment. Disbursed amounts reflect confirmed payments; repayment receipts and installment history appear below.')}</p></section>;
}
