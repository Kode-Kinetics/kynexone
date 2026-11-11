'use client';

import { useLocale } from '../../contexts/LocaleContext';
import { formatAmount } from '../../lib/hire/money';
import { gradePayVerdict, type GradeRange } from '../../lib/gradePayRange';

/**
 * On the Edit salary step (and the review): what a grade change means for pay. The pay is not changed for HR (the amounts are
 * HR's to enter); this says which grade the pay will be judged against, its range, and whether the pay fits, before saving.
 */
export function EditGradePayNotice({
  fromCode, grade, gradeCode, gradeChanged, payEdited, monthlyPay, currency, compact = false,
}: {
  fromCode: string;
  /** The grade the pay is judged against (the new one when it changed); undefined when the code names no active grade. */
  grade: GradeRange | undefined;
  gradeCode: string;
  gradeChanged: boolean;
  /** HR has changed the pay in this edit (otherwise the pay shown is the record in force). */
  payEdited: boolean;
  monthlyPay: number;
  currency: string;
  /** The review: only the out-of-range warning. */
  compact?: boolean;
}) {
  const { t, locale } = useLocale();
  const money = (n: number) => formatAmount(n, currency, locale);
  const verdict = grade ? gradePayVerdict(grade, monthlyPay) : 'noRange';
  const outside = verdict === 'below' || verdict === 'above';
  if (compact && !outside) return null;

  const consequence = gradeChanged
    ? t('Change the pay before saving, or the grade change will be refused.')
    : t('Change the pay before saving, or the salary change will be refused.');
  const verdictText = verdict === 'below'
    ? t('The monthly pay of {amount} is below this range.', { amount: money(monthlyPay) })
    : verdict === 'above'
      ? t('The monthly pay of {amount} is above this range.', { amount: money(monthlyPay) })
      : verdict === 'inside'
        ? t('The monthly pay of {amount} is inside this range.', { amount: money(monthlyPay) })
        : null;
  const rangeText = grade
    ? t('{grade} salary range: {min} to {max} a month.', { grade: gradeCode, min: grade.minSalary > 0 ? money(grade.minSalary) : '—', max: grade.maxSalary > 0 ? money(grade.maxSalary) : '—' })
    : '';

  if (compact) {
    return <p role="alert" data-testid="edit-grade-pay-review" className="mb-4 rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-300">
      {rangeText} {verdictText} {consequence}
    </p>;
  }

  return (
    <div data-testid="edit-grade-pay" className={`rounded-lg px-3 py-2.5 text-sm sm:col-span-2 ${outside ? 'bg-rose-50 text-rose-800 dark:bg-rose-500/10 dark:text-rose-200' : 'bg-sky-50 text-slate-800 dark:bg-sky-500/10 dark:text-slate-100'}`}>
      {gradeChanged && <p className="font-semibold">{t('Grade changes from {from} to {to}.', { from: fromCode || t('None'), to: gradeCode })}{!payEdited && <> <span className="font-normal">{t('The pay below is the current pay. It stays as it is unless you change it.')}</span></>}</p>}
      {!grade
        ? <p className={gradeChanged ? 'mt-1' : undefined}>{t('{grade} is not an active grade of this company, so its salary range cannot be shown.', { grade: gradeCode })}</p>
        : verdict === 'noRange'
          ? <p className={gradeChanged ? 'mt-1' : undefined}>{t('{grade} has no salary range set.', { grade: gradeCode })}</p>
          : <p className={gradeChanged ? 'mt-1' : undefined} role={outside ? 'alert' : undefined}>
            {rangeText} {verdictText}{outside && <> {consequence}</>}
          </p>}
    </div>
  );
}
