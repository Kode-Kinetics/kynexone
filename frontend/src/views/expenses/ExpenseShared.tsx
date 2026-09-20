'use client';

import { Paperclip } from 'lucide-react';
import type { ExpenseClaim, ExpenseClaimStatus, ExpenseViolation } from '../../api/expenses';
import { formatMoney } from '../../api/expenses';

const STATUS_CLS: Record<ExpenseClaimStatus, string> = {
  Draft: 'bg-slate-100 text-slate-600 dark:bg-white/[0.06] dark:text-slate-300',
  Submitted: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-300',
  Approved: 'bg-blue-50 text-blue-700 dark:bg-blue-500/10 dark:text-blue-300',
  Rejected: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-300',
  Scheduled: 'bg-indigo-50 text-indigo-700 dark:bg-indigo-500/10 dark:text-indigo-300',
  Paid: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-300',
  Cancelled: 'bg-slate-100 text-slate-400 line-through dark:bg-white/[0.04] dark:text-slate-500',
};

const STATUS_LABEL: Record<ExpenseClaimStatus, string> = {
  Draft: 'Draft',
  Submitted: 'Awaiting approval',
  Approved: 'Approved — awaiting payroll',
  Rejected: 'Rejected',
  Scheduled: 'In a payroll run',
  Paid: 'Paid via payroll',
  Cancelled: 'Cancelled',
};

export function ExpenseStatusBadge({ status }: { status: ExpenseClaimStatus }) {
  return (
    <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-semibold ${STATUS_CLS[status] ?? STATUS_CLS.Draft}`}>
      {STATUS_LABEL[status] ?? status}
    </span>
  );
}

/** One sentence that tells the reader where the claim is, in plain words. */
export function ExpenseProgressNote({ claim }: { claim: ExpenseClaim }) {
  let text: string | null = null;
  if (claim.status === 'Submitted' && claim.approval)
    text = `Step ${claim.approval.currentStepOrder}: waiting for ${claim.approval.currentApproverName || claim.approval.currentApproverRole || 'an approver'}.`;
  else if (claim.status === 'Approved') text = 'Approved. It will be paid with the next payroll run payroll adds it to.';
  else if (claim.status === 'Scheduled') text = `Will be paid in the ${claim.payrollPeriod ?? 'next'} payroll.`;
  else if (claim.status === 'Paid') text = `Paid in the ${claim.payrollPeriod ?? ''} payroll.`;
  else if (claim.status === 'Rejected') text = claim.rejectionReason ? `Reason: ${claim.rejectionReason}` : 'Rejected.';
  if (!text) return null;
  return (
    <p className={`text-xs ${claim.status === 'Rejected' ? 'text-rose-600 dark:text-rose-300' : 'text-slate-500 dark:text-slate-400'}`}>{text}</p>
  );
}

export function ExpenseLinesTable({
  claim,
  onReceipt,
  renderReceiptAction,
}: {
  claim: ExpenseClaim;
  onReceipt?: (lineId: string, fileName: string) => void;
  renderReceiptAction?: (lineId: string) => React.ReactNode;
}) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-xs">
        <thead>
          <tr className="text-left text-[11px] uppercase tracking-wide text-slate-400">
            <th className="py-1.5 pr-3 font-semibold">Date</th>
            <th className="py-1.5 pr-3 font-semibold">Category</th>
            <th className="py-1.5 pr-3 font-semibold">Description</th>
            <th className="py-1.5 pr-3 text-right font-semibold">Amount</th>
            <th className="py-1.5 font-semibold">Receipt</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">
          {claim.lines.map((l) => (
            <tr key={l.id} className="text-slate-700 dark:text-slate-200">
              <td className="py-1.5 pr-3 whitespace-nowrap">{l.expenseDate}</td>
              <td className="py-1.5 pr-3">{l.categoryName}</td>
              <td className="py-1.5 pr-3">{l.description}</td>
              <td className="py-1.5 pr-3 text-right tabular-nums">{formatMoney(l.amount, null)}</td>
              <td className="py-1.5">
                <div className="flex items-center gap-2">
                  {l.hasReceipt ? (
                    onReceipt ? (
                      <button type="button" onClick={() => onReceipt(l.id, l.receiptFileName ?? 'receipt')} className="inline-flex items-center gap-1 text-blue-600 hover:underline dark:text-blue-400">
                        <Paperclip className="h-3 w-3" /> {l.receiptFileName ?? 'Receipt'}
                      </button>
                    ) : (
                      <span className="inline-flex items-center gap-1 text-slate-500"><Paperclip className="h-3 w-3" /> {l.receiptFileName}</span>
                    )
                  ) : (
                    <span className="text-slate-400">None</span>
                  )}
                  {renderReceiptAction?.(l.id)}
                </div>
              </td>
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr className="font-semibold text-slate-800 dark:text-slate-100">
            <td colSpan={3} className="pt-2 text-right">Total</td>
            <td className="pt-2 pr-3 text-right tabular-nums">{formatMoney(claim.totalAmount, claim.currency)}</td>
            <td />
          </tr>
        </tfoot>
      </table>
    </div>
  );
}

export function ViolationList({ message, violations }: { message: string | null; violations: ExpenseViolation[] }) {
  if (!message && violations.length === 0) return null;
  return (
    <div role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-xs text-rose-700 dark:border-rose-500/20 dark:bg-rose-500/[0.08] dark:text-rose-300">
      {violations.length > 1 ? (
        <ul className="list-disc space-y-0.5 pl-4">
          {violations.map((v, i) => <li key={`${v.code}-${i}`}>{v.message}</li>)}
        </ul>
      ) : (
        <p>{violations[0]?.message ?? message}</p>
      )}
    </div>
  );
}

export function SkeletonRows({ rows = 3 }: { rows?: number }) {
  return (
    <div className="space-y-2" aria-busy="true">
      {Array.from({ length: rows }).map((_, i) => (
        <div key={i} className="h-20 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" />
      ))}
    </div>
  );
}
