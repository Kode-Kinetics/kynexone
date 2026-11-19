'use client';

import { AlertTriangle } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { exitDebtSummary, moneyByCurrency, type ExitDebt } from '../../lib/exitDebts';
import { repaymentMethodLabels } from '../../lib/loanWorkflow';
import type { LoanRepaymentMethod } from '../../api/loans';

/**
 * Every loan and salary advance a leaver still owes, on the offboarding card and on the final settlement. A loan repaid by
 * bank transfer used to be invisible at exit and was never recovered; a held loan is listed and named so nobody pays the
 * leaver out believing it was covered.
 */
export function ExitDebtsPanel({ debts, employeeName, fallbackCurrency, showPlanned = false, unrecovered = 0, testId }: {
  debts: ExitDebt[];
  employeeName: string;
  fallbackCurrency?: string;
  /** The settlement view shows what the final pay will recover from each debt. */
  showPlanned?: boolean;
  /** The settlement's shortfall: what the final pay cannot fund (in the settlement currency). */
  unrecovered?: number;
  testId?: string;
}) {
  const { t } = useLocale();
  const fx = useFormat();
  const summary = exitDebtSummary(debts, fallbackCurrency ?? '');
  const money = (amount: number, currency: string) => fx.money(amount, currency);
  const total = moneyByCurrency(summary.byCurrency, money);
  const held = moneyByCurrency(summary.heldByCurrency, money);

  return (
    <div className="rounded-lg border border-slate-200 p-2.5 text-xs dark:border-white/10" data-testid={testId}>
      <p className="mb-1 font-semibold text-slate-700 dark:text-slate-300">{t('Loans and advances still owed')}</p>
      {debts.length === 0 ? (
        <p className="text-slate-500 dark:text-slate-400">{t('Nothing owed: no open loan or salary advance.')}</p>
      ) : (
        <>
          <ul className="space-y-1">
            {debts.map(d => (
              <li key={`${d.kind}-${d.id}`} className="flex flex-wrap items-center justify-between gap-2">
                <span className="text-slate-700 dark:text-slate-300">
                  <bdi>{d.reference}</bdi> · {d.kind === 'Loan' ? t('Loan balance') : t('Salary advance balance')}
                  {d.repaymentMethod && repaymentMethodLabels[d.repaymentMethod as LoanRepaymentMethod]
                    ? ` · ${t(repaymentMethodLabels[d.repaymentMethod as LoanRepaymentMethod])}` : ''}
                  {d.held && <span className="ms-1 font-medium text-amber-700 dark:text-amber-400">· {t('On hold, not recovered from the final pay')}</span>}
                </span>
                <span className="font-semibold tabular-nums text-slate-800 dark:text-white">
                  {fx.money(d.outstandingBalance, d.currency || fallbackCurrency || '')}
                  {showPlanned && !d.held && (
                    <span className="ms-2 font-normal text-slate-500 dark:text-slate-400">
                      {t('Planned from final pay: {amount}', { amount: fx.money(d.plannedRecovery, d.currency || fallbackCurrency || '') })}
                    </span>
                  )}
                </span>
              </li>
            ))}
          </ul>
          {total && (
            <p className="mt-2 flex items-start gap-1 text-amber-700 dark:text-amber-400" role="status">
              <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
              <span>{t('{name} still owes {amount}. The final settlement recovers what it can; what it cannot is moved to the employee receivable when the settlement is paid.', { name: employeeName, amount: total })}</span>
            </p>
          )}
          {held && (
            <p className="mt-1 text-amber-700 dark:text-amber-400">
              {t('{amount} is on hold and is not recovered by the final settlement. HR must release the hold or Finance must record its recovery.', { amount: held })}
            </p>
          )}
          {unrecovered > 0 && (
            <p className="mt-1 font-medium text-danger-fg">
              {t('{name}: {amount} cannot be recovered from this settlement. It is not written off: it moves to the employee receivable when the settlement is paid.', { name: employeeName, amount: fx.money(unrecovered, fallbackCurrency || '') })}
            </p>
          )}
        </>
      )}
    </div>
  );
}
