// Release A slice R2 — the package in words. Pure functions (no React), shared by the HR panel and "My package", and
// pinned by unit/releaseA.package.spec.ts. Every sentence is an i18n key in src/i18n/releaseA/package.ts; values are
// filled into {placeholders} after translation, so Arabic word order is the translator's, not the code's.

export type Translate = (key: string) => string;

/** The fields both the HR line and the employee line carry. */
export interface ValueLine {
  componentCode: string;
  resolvedAmount?: number | null;
  valueType: string | null;
  amount: number | null;
  rate: number | null;
  monthlyCash: number | null;
  coverageTier: string | null;
  quantity: number | null;
  dependantScope: string;
  maxDependants: number | null;
  dependantsCovered: number;
  limitPeriod: string | null;
}

export interface FormatContext {
  t: Translate;
  locale: string;
  currency: string;
}

const CURRENCY_AR: Record<string, string> = { SAR: 'ر.س', AED: 'د.إ', KWD: 'د.ك', BHD: 'د.ب', OMR: 'ر.ع', QAR: 'ر.ق' };

export function fill(template: string, values: Record<string, string | number>): string {
  return Object.entries(values).reduce((s, [k, v]) => s.split(`{${k}}`).join(String(v)), template);
}

/** "SAR 2,000" / "2,000 ر.س". Western digits in both (as Saudi payslips print them); cents only when present. */
export function money(amount: number, ctx: Pick<FormatContext, 'locale' | 'currency'>): string {
  const digits = Number.isInteger(amount) ? 0 : 2;
  const n = amount.toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: 2 });
  return ctx.locale === 'ar' ? `${n} ${CURRENCY_AR[ctx.currency] ?? ctx.currency}` : `${ctx.currency} ${n}`;
}

/** 1 Feb 2026 / ١ فبراير ٢٠٢٦ is avoided: Gregorian, Arabic month names, Western digits. */
export function date(iso: string | null | undefined, locale: string): string {
  if (!iso) return '';
  const d = new Date(`${iso}T00:00:00Z`);
  return d.toLocaleDateString(locale === 'ar' ? 'ar-SA-u-ca-gregory-nu-latn' : 'en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' });
}

function percent(rate: number): string {
  return (Math.round(rate * 10000) / 100).toLocaleString('en-US', { maximumFractionDigits: 2 });
}

function multiple(rate: number): string {
  return rate.toLocaleString('en-US', { maximumFractionDigits: 4 });
}

/** Coverage tier in words: "Class B", "CCHI basic class", "Economy". */
export function tierText(tier: string | null, t: Translate): string {
  if (!tier) return '';
  switch (tier) {
    case 'CchiBasic': return t('CCHI basic class');
    case 'VIP': return t('VIP class');
    case 'Economy': return t('Economy class');
    case 'Business': return t('Business class');
    default: return fill(t('Class {tier}'), { tier });
  }
}

/** Who besides the employee a line covers: "Employee only", "Employee + 3 dependants". */
export function coverageText(line: ValueLine, t: Translate, dependantsOnFile?: number): string {
  if (line.dependantScope === 'None') return t('Employee only');
  if (dependantsOnFile === 0) return t('Employee — no dependants on file');
  if (line.dependantsCovered === 0) return t('Employee (no dependants on file in scope)');
  return line.dependantsCovered === 1
    ? t('Employee + 1 dependant')
    : fill(t('Employee + {n} dependants'), { n: line.dependantsCovered });
}

/**
 * The value of a line as one short phrase. Cash lines read "25% of basic = SAR 2,000"; benefits read in their own unit
 * (a tier, a number of tickets, an amount a child); facilities read as a limit ("Up to SAR 6,000").
 */
