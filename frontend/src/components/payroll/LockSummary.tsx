'use client';

import type { PayrollRun } from '../../api/payroll';
import { changeBetween } from '../../lib/payrollPipeline';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';

export interface LockSummaryData {
  run: PayrollRun;
  /** The earlier run of the same company and type, or null when there is none. */
  previous: PayrollRun | null;
  /** The company currency, already confirmed by the caller. Never a default. */
  currency: string;
  openErrors: number;
  warnings: number;
  overrides: number;
  excluded: number;
}

/** "October 2026" for a run's period, in the viewer's language and calendar. */
export function usePeriodLabel() {
  const fmt = useFormat();
  return (r: { year: number; month: number }) => fmt.date(`${r.year}-${String(r.month).padStart(2, '0')}-01`, 'monthYear');
}

/**
 * What the approver reads before Lock: the figures being frozen, how they moved from the previous run,
 * and what is still open. It is the body of the app's confirm dialog, so it carries no buttons.
 * Every figure comes from the run and the validation, override and population endpoints; nothing here
 * is estimated.
 */
export function LockSummary({ data }: { data: LockSummaryData }) {
  const t = useT();
  const fmt = useFormat();
  const period = usePeriodLabel();
  const { run, previous, currency } = data;
  const against = previous ? period(previous) : '';

  const money = (n: number) => fmt.money(n, currency);
  const signedMoney = (n: number) => `${n > 0 ? '+' : n < 0 ? '−' : ''}${money(Math.abs(n))}`;
  const signedInt = (n: number) => `${n > 0 ? '+' : n < 0 ? '−' : ''}${fmt.integer(Math.abs(n))}`;

  const change = (cur: number, prev: number | undefined, kind: 'money' | 'count') => {
    if (!previous || prev === undefined) return null;
    const c = changeBetween(cur, prev);
    if (c.delta === 0) return t('Same as {period}', { period: against });
    const delta = kind === 'money' ? signedMoney(c.delta) : signedInt(c.delta);
    return c.percent === null
      ? t('{change} against {period}', { change: delta, period: against })
      : t('{change} ({percent}) against {period}', { change: delta, percent: `${c.percent > 0 ? '+' : c.percent < 0 ? '−' : ''}${fmt.percent(Math.abs(c.percent), 1)}`, period: against });
  };

  const rows: Array<{ id: string; label: string; value: string; note?: string | null; strong?: boolean }> = [
    { id: 'headcount', label: t('Employees paid'), value: fmt.integer(run.employeeCount), note: change(run.employeeCount, previous?.employeeCount, 'count') },
    { id: 'gross', label: t('Gross pay'), value: money(run.totalGrossSalary), note: change(run.totalGrossSalary, previous?.totalGrossSalary, 'money') },
    { id: 'deductions', label: t('Deductions'), value: money(run.totalDeductions), note: change(run.totalDeductions, previous?.totalDeductions, 'money') },
    { id: 'net', label: t('Net pay'), value: money(run.totalNetSalary), note: change(run.totalNetSalary, previous?.totalNetSalary, 'money'), strong: true },
  ];

  return (
    <div className="space-y-4 text-fg" data-testid="lock-summary">
      <p className="text-body-sm text-fg-muted">
        {t('Locking posts the payroll journal and freezes these figures. A locked run can no longer be reopened; a correction means voiding it and creating a replacement.')}
      </p>

      <dl className="divide-y divide-line rounded-lg border border-line">
        {rows.map(r => (
          <div key={r.id} className="flex flex-wrap items-baseline justify-between gap-x-3 px-3 py-2" data-testid={`lock-summary-${r.id}`}>
            <dt className="text-body-sm text-fg-muted">{r.label}</dt>
            <dd className={`tabular-nums ${r.strong ? 'font-semibold' : ''}`}><bdi>{r.value}</bdi></dd>
            {r.note && <dd className="mt-0.5 basis-full text-meta text-fg-muted"><bdi>{r.note}</bdi></dd>}
          </div>
        ))}
      </dl>
      {!previous && (
        <p className="text-meta text-fg-muted" data-testid="lock-summary-no-previous">
          {t('There is no earlier run for this company to compare with.')}
        </p>
      )}

      <dl className="divide-y divide-line rounded-lg border border-line">
        <div className="flex items-baseline justify-between gap-3 px-3 py-2" data-testid="lock-summary-excluded">
          <dt className="text-body-sm text-fg-muted">{t('Employees excluded')}</dt>
          <dd className="tabular-nums">{fmt.integer(data.excluded)}</dd>
        </div>
        <div className="flex items-baseline justify-between gap-3 px-3 py-2" data-testid="lock-summary-errors">
          <dt className="text-body-sm text-fg-muted">{t('Open validation errors')}</dt>
          <dd className="tabular-nums">{fmt.integer(data.openErrors)}</dd>
        </div>
        <div className="flex items-baseline justify-between gap-3 px-3 py-2" data-testid="lock-summary-warnings">
          <dt className="text-body-sm text-fg-muted">{t('Open validation warnings')}</dt>
          <dd className="tabular-nums">{fmt.integer(data.warnings)}</dd>
        </div>
        <div className="flex items-baseline justify-between gap-3 px-3 py-2" data-testid="lock-summary-overrides">
          <dt className="text-body-sm text-fg-muted">{t('Errors cleared by override')}</dt>
          <dd className="tabular-nums">{fmt.integer(data.overrides)}</dd>
        </div>
        <div className="flex items-baseline justify-between gap-3 px-3 py-2" data-testid="lock-summary-currency">
          <dt className="text-body-sm text-fg-muted">{t('Currency')}</dt>
          <dd><bdi>{currency}</bdi></dd>
        </div>
      </dl>
    </div>
  );
}
