'use client';

import { AlertTriangle, CheckCircle2, ShieldAlert } from 'lucide-react';
import type { DeductionLine, DeductionStatement } from '../../api/deductions';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import {
  type Audience, type Formatters, CAP_STATUS_LABEL, CATEGORY_LABEL, COMPONENT_LABEL, capCountSentence, capSummary, consentSentences,
  exceedsConsentThreshold, lineSentence, reasonText, splitByCap, sumAmounts,
} from '../../lib/deductions';

/** Money in the statement's currency and a 0–100 share, both in the reader's number format. */
export function useDeductionFormatters(currency: string | null): Formatters {
  const fx = useFormat();
  const { t, locale } = useLocale();
  return {
    money: (n: number) => fx.money(n, currency),
    percent: (n: number) => fx.percent(n, Number.isInteger(n) ? 0 : 2),
    tr: (key: string) => t(key),
    locale,
  };
}

/** "June 2026" in the reader's calendar. */
export function usePeriodLabel() {
  const fx = useFormat();
  return (year: number, month: number) =>
    year > 0 && month >= 1 && month <= 12 ? fx.date(`${year}-${String(month).padStart(2, '0')}-01`, 'monthYear') : '—';
}

const STATUS_TONE = {
  Within: 'bg-emerald-50 text-emerald-800 ring-emerald-200 dark:bg-emerald-500/10 dark:text-emerald-300 dark:ring-emerald-500/30',
  Near: 'bg-amber-50 text-amber-800 ring-amber-200 dark:bg-amber-500/10 dark:text-amber-300 dark:ring-amber-500/30',
  Over: 'bg-rose-50 text-rose-800 ring-rose-200 dark:bg-rose-500/10 dark:text-rose-300 dark:ring-rose-500/30',
  NeedsReview: 'bg-amber-50 text-amber-800 ring-amber-300 dark:bg-amber-500/10 dark:text-amber-300 dark:ring-amber-500/30',
  Voided: 'bg-slate-100 text-slate-600 ring-slate-200 dark:bg-white/[0.06] dark:text-slate-300 dark:ring-white/10',
} as const;

export function CapStatusChip({ status }: { status: DeductionStatement['capStatus'] }) {
  const { t } = useLocale();
  return (
    <span data-testid="cap-status" className={`inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-semibold ring-1 ${STATUS_TONE[status]}`}>
      {t(CAP_STATUS_LABEL[status])}
    </span>
  );
}

/**
 * One payslip's deductions, explained (Release A slice R3): the Art. 93 limit panel, every flag as plain sentences,
 * then each line with its category, legal basis and — for loans — what is left. Every total drills to the lines below
 * it, and the footer says whether the lines add up to the payslip's own deductions total.
 */