export function valueText(line: ValueLine, ctx: FormatContext): string {
  const { t } = ctx;
  const m = (n: number) => money(n, ctx);
  const isLoan = line.componentCode.toUpperCase().startsWith('LOAN_');
  switch (line.valueType) {
    case 'InKind':
      return t('Provided in kind');
    case 'PercentOfBasic':
      return line.monthlyCash != null
        ? fill(t('{rate}% of basic = {amount} a month'), { rate: percent(line.rate ?? 0), amount: m(line.monthlyCash) })
        : fill(t('{rate}% of basic salary'), { rate: percent(line.rate ?? 0) });
    case 'CoverageTier':
      return tierText(line.coverageTier, t);
    case 'Quantity': {
      const per = line.limitPeriod === 'PerTerm' ? t('per contract year') : t('a year');
      return fill(t('{n} × {class} ticket {per}'), { n: line.quantity ?? 1, class: tierText(line.coverageTier, t), per });
    }
    case 'MultipleOfBasic':
    case 'MultipleOfGross':
    case 'MultipleOfHousing': {
      const basis = line.valueType === 'MultipleOfHousing' ? t('housing allowance') : line.valueType === 'MultipleOfBasic' ? t('basic salary') : t('gross salary');
      const formula = `${multiple(line.rate ?? 0)} × ${basis}`;
      const figure = line.resolvedAmount ?? line.amount;
      return figure != null ? fill(t('Up to {amount} ({formula})'), { amount: m(figure), formula }) : formula;
    }
    case 'EligibilityOnly':
      return t('Included');
    case 'Amount':
    default: {
      if (line.monthlyCash != null && line.limitPeriod === 'Monthly') return fill(t('{amount} a month'), { amount: m(line.monthlyCash) });
      if (line.amount == null) return isLoan && line.resolvedAmount != null ? fill(t('Up to {amount}'), { amount: m(line.resolvedAmount) }) : '';
      if (isLoan) return fill(t('Up to {amount}'), { amount: m(line.resolvedAmount ?? line.amount) });
      if (line.limitPeriod === 'PerDay') return fill(t('{amount} a day'), { amount: m(line.amount) });
      if (line.dependantScope === 'Children') {
        const base = fill(t('{amount} a child a year'), { amount: m(line.amount) });
        return line.maxDependants != null ? `${base} · ${fill(t('up to {n} children'), { n: line.maxDependants })}` : base;
      }
      if (line.limitPeriod === 'Annual') return fill(t('{amount} a year'), { amount: m(line.amount) });
      return m(line.amount);
    }
  }
}

/**
 * Every reason a line is not (yet) given, in a sentence. One mapping for every code the server returns (PackageReasons);
 * "not eligible yet" is worded by its criterion and says from when. Unknown codes fall back to a neutral sentence.
 */
export function reasonText(code: string | null | undefined, t: Translate, criterion?: string | null, eligibleFrom?: string | null, locale = 'en'): string {
  if (!code) return '';
  if (code === 'ENTITLEMENT_NOT_ELIGIBLE_CRITERIA') {
    const when = eligibleFrom ? date(eligibleFrom, locale) : null;
    switch (criterion) {
      case 'ServiceMonths': return when ? fill(t('Applies from {date}, once the required months of service are completed.'), { date: when }) : t('Applies once the required months of service are completed.');
      case 'AfterProbation': return when ? fill(t('Applies from {date}, once probation ends.'), { date: when }) : t('Applies once probation ends.');
      case 'Nationality': return t('Limited to another nationality group, under a recorded legal basis.');
      default: return t('Not eligible yet.');
    }
  }
  const known: Record<string, string> = {
    ENTITLEMENT_NOT_OFFERED_BY_COMPANY: 'Your company does not offer this benefit.',
    ENTITLEMENT_NOT_IN_GRADE: 'Not included for this grade.',
    ENTITLEMENT_HOUSING_IN_KIND: 'Housing is provided in kind, so there is no housing allowance to advance against.',
    ENTITLEMENT_SALARY_MISSING: 'No salary is on file yet.',
    ENTITLEMENT_NATIONALITY_UNCONFIRMED: 'Needs confirmation: the contract does not record the nationality class yet.',
    ENTITLEMENT_LOAN_POLICY_BLOCKS: 'The loan policy does not allow it right now. The loan form gives the exact reason.',
    ENTITLEMENT_CELL_MISSING: 'Not set for this grade yet. HR has a gap to fill in the grade table.',
    GRADE_MISSING: 'No grade is recorded, so the grade standard cannot be shown.',
    ENTITLEMENT_FLOOR_HOUSING: 'The salary gives neither a housing allowance nor housing in kind (Article 61).',
    ENTITLEMENT_FLOOR_TRANSPORT: 'The salary gives neither a transport allowance nor transport in kind (Article 61).',
    ENTITLEMENT_TERM_OVERLAP: 'Already fixed under another contract term that overlaps this one.',
    ENTITLEMENT_ROW_IN_THE_WAY: 'The earlier term has this benefit fixed from a later date.',
  };
  return t(known[code] ?? 'This item is not available right now.');
}

// ── "Why?" — what a value is based on ──────────────────────────────────────────────────────────────

export interface EssWhy {
  basis: 'salary' | 'contract' | 'grade' | 'policy';
  gradeName: string | null;
  gradeNameAr: string | null;
  companyRule: boolean;
  since: string | null;
  until: string | null;
}

