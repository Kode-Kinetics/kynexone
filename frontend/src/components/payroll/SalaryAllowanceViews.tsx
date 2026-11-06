'use client';

import { useLocale } from '../../contexts/LocaleContext';
import { isolateLtr, isolateName } from '../../lib/bidi';
import { formatAmount } from '../../lib/hire/money';
import {
  compareAllowances, nameOf, viewMonthly, viewPercent,
  type AllowanceChange, type SalaryAllowanceView,
} from '../../lib/salaryAllowances';

/** One line in words: "1,500.00 a month" or "10% of basic = 500.00 a month". Numbers are isolated so they keep their direction in Arabic. */
function useLineText(currency: string, basic?: number) {
  const { t, locale } = useLocale();
  return (line: SalaryAllowanceView) => {
    const monthly = viewMonthly(line, basic);
    const amount = monthly === undefined ? '—' : formatAmount(monthly, currency, locale);
    return line.basis === 'PercentOfBasic'
      ? t('{rate}% of basic = {amount} a month', { rate: viewPercent(line), amount })
      : t('{amount} a month', { amount });
  };
}

/** Where a line posts and whether it counts in end of service pay: what Finance takes on by approving it. Nothing when the server does not say. */
function AllowanceLiability({ line }: { line: SalaryAllowanceView }) {
  const { t } = useLocale();
  const posts = line.glDriverKey?.trim();
  const eosb = line.eosbIncluded;
  if (!posts && eosb == null) return null;
  return (
    <span className="mt-0.5 block text-xs font-normal text-slate-600 dark:text-slate-300" data-testid="salary-allowance-liability">
      {posts && <span className="block">{t('Posts to {account}', { account: isolateLtr(posts) })}</span>}
      {eosb === true && <span className="block">{t('Counts toward end of service pay')}</span>}
      {eosb === false && <span className="block">{line.eosbExclusionReason?.trim()
        ? t('Left out of end of service pay: {reason}', { reason: isolateName(line.eosbExclusionReason.trim()) })
        : t('Left out of end of service pay')}</span>}
    </span>
  );
}

const CHANGE: Record<AllowanceChange, { mark: string; label: string }> = {
  added: { mark: '+', label: 'New' },
  removed: { mark: '−', label: 'Removed' },
  changed: { mark: '≠', label: 'Changed' },
  unchanged: { mark: '=', label: 'Unchanged' },
};

/**
 * What a salary change does to the monthly cash allowances, for the Finance approver: the lines on the salary now beside the lines
 * proposed. A difference is never shown by colour alone: each row says New, Removed, Changed or Unchanged in words, with a mark, and a
 * changed row is also bolder and carries a bar on its start edge. A proposal that does not mention allowances says so plainly.
 */