export function DeductionStatementView({ statement, audience, showHeader = true }: {
  statement: DeductionStatement;
  audience: Audience;
  showHeader?: boolean;
}) {
  const { t, locale } = useLocale();
  const fmt = useDeductionFormatters(statement.currency);
  const period = usePeriodLabel();
  const { counted, notCounted } = splitByCap(statement.lines);
  const summary = capSummary(statement, fmt);
  const voided = statement.capStatus === 'Voided';
  const notFinal = statement.slipStatus !== 'Final' && !voided;
  // An employee never sees a summary-only slip as if it were itemised: they are told it cannot be broken down yet.
  const cannotBreakDown = audience === 'employee' && !statement.reconciles;

  return (
    <div className="space-y-4" data-testid="deduction-statement">
      {showHeader && (
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h3 className="text-sm font-bold text-slate-800 dark:text-slate-100">{period(statement.year, statement.month)}</h3>
          <div className="flex items-center gap-2">
            {notFinal && audience === 'hr' && (
              <span className="rounded-full bg-slate-100 px-2 py-0.5 text-[11px] font-semibold text-slate-600 dark:bg-white/[0.06] dark:text-slate-300">
                {t('Not final yet')}
              </span>
            )}
            <CapStatusChip status={statement.capStatus} />
          </div>
        </div>
      )}

      {voided && (
        <p role="status" data-testid="statement-voided" className="rounded-xl bg-slate-100 p-3 text-xs font-semibold text-slate-700 dark:bg-white/[0.06] dark:text-slate-200">
          {t('Voided, no longer applies')}. {t('This payroll run was voided, so its deductions were reversed and nothing here needs action.')}
        </p>
      )}

      {cannotBreakDown && (
        <p role="status" data-testid="statement-cannot-break-down" className="rounded-xl bg-amber-50 p-3 text-sm font-medium text-amber-900 dark:bg-amber-500/10 dark:text-amber-200">
          {t("This payslip can't be broken down yet. Ask HR.")}
        </p>
      )}

      <section aria-label={t('The 50% limit')} className="rounded-xl border border-slate-200 p-3 dark:border-white/10">
        <dl className="grid grid-cols-2 gap-3 sm:grid-cols-4">
          <Figure label={t('Wage due (after absence)')} value={fmt.money(statement.wageDue)} testId="cap-wage" />
          <Figure label={t('Counted deductions')} value={fmt.money(statement.debtTotal)} testId="cap-debt" />
          <Figure label={t('50% limit')} value={fmt.money(statement.capLimit)} />
          <Figure
            label={statement.headroom < 0 ? t('Over the limit by') : t('Room left')}
            value={fmt.money(Math.abs(statement.headroom))}
            tone={statement.headroom < 0 ? 'bad' : undefined}
            testId="cap-room"
          />
        </dl>
        <p className="mt-2 text-[11px] text-slate-500 dark:text-slate-400" data-testid="cap-wage-note">
          {t('Gross pay {gross} minus pay not earned for absence {absence}.', { gross: fmt.money(statement.grossPay), absence: fmt.money(statement.payNotEarned) })}
          {statement.otherRuns > 0 && <> {t('Includes {count, plural, one {# other payroll run} other {# other payroll runs}} this month: the limit covers the whole month.', { count: statement.otherRuns })}</>}
          {audience === 'hr' && statement.otherRunsNotFinal > 0 && <> {t('{count, plural, one {# of them is} other {# of them are}} not final yet.', { count: statement.otherRunsNotFinal })}</>}
          {' '}{t('Counting the limit on the wage after absence is pending legal confirmation.')}
        </p>
        {!voided && !cannotBreakDown && <p className="mt-2 text-xs text-slate-700 dark:text-slate-200">{t(summary.key, summary.params)}</p>}
        <p className="mt-1 text-[11px] text-slate-500 dark:text-slate-400">
          {t('Article 93: loan, advance, penalty and court-ordered deductions together may not exceed half of the wage due. Social insurance (GOSI) and pay not earned for absence are not counted.')}
        </p>
      </section>

      {!voided && statement.reasons.length > 0 && (
        <ul className="space-y-2" aria-label={t('Needs attention')}>
          {statement.reasons.map((r) => {
            const text = reasonText(r, locale);
            const severe = r.code === 'DEDUCTIONS_OVER_HALF_WAGE';
            return (
              <li key={r.code} role={severe ? 'alert' : undefined}
                className={`rounded-xl p-3 text-xs ${severe ? 'bg-rose-50 text-rose-900 dark:bg-rose-500/10 dark:text-rose-200' : 'bg-amber-50 text-amber-900 dark:bg-amber-500/10 dark:text-amber-200'}`}>
                <p className="flex items-center gap-1.5 font-semibold">
                  {severe ? <ShieldAlert className="h-3.5 w-3.5" aria-hidden /> : <AlertTriangle className="h-3.5 w-3.5" aria-hidden />}
                  {text.title}
                </p>
                <p className="mt-1">{text.why}</p>
                {audience === 'hr' && <p className="mt-1 font-medium">{text.fix}</p>}
              </li>
            );
          })}
        </ul>
      )}

      {!cannotBreakDown && <>
      <LineGroup title={t('Counted toward the 50% limit')} total={fmt.money(sumAmounts(counted))} lines={counted} audience={audience}
        fmt={fmt} empty={t('Nothing on this payslip counts toward the limit.')} testId="lines-counted" />
      <LineGroup title={t('Not counted in the limit')} total={fmt.money(sumAmounts(notCounted))} lines={notCounted} audience={audience}
        fmt={fmt} empty={t('No other deductions on this payslip.')} testId="lines-not-counted" />
      </>}

      {!cannotBreakDown && <p className={`flex items-start gap-1.5 text-[11px] ${statement.reconciles ? 'text-slate-500 dark:text-slate-400' : 'font-semibold text-amber-700 dark:text-amber-300'}`}
        data-testid="statement-reconciles">
        {statement.reconciles
          ? <><CheckCircle2 className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />{t("These lines add up to the payslip's deductions total of {amount}.", { amount: fmt.money(statement.slipDeductionTotal) })}</>
          : <><AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />{t("These lines do not add up to the payslip's deductions total of {amount}. Ask payroll to check this payslip before relying on it.", { amount: fmt.money(statement.slipDeductionTotal) })}</>}
      </p>}
    </div>
  );
}