/** The employee's explanation of one line: the source, the grade, and whether it can change. */
export function essWhy(why: EssWhy, ctx: FormatContext): string[] {
  const { t, locale } = ctx;
  const grade = (locale === 'ar' ? why.gradeNameAr : null) || why.gradeName || '';
  const out: string[] = [];
  switch (why.basis) {
    case 'salary':
      out.push(fill(t('Paid with your salary from {date}, as in your Qiwa contract.'), { date: date(why.since, locale) }));
      break;
    case 'contract':
      out.push(fill(t('Fixed in your contract from {from} to {to}. Changes to the grade table do not alter it.'),
        { from: date(why.since, locale), to: why.until ? date(why.until, locale) : t('the end of the contract') }));
      break;
    case 'grade':
      out.push(fill(t('The standard for your grade ({grade}), in force since {date}. Not yet fixed in your contract.'),
        { grade, date: date(why.since, locale) }));
      break;
    case 'policy':
      out.push(fill(t('Current company policy for your grade ({grade}), in force since {date}. It can change.'),
        { grade, date: date(why.since, locale) }));
      break;
  }
  if (why.basis !== 'salary') out.push(why.companyRule ? t('Your company sets its own value for this grade.') : t('The same for every company in the group.'));
  return out;
}

export interface HrWhyInput {
  source: string;
  gradeStandardDiffers: boolean;
  isCompanyOverride: boolean;
  gradeName: string;
  companyName: string;
  cell?: { effectiveFrom: string; minServiceMonths: number | null; afterProbation: boolean; nationalityScope: string; nationalityBasis: string | null; note: string | null; valueText: string } | null;
  frozen?: { effectiveFrom: string; effectiveTo: string | null; verificationState: string; contractNumber: string } | null;
  salarySince?: string | null;
}

/** HR's explanation: the grade cell (and its criteria), the frozen row and its term, the salary row, and what differs. */
export function hrWhy(input: HrWhyInput, ctx: FormatContext): string[] {
  const { t, locale } = ctx;
  const out: string[] = [];
  const cellSince = input.cell ? date(input.cell.effectiveFrom, locale) : '';
  switch (input.source) {
    case 'Salary':
      out.push(fill(t('From the salary row in force since {date} (the Qiwa contract wage).'), { date: date(input.salarySince, locale) }));
      if (input.cell) out.push(fill(t('Grade standard for {grade}: {value}.'), { grade: input.gradeName, value: input.cell.valueText }));
      break;
    case 'ContractFrozen':
      if (input.frozen)
        out.push(fill(t('Fixed for contract {number} from {from} to {to}.'), {
          number: input.frozen.contractNumber, from: date(input.frozen.effectiveFrom, locale),
          to: input.frozen.effectiveTo ? date(input.frozen.effectiveTo, locale) : t('the end of the contract'),
        }));
      if (input.cell) out.push(fill(t('Copied from the grade cell for {grade}, in force since {date}.'), { grade: input.gradeName, date: cellSince }));
      if (input.frozen?.verificationState === 'Unverified') out.push(t('Loaded from the grade table and not yet confirmed against the signed contract.'));
      break;
    case 'GradeStandard':
      if (input.cell) out.push(fill(t('Grade cell for {grade}, in force since {date}. Not yet fixed for this contract year.'), { grade: input.gradeName, date: cellSince }));
      break;
    case 'Facility':
      if (input.cell) out.push(fill(t('Current policy: the grade cell for {grade}, in force since {date}.'), { grade: input.gradeName, date: cellSince }));
      break;
  }
  if (input.cell && input.source !== 'Salary')
    out.push(input.isCompanyOverride ? fill(t('{company} sets its own value for this grade.'), { company: input.companyName }) : t('The same for every company in the group.'));
  if (input.cell?.minServiceMonths) out.push(fill(t('Applies after {n} months of service.'), { n: input.cell.minServiceMonths }));
  if (input.cell?.afterProbation) out.push(t('Applies once probation ends.'));
  if (input.cell && input.cell.nationalityScope !== 'Any')
    out.push(fill(input.cell.nationalityScope === 'Saudi'
      ? t('Limited to Saudi employees. Legal basis: {basis}')
      : t('Limited to non-Saudi employees. Legal basis: {basis}'), { basis: input.cell.nationalityBasis ?? '' }));
  if (input.gradeStandardDiffers) out.push(t('The grade standard is different now. This is reviewed at renewal; nothing changes mid-year.'));
  return out;
}
