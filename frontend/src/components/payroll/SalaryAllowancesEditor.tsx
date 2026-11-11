'use client';

import Link from 'next/link';
import { Plus, Trash2 } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useLocale } from '../../contexts/LocaleContext';
import type { CashAllowanceCatalog } from '../../hooks/useCashAllowanceCatalog';
import { formatAmount } from '../../lib/hire/money';
import {
  allowanceProblems, blankLine, lineMonthly, lineName, nameOf, shownLines, totalMonthly, valueAfterEdit,
  type AllowanceBasis, type AllowanceDraftLine, type AllowanceValue,
} from '../../lib/salaryAllowances';

/**
 * "Other monthly allowances" on a salary: the customer-defined cash allowances (ALW_*) as lines of allowance, fixed amount or
 * percentage of basic, value and the monthly amount a percentage comes to.
 *
 * `value` is null while the person has not touched the section: the caller then sends no `allowances` at all, and the server
 * carries the current lines forward (an existing salary) or fills them from the grade (a first package). Only an edit makes it a list.
 * `baseline` is what an untouched section means: the salary record's lines, or the grade's. Editing back to the baseline is untouched again.
 */
export function SalaryAllowancesEditor({
  idPrefix, baseline, baselineKind, noGrade = false, baselineKnown = true, value, onChange, basic, currency, catalog, onRetry, disabled = false, showProblems = false, serverProblem = null,
}: {
  idPrefix: string;
  baseline: readonly AllowanceDraftLine[];
  /** Whether an untouched section means the salary record's own lines (carried forward) or the grade's lines (filled in on save). */
  baselineKind: 'record' | 'grade';
  /** Create forms: no grade is chosen yet, so there is nothing to fill in from. */
  noGrade?: boolean;
  /** False when the salary record's own lines could not be read (no employee chosen yet, or no access): untouched then means "left as they are". */
  baselineKnown?: boolean;
  value: AllowanceValue;
  onChange: (next: AllowanceValue) => void;
  basic: number | undefined;
  currency: string;
  catalog: CashAllowanceCatalog;
  onRetry: () => void;
  disabled?: boolean;
  /** Show each line's problem now (after a failed save), rather than while it is still being filled in. */
  showProblems?: boolean;
  /** The server's own refusal of the lines (salary_allowance_invalid / salary_allowance_unknown), in its words. */
  serverProblem?: string | null;
}) {
  const { t, locale } = useLocale();
  const { hasPermission } = useAuth();
  const lines = shownLines(baseline, value);
  const touched = value !== null;
  const problems = allowanceProblems(lines, basic);
  const ready = catalog.status === 'ready';
  const choices = ready ? catalog.choices : [];
  const editable = ready && !disabled;
  const money = (n: number) => formatAmount(n, currency, locale);
  const set = (next: AllowanceDraftLine[]) => onChange(valueAfterEdit(baseline, next));
  // Switching a line to another allowance drops the names it came with: they belonged to the old one.
  const patch = (key: string, p: Partial<AllowanceDraftLine>) => set(lines.map(l => {
    if (l.key !== key) return l;
    const next = { ...l, ...p };
    if (p.code !== undefined && p.code.toUpperCase() !== l.code.toUpperCase()) { delete next.nameEn; delete next.nameAr; }
    return next;
  }));

  // Nothing to show and nothing that can be added: the tenant has no Benefits by grade, or this person may not read it.
  if (catalog.status === 'unavailable' && lines.length === 0) return null;

  const total = totalMonthly(lines, basic);
  const allUsed = choices.every(c => lines.some(l => l.code.toUpperCase() === c.code));
  const note = touched
    ? t('You changed these. Saving sends exactly the lines shown here.')
    : baselineKind === 'record' && !baselineKnown
      ? t('The allowances already on this employee’s salary stay as they are. Add a line to set the allowances yourself instead.')
      : baselineKind === 'record'
      ? lines.length > 0
        ? t('These stay as they are on the new salary. A percentage is worked out again on the new basic salary.')
        : t('There are no monthly allowances on this salary. Add one if this employee should get it.')
      : noGrade
        ? t('Choose a grade to fill these in from Benefits by grade, or add them yourself.')
        : lines.length > 0
          ? t('Filled in from Benefits by grade for this grade when you save. Change a line to set your own.')
          : t('This grade gives no monthly allowances. Add one if this employee should get it.');

  const usedElsewhere = (key: string) => new Set(lines.filter(l => l.key !== key).map(l => l.code.toUpperCase()));
  // The server's name for the line, then the catalogue's, then the code: a reader who cannot list the catalogue still sees names.
  const nameFor = (line: AllowanceDraftLine) => lineName(line, choices, locale);

  return (
    <fieldset className="rounded-xl border border-slate-200 p-3 dark:border-white/10 sm:col-span-2" data-testid="salary-allowances" aria-describedby={`${idPrefix}-note`}>
      <legend className="px-1 text-sm font-semibold text-slate-800 dark:text-slate-100">{t('Other monthly allowances')}</legend>
      <p id={`${idPrefix}-note`} className="text-xs text-slate-600 dark:text-slate-300" data-testid="salary-allowances-note">{note}</p>

      {catalog.status === 'loading' && <p role="status" className="mt-2 text-xs text-slate-500">{t('Loading the monthly allowances…')}</p>}
      {catalog.status === 'error' && (
        <p role="alert" className="mt-2 text-sm text-red-600">
          {t('The monthly allowances could not be loaded.')}{' '}
          <button type="button" className="text-sapphire underline" onClick={onRetry}>{t('Try again')}</button>
        </p>
      )}
      {catalog.status === 'unavailable' && <p className="mt-2 text-xs text-slate-500">{t('You can’t add or change monthly allowances here. The ones already on the salary stay as they are.')}</p>}
      {serverProblem && <p role="alert" className="mt-2 text-sm text-red-600" data-testid="salary-allowances-server-problem">{serverProblem}</p>}

      {lines.length > 0 && (
        <ul className="mt-3 space-y-3">
          {lines.map((line, i) => {
            const n = i + 1;
            const problem = showProblems ? problems[line.key] : undefined;
            const monthly = lineMonthly(line, basic);
            const taken = usedElsewhere(line.key);
            const knownCode = choices.some(c => c.code === line.code.toUpperCase());
            const errorId = `${idPrefix}-line-${n}-error`;
            if (!editable) {
              return (
                <li key={line.key} className="flex flex-wrap items-baseline justify-between gap-2 text-sm" data-testid="salary-allowance-line">
                  <span className="font-medium text-slate-800 dark:text-slate-100">{nameFor(line)}{nameFor(line) !== line.code && <> <span className="text-xs font-normal text-slate-500" dir="ltr"><bdi>{line.code}</bdi></span></>}</span>
                  <span className="text-slate-700 dark:text-slate-200">
                    {line.basis === 'PercentOfBasic'
                      ? t('{rate}% of basic = {amount} a month', { rate: line.value, amount: monthly === undefined ? '—' : money(monthly) })
                      : t('{amount} a month', { amount: monthly === undefined ? '—' : money(monthly) })}
                  </span>
                </li>
              );
            }
            return (
              <li key={line.key} className="grid gap-2 sm:grid-cols-[minmax(0,2fr)_minmax(0,1.5fr)_minmax(0,1fr)_auto] sm:items-start" data-testid="salary-allowance-line">
                <select className="select w-full" aria-label={t('Allowance, line {n}', { n })} aria-invalid={!!problem} aria-describedby={problem ? errorId : undefined}
                  value={line.code.toUpperCase()} onChange={e => patch(line.key, { code: e.target.value })}>
                  <option value="">{t('Choose an allowance')}</option>
                  {!knownCode && line.code && <option value={line.code.toUpperCase()}>{nameFor(line)}</option>}
                  {choices.map(c => <option key={c.code} value={c.code} disabled={taken.has(c.code) || (c.financePending && c.code !== line.code.toUpperCase())}>
                    {c.financePending ? t('{name} — waiting for Finance', { name: nameOf(c, locale) }) : nameOf(c, locale)}</option>)}
                </select>
                <select className="select w-full" aria-label={t('Fixed amount or percentage of basic, line {n}', { n })} value={line.basis}
                  onChange={e => patch(line.key, { basis: e.target.value as AllowanceBasis })}>
                  <option value="Amount">{t('Fixed amount')}</option>
                  <option value="PercentOfBasic">{t('% of basic salary')}</option>
                </select>
                <div>
                  <input className="input w-full text-start" inputMode="decimal" dir="ltr" autoComplete="off"
                    aria-label={line.basis === 'PercentOfBasic' ? t('Percentage of basic, line {n}', { n }) : t('Monthly amount, line {n}', { n })}
                    aria-invalid={!!problem} aria-describedby={problem ? errorId : undefined}
                    value={line.value} onChange={e => patch(line.key, { value: e.target.value })} />
                  {line.basis === 'PercentOfBasic' && (
                    <p className="mt-1 text-xs text-slate-600 dark:text-slate-300" data-testid="salary-allowance-computed">
                      {monthly === undefined
                        ? t('Worked out once the basic salary is entered.')
                        : t('{amount} a month', { amount: money(monthly) })}
                    </p>
                  )}
                </div>
                <button type="button" className="btn-secondary h-9 px-2" aria-label={t('Remove allowance line {n}', { n })} title={t('Remove')}
                  onClick={() => set(lines.filter(l => l.key !== line.key))}><Trash2 className="h-4 w-4" aria-hidden="true" /></button>
                {problem && <p id={errorId} role="alert" className="text-sm text-red-600 sm:col-span-4">{t(problem)}</p>}
              </li>
            );
          })}
        </ul>
      )}

      {lines.length > 0 && (
        <p className="mt-3 text-sm font-medium text-slate-800 dark:text-slate-100" data-testid="salary-allowances-total">
          {t('Total of these allowances: {amount} a month', { amount: money(total) })}
        </p>
      )}

      {/* Nothing in the catalogue to add: say where allowances are set up, rather than offering a button that can never work. */}
      {ready && choices.length === 0 && (
        <p className="mt-2 text-xs text-slate-600 dark:text-slate-300" data-testid="salary-allowances-none-defined">
          {t('No monthly allowance has been added in Benefits by grade yet.')}{' '}
          {hasPermission('entitlements.manage')
            ? <Link href="/benefits/by-grade" target="_blank" rel="noopener noreferrer" className="font-semibold text-sapphire underline">{t('Add allowances in Benefits by grade')}<span className="sr-only"> {t('(opens in a new tab)')}</span></Link>
            : t('Ask an administrator to add them in Benefits by grade.')}
        </p>
      )}

      {ready && (choices.length > 0 || touched) && (
        <div className="mt-3 flex flex-wrap items-center gap-2">
          {choices.length > 0 && <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={!editable || allUsed}
            onClick={() => set([...lines, blankLine()])}><Plus className="h-4 w-4" aria-hidden="true" />{t('Add an allowance')}</button>}
          {touched && (
            <button type="button" className="text-xs text-sapphire underline" disabled={disabled}
              onClick={() => onChange(null)}>{baselineKind === 'record' ? t('Go back to the allowances on the salary now') : t('Go back to the grade’s allowances')}</button>
          )}
        </div>
      )}
    </fieldset>
  );
}