function Figure({ label, value, tone, testId }: { label: string; value: string; tone?: 'bad'; testId?: string }) {
  return (
    <div>
      <dt className="text-[11px] font-medium text-slate-500 dark:text-slate-400">{label}</dt>
      <dd data-testid={testId} className={`font-mono text-sm font-bold tabular-nums ${tone === 'bad' ? 'text-rose-700 dark:text-rose-300' : 'text-slate-900 dark:text-white'}`}>{value}</dd>
    </div>
  );
}

function LineGroup({ title, total, lines, audience, fmt, empty, testId }: {
  title: string; total: string; lines: DeductionLine[]; audience: Audience; fmt: Formatters; empty: string; testId: string;
}) {
  const { t } = useLocale();
  return (
    <section data-testid={testId}>
      <div className="mb-1.5 flex items-baseline justify-between gap-2">
        <h4 className="text-xs font-bold uppercase tracking-wide text-slate-500 dark:text-slate-400">{title}</h4>
        <span className="font-mono text-xs font-semibold tabular-nums text-slate-700 dark:text-slate-200">{total}</span>
      </div>
      {lines.length === 0 ? (
        <p className="text-xs text-slate-500 dark:text-slate-400">{empty}</p>
      ) : (
        <ul className="divide-y divide-slate-100 rounded-xl border border-slate-200 dark:divide-white/[0.06] dark:border-white/10">
          {lines.map((line, i) => <LineRow key={`${line.componentCode}-${line.loanId ?? i}`} line={line} audience={audience} fmt={fmt} />)}
        </ul>
      )}
      {lines.length > 0 && <p className="sr-only">{t('Total')} {total}</p>}
    </section>
  );
}

function LineRow({ line, audience, fmt }: { line: DeductionLine; audience: Audience; fmt: Formatters }) {
  const { t } = useLocale();
  const sentence = lineSentence(line, fmt);
  const consent = consentSentences(line, audience, fmt);
  const isDebtLine = line.loanId != null;
  const consentProblem = exceedsConsentThreshold(line) && !line.consentOnFile;
  return (
    <li className="space-y-1 px-3 py-2.5" data-testid="deduction-line">
      <div className="flex items-start justify-between gap-3">
        <p className="text-sm font-semibold text-slate-800 dark:text-slate-100">{t(COMPONENT_LABEL[line.componentCode] ?? CATEGORY_LABEL[line.category])}</p>
        <span className="font-mono text-sm font-semibold tabular-nums text-slate-900 dark:text-white">{fmt.money(line.amount)}</span>
      </div>
      {isDebtLine && <p className="text-xs text-slate-700 dark:text-slate-200" data-testid="deduction-sentence">{t(sentence.key, sentence.params)}</p>}
      {!isDebtLine && line.label && !COMPONENT_LABEL[line.componentCode] && <p className="text-[11px] text-slate-500 dark:text-slate-400">{line.label}</p>}
      {consent.map((c) => (
        <p key={c.key} className={`text-xs ${consentProblem && c === consent[consent.length - 1] ? 'font-semibold text-amber-700 dark:text-amber-300' : 'text-slate-600 dark:text-slate-300'}`}>
          {t(c.key, c.params)}
        </p>
      ))}
      <p className="text-[11px] text-slate-500 dark:text-slate-400">{t(line.legalBasisKey)}</p>
      <span className={`inline-block rounded-full px-1.5 py-0.5 text-[10px] font-semibold ${line.countsTowardCap
        ? 'bg-sapphire/10 text-sapphire dark:bg-cyanAccent/10 dark:text-cyanAccent'
        : 'bg-slate-100 text-slate-600 dark:bg-white/[0.06] dark:text-slate-300'}`}>
        {t(capCountSentence(line))}
      </span>
    </li>
  );
}
