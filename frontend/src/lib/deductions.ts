import { msg } from '../i18n/translations';
import type { MessageParams } from '../i18n/message';
import type { CapStatus, DeductionBlockReason, DeductionCategory, DeductionLine, DeductionStatement } from '../api/deductions';

/**
 * Pure helpers for the deductions statement (Release A slice R3): every number becomes a whole sentence key with
 * placeholders, so Arabic can order it naturally. Money and percentages are formatted by the caller (useFormat) and
 * passed in already formatted.
 */

export type Audience = 'hr' | 'employee';

/** A translatable sentence: a dictionary key and its placeholders. */
export interface Sentence { key: string; params?: MessageParams }

export const CATEGORY_LABEL: Record<DeductionCategory, string> = {
  Statutory: msg('Social insurance and other deductions required by law'),
  EmployerLoan: msg('Employer loan'),
  SalaryAdvance: msg('Salary advance'),
  Absence: msg('Pay not earned (absence)'),
  PenaltyOrAdjustment: msg('Penalty or adjustment'),
  CourtOrder: msg('Court order'),
  Other: msg('Other deduction'),
};

export const CAP_STATUS_LABEL: Record<CapStatus, string> = {
  Within: msg('Within the limit'),
  Near: msg('Near the limit'),
  Over: msg('Over the limit'),
};

/** Art. 92: above this share of the wage a loan instalment needs written consent. */
export const CONSENT_THRESHOLD_PERCENT = 10;

export interface Formatters {
  money: (n: number) => string;
  percent: (n: number) => string;
  /** Translates a nested label (a category, "Salary advance") before it is placed inside a sentence. */
  tr: (key: string) => string;
}

/** The main sentence for one line. Loan and advance lines say what is left; others say what they are. */
export function lineSentence(line: DeductionLine, fmt: Formatters): Sentence {
  const isDebt = line.category === 'EmployerLoan' || line.category === 'SalaryAdvance';
  const amount = fmt.money(line.amount);
  if (!isDebt || line.loanId == null) return { key: CATEGORY_LABEL[line.category] };
  const type = line.category === 'SalaryAdvance' ? fmt.tr(msg('Salary advance')) : (line.loanType || fmt.tr(msg('Employer loan')));
  const named = line.loanNumber ? `${type} ${line.loanNumber}` : type;
  if (line.balanceAfter == null || line.instalmentsRemaining == null)
    return { key: msg('{type} instalment of {amount}. The balance after this payslip could not be confirmed.'), params: { type: named, amount } };
  if (line.balanceAfter <= 0 || line.instalmentsRemaining === 0)
    return { key: msg('{type} instalment of {amount}: this was the last instalment.'), params: { type: named, amount } };
  const balance = fmt.money(line.balanceAfter);
  if (line.instalmentsTotal != null && line.instalmentsTotal >= line.instalmentsRemaining)
    return {
      key: msg('{type} instalment of {amount}: {remaining} of {total} instalments left, {balance} still owed.'),
      params: { type: named, amount, remaining: line.instalmentsRemaining, total: line.instalmentsTotal, balance },
    };
  return {
    key: msg('{type} instalment of {amount}: {remaining, plural, one {# instalment} other {# instalments}} left, {balance} still owed.'),
    params: { type: named, amount, remaining: line.instalmentsRemaining, balance },
  };
}

/** The Art. 92 sentence for an employer-loan line: the 10% limit, this instalment's share, and whether consent is on file. */
export function consentSentences(line: DeductionLine, audience: Audience, fmt: Formatters): Sentence[] {
  if (line.category !== 'EmployerLoan' || line.loanId == null) return [];
  const out: Sentence[] = [];
  if (line.percentOfWage != null)
    out.push(audience === 'employee'
      ? { key: msg('Limit without your written consent: 10% of your wage (this instalment is {pct}).'), params: { pct: fmt.percent(line.percentOfWage) } }
      : { key: msg("Limit without the employee's written consent: 10% of the wage (this instalment is {pct})."), params: { pct: fmt.percent(line.percentOfWage) } });
  if (line.consentOnFile) out.push({ key: audience === 'employee' ? msg('Your written consent is on file.') : msg("The employee's written consent is on file.") });
  else if (exceedsConsentThreshold(line)) out.push({ key: msg('Above 10% and no written consent is on file.') });
  return out;
}

/** True when an employer-loan instalment is above 10% of the wage, or the wage is unknown (fail-closed). */
export function exceedsConsentThreshold(line: DeductionLine): boolean {
  return line.category === 'EmployerLoan' && line.loanId != null
    && (line.percentOfWage == null || line.percentOfWage > CONSENT_THRESHOLD_PERCENT);
}

/** Whether the line counts toward the Art. 93 half-wage limit, in words. */
export function capCountSentence(line: DeductionLine): string {
  return line.countsTowardCap ? msg('Counts toward the 50% limit') : msg('Not counted in the 50% limit');
}

/** The cap summary: what is counted, the limit, and the room left (or by how much it is over). */
export function capSummary(s: Pick<DeductionStatement, 'debtTotal' | 'capLimit' | 'headroom' | 'debtPercentOfWage' | 'capStatus'>, fmt: Formatters): Sentence {
  if (s.capStatus === 'Over')
    return { key: msg('Counted deductions of {debt} are over the limit of {limit} by {over}.'), params: { debt: fmt.money(s.debtTotal), limit: fmt.money(s.capLimit), over: fmt.money(-s.headroom) } };
  return {
    key: msg('Counted deductions of {debt} use {pct} of the wage. The limit is {limit}, so {room} is left.'),
    params: { debt: fmt.money(s.debtTotal), pct: fmt.percent(s.debtPercentOfWage ?? 0), limit: fmt.money(s.capLimit), room: fmt.money(s.headroom) },
  };
}

/** A block reason in the reader's language. */
export function reasonText(reason: DeductionBlockReason, locale: string): { title: string; why: string; fix: string } {
  const ar = locale === 'ar';
  return { title: ar ? reason.titleAr : reason.titleEn, why: ar ? reason.whyAr : reason.whyEn, fix: ar ? reason.fixAr : reason.fixEn };
}

/** Lines grouped for display: counted first (they are what the limit is about), then the rest. */
export function splitByCap(lines: DeductionLine[]): { counted: DeductionLine[]; notCounted: DeductionLine[] } {
  return { counted: lines.filter((l) => l.countsTowardCap), notCounted: lines.filter((l) => !l.countsTowardCap) };
}

/** Sums to the cent, so a reader can check a total against its lines. */
export function sumAmounts(lines: DeductionLine[]): number {
  return Math.round(lines.reduce((acc, l) => acc + Math.round(l.amount * 100), 0)) / 100;
}
