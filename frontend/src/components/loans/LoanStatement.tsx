'use client';

import { useEffect, useState } from 'react';
import { loansApi, type EmployeeLoan, type LoanDetail } from '../../api/loans';
import { loanErrorMessage, localDateToday } from '../../lib/loanWorkflow';
import { useFormat } from '../../hooks/useFormat';

function totals(loans: EmployeeLoan[]) {
  return Object.values(loans.reduce<Record<string, { currency: string; requested: number; approved: number; disbursed: number; repaid: number; outstanding: number }>>((groups, loan) => {
    const group = groups[loan.currency] ??= { currency: loan.currency, requested: 0, approved: 0, disbursed: 0, repaid: 0, outstanding: 0 };
    group.requested += loan.requestedAmount; group.approved += loan.approvedAmount;
    group.disbursed += loan.disbursementDate ? loan.approvedAmount : 0;
    group.repaid += loan.totalRepaid; group.outstanding += loan.outstandingBalance;
    return groups;
  }, {}));
}

export function LoanAccountSummary({ mine, version }: { mine: boolean; version: number }) {
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
      } catch (e) { if (active) setError(loanErrorMessage(e, 'Unable to load account totals.')); }
    })();
    return () => { active = false; };
  }, [mine, version]);
  if (error) return <p role="alert" className="text-sm text-red-600">{error}</p>;
  return <div className="space-y-3">{totals(loans).map(group => <section key={group.currency} className="surface p-4" aria-label={`${group.currency} loan account totals`}><h2 className="mb-3 text-sm font-semibold">{group.currency} loan account</h2><dl className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">{(['requested', 'approved', 'disbursed', 'repaid', 'outstanding'] as const).map(key => <div key={key}><dt className="text-xs capitalize text-slate-500">{key}</dt><dd className="mt-1 font-semibold">{fx.plain.money(group[key], group.currency || null)}</dd></div>)}</dl></section>)}</div>;
}

export function LoanStatement({ detail }: { detail: LoanDetail }) {
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
  return <section className="space-y-3" aria-label="Loan statement"><div className="flex flex-wrap justify-between gap-2"><h3 className="text-sm font-semibold">Loan statement · {loan.currency}</h3><p className="text-xs text-slate-500">As of {today}</p></div><dl className="grid grid-cols-2 gap-3 sm:grid-cols-3">{values.map(([label, amount]) => <div key={label} className="surface rounded-lg p-3"><dt className="text-xs text-slate-500">{label}</dt><dd className="mt-1 font-semibold">{fx.plain.money(amount, loan.currency || null)}</dd></div>)}</dl><p className="text-xs text-slate-500">Approval is a commitment. Disbursed amounts reflect confirmed payments; repayment receipts and installment history appear below.</p></section>;
}
