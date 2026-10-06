'use client';

import { useState } from 'react';
import { ChevronDown, Landmark } from 'lucide-react';
import type { DebtBalance, EmployeeDeductions } from '../../api/deductions';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import type { Audience } from '../../lib/deductions';
import { CapStatusChip, DeductionStatementView, usePeriodLabel } from './DeductionStatementView';

/**
 * Open loans and advances, then one entry per payslip (newest first) that expands into its full statement. Shared by
 * the HR profile panel and the employee's "My deductions" page.
 */
export function DeductionsHistory({ data, audience, months }: { data: EmployeeDeductions; audience: Audience; months: number }) {
  const { t } = useLocale();
  const fx = useFormat();
  const period = usePeriodLabel();
  const [expanded, setExpanded] = useState<string | null>(data.statements[0]?.slipId ?? null);
  const debts = [...data.balances.loans.map((b) => ({ b, advance: false })), ...data.balances.advances.map((b) => ({ b, advance: true }))];

  return (
    <div className="space-y-4">
      <section aria-labelledby="deductions-balances" className="rounded-2xl border border-slate-200 bg-white p-4 dark:border-white/[0.06] dark:bg-white/[0.03]">
        <h2 id="deductions-balances" className="flex items-center gap-1.5 text-sm font-bold text-slate-800 dark:text-slate-100">
          <Landmark className="h-4 w-4 text-sapphire dark:text-cyanAccent" aria-hidden /> {t('Open loans and advances')}
        </h2>
        {debts.length === 0 ? (
          <p className="mt-2 text-xs text-slate-500 dark:text-slate-400" data-testid="balances-empty">
            {audience === 'employee' ? t('You have nothing left to repay.') : t('Nothing is left to repay.')}
          </p>
        ) : (
          <ul className="mt-2 divide-y divide-slate-100 dark:divide-white/[0.06]" data-testid="balances">
            {debts.map(({ b, advance }) => <BalanceRow key={b.id} balance={b} advance={advance} audience={audience} money={(n) => fx.money(n, b.currency)} />)}
          </ul>
        )}
      </section>

      {data.statements.length === 0 ? (
        <p className="rounded-2xl border border-slate-200 bg-white p-6 text-center text-sm text-slate-500 dark:border-white/[0.06] dark:bg-white/[0.03] dark:text-slate-400"
          data-testid="statements-empty">
          {audience === 'employee'
            ? t('You have no finalised payslips in the last {months} months.', { months })
            : t('No payslips for this employee in the last {months} months.', { months })}
        </p>
      ) : (
        <ul className="space-y-2" data-testid="statements">
          {data.statements.map((s) => {
            const isOpen = expanded === s.slipId;
            return (
              <li key={s.slipId} className="rounded-2xl border border-slate-200 bg-white dark:border-white/[0.06] dark:bg-white/[0.03]">
                <button type="button" aria-expanded={isOpen} onClick={() => setExpanded(isOpen ? null : s.slipId)}
                  className="flex w-full flex-wrap items-center justify-between gap-2 px-4 py-3 text-start">
                  <span className="text-sm font-semibold text-slate-800 dark:text-slate-100">{period(s.year, s.month)}</span>
                  <span className="flex items-center gap-2 text-xs text-slate-600 dark:text-slate-300">
                    {t('Counted: {amount}', { amount: fx.money(s.debtTotal, s.currency) })}
                    <CapStatusChip status={s.capStatus} />
                    <ChevronDown className={`h-4 w-4 transition-transform ${isOpen ? 'rotate-180' : ''}`} aria-hidden />
                  </span>
                </button>
                {isOpen && (
                  <div className="border-t border-slate-100 px-4 py-4 dark:border-white/[0.06]">
                    <DeductionStatementView statement={s} audience={audience} showHeader={audience === 'hr'} />
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}

function BalanceRow({ balance: b, advance, audience, money }: { balance: DebtBalance; advance: boolean; audience: Audience; money: (n: number) => string }) {
  const { t } = useLocale();
  const fx = useFormat();
  const type = advance ? t('Salary advance') : b.type;
  return (
    <li className="py-2.5 text-sm" data-testid="balance">
      <div className="flex items-start justify-between gap-3">
        <p className="font-semibold text-slate-800 dark:text-slate-100">{type} <span className="font-mono text-xs font-normal text-slate-500">{b.loanNo}</span></p>
        <span className="font-mono font-semibold tabular-nums text-slate-900 dark:text-white">{money(b.outstanding)}</span>
      </div>
      <p className="text-xs text-slate-600 dark:text-slate-300">
        {b.remaining != null
          ? t('{balance} still owed: {instalment} a month, {remaining, plural, one {# instalment} other {# instalments}} left.',
            { balance: money(b.outstanding), instalment: money(b.instalment), remaining: b.remaining })
          : t('{balance} still owed: {instalment} a month.', { balance: money(b.outstanding), instalment: money(b.instalment) })}
      </p>
      {b.nextDueOn && <p className="text-[11px] text-slate-500 dark:text-slate-400">{t('Next instalment due on {date}.', { date: fx.date(b.nextDueOn) })}</p>}
      {!b.deductedFromPay && <p className="text-[11px] text-slate-500 dark:text-slate-400">{t('Repaid outside payroll, so it is not deducted from pay.')}</p>}
      {!advance && b.consentOnFile && (
        <p className="text-[11px] text-slate-500 dark:text-slate-400">
          {audience === 'employee' ? t('Your written consent is on file.') : t("The employee's written consent is on file.")}
        </p>
      )}
    </li>
  );
}