export function SalaryAllowanceComparison({ current, proposed, carriedForward, currency, basic }: {
  current: readonly SalaryAllowanceView[] | null | undefined;
  proposed: readonly SalaryAllowanceView[] | null | undefined;
  carriedForward: boolean;
  currency: string;
  /** The proposed basic salary, for a percentage line whose amount the server has not stated. */
  basic?: number;
}) {
  const { t, locale } = useLocale();
  const text = useLineText(currency, basic);
  const rows = compareAllowances(current, proposed);
  if (rows.length === 0) return null;
  const moved = rows.some(r => r.change !== 'unchanged');
  return (
    <section className="mt-2 space-y-1" aria-label={t('Monthly allowances in this change')} data-testid="salary-allowance-comparison">
      <h4 className="text-xs font-semibold text-slate-700 dark:text-slate-200">{t('Monthly allowances')}</h4>
      <p className="text-xs text-slate-600 dark:text-slate-300" data-testid="salary-allowance-carried">
        {carriedForward
          ? moved
            ? t('This change does not touch the monthly allowances; the current ones carry on. A percentage is worked out again on the new basic salary.')
            : t('This change does not touch the monthly allowances; the current ones carry on unchanged.')
          : moved
            ? t('This change sets the monthly allowances shown below.')
            : t('The monthly allowances in this change are the same as the current ones.')}
      </p>
      <div className="overflow-x-auto">
        <table className="w-full min-w-[32rem] border-collapse text-start text-xs">
          <thead>
            <tr className="text-slate-500">
              <th scope="col" className="py-1 pe-3 text-start font-medium">{t('Allowance')}</th>
              <th scope="col" className="py-1 pe-3 text-start font-medium">{t('On the salary now')}</th>
              <th scope="col" className="py-1 pe-3 text-start font-medium">{t('Proposed')}</th>
              <th scope="col" className="py-1 text-start font-medium">{t('Difference')}</th>
            </tr>
          </thead>
          <tbody>
            {rows.map(r => {
              const named = r.proposed ?? r.current!;
              const diff = r.change !== 'unchanged';
              return (
                <tr key={r.code} data-change={r.change} data-testid="salary-allowance-row"
                  className={`border-t border-slate-100 align-top dark:border-white/[0.06] ${diff ? 'border-s-4 border-s-sapphire font-semibold text-slate-900 dark:text-white' : 'text-slate-600 dark:text-slate-300'}`}>
                  <th scope="row" className="py-1 pe-3 text-start font-medium">
                    {nameOf(named, locale)} <span className="font-normal text-slate-500" dir="ltr"><bdi>{r.code}</bdi></span>
                    <AllowanceLiability line={named} />
                  </th>
                  <td className="py-1 pe-3">{r.current ? <bdi>{text(r.current)}</bdi> : <><span aria-hidden="true">—</span><span className="sr-only">{t('None')}</span></>}</td>
                  <td className="py-1 pe-3">{r.proposed ? <bdi>{text(r.proposed)}</bdi> : <><span aria-hidden="true">—</span><span className="sr-only">{t('None')}</span></>}</td>
                  <td className="py-1"><span aria-hidden="true">{CHANGE[r.change].mark} </span>{t(CHANGE[r.change].label)}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </section>
  );
}

/** The allowance lines on the salary record in force, by name in the reader's language, with the basis and the monthly amount. */
export function SalaryAllowanceList({ lines, names, currency, basic, pending = false }: {
  lines: readonly SalaryAllowanceView[];
  /** Names by code from Benefits by grade, when this reader may read them; otherwise the code stands in. */
  names: ReadonlyMap<string, { nameEn: string; nameAr: string }>;
  currency: string;
  basic?: number;
  /** The lines are a new hire's first package still waiting for Finance. */
  pending?: boolean;
}) {
  const { t, locale } = useLocale();
  const text = useLineText(currency, basic);
  if (lines.length === 0) return null;
  return (
    <section className="border-t border-slate-200 p-5 dark:border-white/10" aria-labelledby="salary-allowances-heading" data-testid="employee-salary-allowances">
      <h3 id="salary-allowances-heading" className="font-semibold text-slate-950 dark:text-white">{t('Monthly allowances')}</h3>
      <p className="mt-1 text-sm text-slate-500">{pending ? t('Part of the first package. Nothing is paid until Finance approves it.') : t('Paid each month on top of the salary above.')}</p>
      <dl className="mt-3 grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
        {lines.map(line => {
          const code = line.code.toUpperCase();
          const known = names.get(code);
          return (
            <div key={code} className="rounded-xl border border-slate-200 p-4 dark:border-white/10" data-testid="employee-salary-allowance">
              <dt className="text-xs font-semibold uppercase tracking-wide text-slate-400">{nameOf({ code, nameEn: line.nameEn ?? known?.nameEn, nameAr: line.nameAr ?? known?.nameAr }, locale)}</dt>
              <dd className="mt-1 font-semibold text-slate-900 dark:text-white"><bdi>{text(line)}</bdi></dd>
            </div>
          );
        })}
      </dl>
    </section>
  );
}
