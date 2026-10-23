import type { Formatter } from './format';

/**
 * Plain names, units and value formatting for statutory rule keys (Setup → Statutory rules and
 * GL & rates → Statutory rates).
 *
 * THE UNIT IS THE SERVER'S, NOT OURS. `statutoryUnitFor` mirrors the backend unit registry,
 * backend-dotnet/Zayra.Api/Infrastructure/Payroll/StatutoryValueUnits.cs `SpecFor`, rule for rule
 * and in the same order, plus a handful of keys whose unit the registry leaves "Unconstrained" but
 * whose reader proves it (each one cites the reader). A rate is STORED as a decimal fraction of its
 * base (0.0975 = 9.75%) and payroll multiplies it straight into the wage
 * (KsaCalculators.cs `coveredWage * empAnnuity`), so a misread here moves payroll. Anything this
 * file cannot prove is `unknown` and is shown raw with a visible "Unit not specified" flag; it is
 * never given a guessed unit.
 *
 * Percent <-> fraction conversion is done on the decimal STRING (shiftDecimal), never through a
 * float multiply, so 0.0975 <-> 9.75 round-trips exactly.
 */

export type Translate = (key: string, params?: Record<string, string | number>) => string;

export type StatutoryUnitKind =
  | 'fraction' | 'amount' | 'multiplier' | 'divisor' | 'days' | 'years'
  | 'minutesPerDay' | 'minutesPerWeek' | 'hours' | 'count'
  | 'boolean' | 'date' | 'choice' | 'text' | 'unknown';

export interface StatutoryUnit {
  kind: StatutoryUnitKind;
  /** Admissible range in the STORED representation (a fraction for rates). */
  min?: number;
  max?: number;
  /** Decimal places the server accepts for the stored value. */
  maxScale?: number;
  /** A social-insurance contribution rate (capped at 30% by the server). */
  contribution?: boolean;
  /** ISO currency of an amount, only where the key or its seed proves it. */
  currency?: string;
  /** The amount is a monthly figure (proven by the reader or the seed). */
  perMonth?: boolean;
  /** Closed list of admissible words. */
  choices?: readonly string[];
  /** Stored spellings of a yes/no value. */
  trueValue?: string;
  falseValue?: string;
}

const CONTRIBUTION_FAMILIES = ['gosi.', 'saned.', 'gpssa.', 'grsia.', 'dews.'];
const RATE_SCALE = 6; // numeric(9,6), StatutoryValueUnits.RateScale

// Release A toggles (Application/Contracts/RenewalRules.cs ValidateOverride: 'true' or 'false').
const RENEWAL_TOGGLES = new Set(['contracts.as_is_requires_employee_acceptance', 'contracts.as_is_requires_qiwa_step']);

/** The unit of a rule key. `dataType` is the row's declared type ("decimal", "bool", "string", "int", "json"). */
export function statutoryUnitFor(ruleKey: string | null | undefined, dataType?: string | null): StatutoryUnit {
  const k = (ruleKey ?? '').trim().toLowerCase();
  const type = (dataType ?? '').trim().toLowerCase();
  if (!k) return { kind: 'unknown' };

  // nitaqat.curve.* are MHRSD regression constants: StatutoryValueUnits.SpecFor returns
  // Unconstrained for them. Only the `.verified` flag has a proven reading: NitaqatCurve.cs reads
  // it as a decimal and treats any non-zero value as verified (`verifiedFlag.Value != 0m`).
  if (k.startsWith('nitaqat.curve.')) {
    return k.endsWith('.verified') ? { kind: 'boolean', trueValue: '1', falseValue: '0' } : { kind: 'unknown' };
  }

  // StatutoryValueUnits.EnumeratedValues: ot.hourly_base ∈ {basic, wage}.
  if (k === 'ot.hourly_base') return { kind: 'choice', choices: ['basic', 'wage'] };

  if (k.endsWith('_rate') || k.endsWith('_ratio')) {
    const contribution = CONTRIBUTION_FAMILIES.some((family) => k.startsWith(family));
    return { kind: 'fraction', min: 0, max: contribution ? 0.3 : 1, maxScale: RATE_SCALE, contribution };
  }

  if (k.endsWith('_sar') || k.endsWith('contribution_salary_min') || k.endsWith('contribution_salary_max')) {
    // _sar keys are Saudi riyals by name. The GPSSA bounds are AED (StatutoryRuleSeeder.cs, "AED 1,000" /
    // "AED 50,000"). GOSI bounds are monthly (KsaGosiWageBounds.cs: "The bounds are MONTHLY"), and so is
    // the Nitaqat counting wage (MHRSD decision 61706 as cited in StatutoryRuleSeeder.cs: "the monthly wage").
    const currency = k.endsWith('_sar') ? 'SAR' : k.startsWith('gpssa.') ? 'AED' : undefined;
    const perMonth = k.startsWith('gosi.covered_wage_') || k.startsWith('nitaqat.counting_wage_');
    return { kind: 'amount', min: 0, max: 100_000_000, maxScale: 2, currency, perMonth };
  }

  if (k.endsWith('_multiplier')) return { kind: 'multiplier', min: 0, max: 10, maxScale: 4 };
  if (k.endsWith('_divisor')) return { kind: 'divisor', min: 1, max: 366, maxScale: 4 };
  if (k.endsWith('_days') || k.endsWith('_days_per_year')) return { kind: 'days', min: 0, max: 366, maxScale: 2 };
  if (k.endsWith('_years')) return { kind: 'years', min: 0, max: 100, maxScale: 2 };
  if (k.endsWith('_minutes_per_day')) return { kind: 'minutesPerDay', min: 0, max: 1440, maxScale: 0 };
  if (k.endsWith('_minutes_per_week')) return { kind: 'minutesPerWeek', min: 0, max: 10080, maxScale: 0 };
  if (k.endsWith('_monthly_hours') || k.endsWith('_hours')) return { kind: 'hours', min: 0, max: 744, maxScale: 2 };

  // ── Keys SpecFor leaves Unconstrained, whose reader proves the unit ──
  // KsaStatutorySpecialLeave.cs: "Birth leave's own event-date limit", a day count (fallback 7m).
  if (k === 'leave.event_date_max_lead_days.paternity') return { kind: 'days', min: 0, max: 366, maxScale: 2 };
  // RenewalRuleSet.cs: `int Art55MaxConsecutiveRenewals`; RenewalRules.cs: a whole number 1–3650.
  if (k === 'ksa.art55.max_consecutive_renewals') return { kind: 'count', min: 1, max: 3650, maxScale: 0 };
  // RenewalRules.cs ValidateOverride: 'conservative' or 'lenient'.
  if (k === 'ksa.art55.reading') return { kind: 'choice', choices: ['conservative', 'lenient'] };
  // RenewalRules.cs ValidateOverride: "a date written yyyy-MM-dd".
  if (k === 'ksa.unified_contract_from') return { kind: 'date' };
  // StatutoryFlag.cs reads true/yes/y/1 and false/no/n/0; seeded bool rows are written 'true'/'false'.
  if (RENEWAL_TOGGLES.has(k) || type === 'bool' || type === 'boolean') return { kind: 'boolean', trueValue: 'true', falseValue: 'false' };
  // A word value has nothing to measure; it is shown as stored.
  if (type === 'string') return { kind: 'text' };
  return { kind: 'unknown' };
}

// ── Exact decimal arithmetic ────────────────────────────────────────────────────────────────

/** Arabic-Indic / Persian digits and the Arabic decimal separator, read as ASCII. */
export function normalizeDigits(raw: string): string {
  return raw
    .replace(/[٠-٩]/g, (d) => String(d.charCodeAt(0) - 0x0660))
    .replace(/[۰-۹]/g, (d) => String(d.charCodeAt(0) - 0x06f0))
    .replace(/٫/g, '.')
    .trim();
}

/** Moves the decimal point of a plain decimal string `places` to the right (negative = left). Null if not a plain decimal. */
export function shiftDecimal(raw: string, places: number): string | null {
  const m = /^([+-]?)(\d*)(?:\.(\d*))?$/.exec(raw.trim());
  if (!m || (!m[2] && !m[3])) return null;
  const sign = m[1] === '-' ? '-' : '';
  const intPart = m[2] ?? '';
  let digits = intPart + (m[3] ?? '');
  let point = intPart.length + places;
  if (point < 0) { digits = '0'.repeat(-point) + digits; point = 0; }
  if (point > digits.length) digits += '0'.repeat(point - digits.length);
  const whole = digits.slice(0, point).replace(/^0+/, '') || '0';
  const fraction = digits.slice(point).replace(/0+$/, '');
  const out = fraction ? `${whole}.${fraction}` : whole;
  return out === '0' ? '0' : sign + out;
}

/** "0.0975" → "9.75". */
export const fractionToPercent = (stored: string) => shiftDecimal(stored, 2);
/** "9.75" → "0.0975". */
export const percentToFraction = (percent: string) => shiftDecimal(percent, -2);

const PLAIN_DECIMAL = /^\d+(\.\d+)?$/;
const scaleOf = (s: string) => (s.split('.')[1] ?? '').length;

// ── Names ────────────────────────────────────────────────────────────────────────────────────

const ACTIVITY_WORDS = (code: string) => code.replace(/_/g, ' ').toLowerCase().replace(/^\w/, (c) => c.toUpperCase());

/** The plain-language name of a rule key, or null when there is none (the caller shows the key). */
export function knownStatutoryRuleName(t: Translate, key: string): string | null {
  switch (key) {
    // Shared with GL & rates → Statutory rates.
    case 'gosi.saudi_employee_rate': return t('GOSI contribution, Saudi employee share');
    case 'gosi.saudi_employer_rate': return t('GOSI contribution, Saudi employer share');
    case 'gosi.new_entrant_employee_rate': return t('GOSI annuities, new entrant (registered from 3 July 2024), employee share');
    case 'gosi.new_entrant_employer_rate': return t('GOSI annuities, new entrant (registered from 3 July 2024), employer share');
    case 'gosi.covered_wage_min_annuities_sar': return t('GOSI minimum contributory wage, annuities (warning only)');
    case 'gosi.covered_wage_min_occupational_hazards_sar': return t('GOSI minimum contributory wage, occupational hazards (warning only)');
    case 'gosi.employee_rate': return t('GOSI contribution, employee share');
    case 'gosi.saned_rate': return t('SANED unemployment insurance contribution');
    case 'gosi.expat_occupational_hazard_rate': return t('GOSI occupational hazards, non-Saudi employees');
    case 'gosi.covered_wage_ceiling_sar': return t('GOSI maximum contributory wage');
    case 'gosi.covered_wage_floor_sar': return t('GOSI minimum contributory wage');
    case 'gosi.retirement_age_years': return t('GOSI retirement age');
    case 'ot.standard_multiplier': return t('Overtime pay multiplier');
    case 'ot.restday_multiplier': return t('Rest-day overtime multiplier');
    case 'ot.holiday_multiplier': return t('Public-holiday overtime multiplier');
    case 'ot.standard_monthly_hours': return t('Standard monthly working hours');
    case 'leave.annual_base_days': return t('Annual leave entitlement');
    case 'leave.annual_tiered_days': return t('Annual leave after the service threshold');
    case 'leave.annual_tier_threshold_years': return t('Years of service for the higher annual leave');
    case 'leave.maternity_days': return t('Maternity leave');
    case 'leave.paternity_days': return t('Paternity leave');
    case 'leave.marriage_days': return t('Marriage leave');
    case 'leave.bereavement_days': return t('Bereavement leave');
    case 'leave.hajj_max_days': return t('Hajj leave, maximum');
    case 'leave.sick_band1_days': return t('Sick leave at full pay');
    case 'leave.sick_band2_days': return t('Sick leave at reduced pay');
    case 'leave.sick_band3_days': return t('Sick leave without pay');
    case 'eosb.tier1_days_per_year': return t('End-of-service days per year, first five years');
    case 'eosb.tier2_days_per_year': return t('End-of-service days per year, after five years');
    case 'nitaqat.default_target_ratio': return t('Nitaqat Saudization target');
    case 'gpssa.national_employee_rate': return t('GPSSA pension contribution, national employee share');
    case 'gpssa.national_employer_rate': return t('GPSSA pension contribution, national employer share');
    case 'grsia.national_employee_rate': return t('GRSIA pension contribution, national employee share');
    case 'grsia.national_employer_rate': return t('GRSIA pension contribution, national employer share');
    case 'dews.tier1_monthly_rate': return t('DEWS monthly contribution, first five years');
    case 'dews.tier2_monthly_rate': return t('DEWS monthly contribution, after five years');
    // The rest of the seeded catalogue (StatutoryRuleSeeder.cs).
    case 'nitaqat.counting_wage_floor_sar': return t('Nitaqat wage to count a Saudi as one worker');
    case 'nitaqat.counting_wage_half_floor_sar': return t('Nitaqat wage to count a Saudi as half a worker');
    case 'lop.monthly_day_divisor': return t('Days in a month, for unpaid-leave deductions');
    case 'lop.standard_work_minutes_per_day': return t('Working minutes a day, for unpaid-time deductions');
    case 'ot.hourly_base': return t('Overtime hour is priced on');
    case 'workhours.standard_minutes_per_day': return t('Standard working minutes a day');
    case 'workhours.ramadan_minutes_per_day': return t('Ramadan working minutes a day');
    case 'workhours.ramadan_minutes_per_week': return t('Ramadan working minutes a week');
    case 'workhours.ramadan_scope': return t('Who works Ramadan hours');
    case 'leave.sick_band1_pay_rate': return t('Sick pay, first band');
    case 'leave.sick_band2_pay_rate': return t('Sick pay, second band');
    case 'leave.sick_band3_pay_rate': return t('Sick pay, third band');
    case 'leave.sick_apply_statutory_scale': return t('Apply the statutory sick-pay scale');
    case 'leave.sick_reduction_wage_base': return t('Wage that reduced sick pay is based on');
    case 'leave.bereavement_sibling_days': return t('Bereavement leave, sibling');
    case 'leave.hajj_min_days': return t('Hajj leave, minimum');
    case 'leave.hajj_min_service_years': return t('Years of service before Hajj leave');
    case 'leave.iddah_muslim_days': return t('Iddah leave, Muslim employee');
    case 'leave.iddah_non_muslim_days': return t('Iddah leave, non-Muslim employee');
    case 'leave.event_date_max_lead_days': return t('Longest gap between the event and the leave');
    case 'leave.event_date_max_lead_days.paternity': return t('Longest gap between the birth and paternity leave');
    case 'eosb.include_transport': return t('End of service includes the transport allowance');
    case 'eosb.include_other_allowances': return t('End of service includes other allowances');
    case 'eosb.exclude_unpaid_leave': return t('End of service leaves out unpaid leave');
    case 'ksa.art55.max_consecutive_renewals': return t('Fixed-term renewals allowed (Article 55)');
    case 'ksa.art55.max_total_years': return t('Fixed-term contract years allowed (Article 55)');
    case 'ksa.art55.reading': return t('How Article 55 is read');
    case 'ksa.unified_contract_from': return t('Unified employment contract applies from');
    case 'qiwa.contract_response_days': return t('Days an employee has to answer a contract in Qiwa');
    case 'contracts.renewal_lead_days': return t('Renewal review starts before the contract ends');
    case 'contracts.offer_lead_days': return t('Renewal offer is due before the notice date');
    case 'contracts.qiwa_submit_lead_days': return t('Qiwa submission is due before the contract ends');
    case 'contracts.qiwa_gate_lead_days': return t('Last Qiwa check before the contract ends');
    case 'contracts.default_non_renewal_notice_days': return t('Default non-renewal notice');
    case 'contracts.open_margin_days': return t('Preparation time for a renewal case');
    case 'contracts.original_term_joining_tolerance_days': return t('Days a first contract may start after joining');
    case 'contracts.chain_gap_tolerance_days': return t('Gap allowed between two contract terms');
    case 'contracts.as_is_requires_employee_acceptance': return t('Employee must accept an unchanged renewal');
    case 'contracts.as_is_requires_qiwa_step': return t('Unchanged renewal still goes through Qiwa');
    case 'gpssa.contribution_salary_min': return t('GPSSA minimum contribution salary');
    case 'gpssa.contribution_salary_max': return t('GPSSA maximum contribution salary');
    case 'emiratisation.target_ratio': return t('Emiratisation target');
    case 'qatarization.target_ratio': return t('Qatarisation target');
    case 'grsia.include_housing_in_contribution_salary': return t('GRSIA contribution salary includes housing');
    default: break;
  }
  // Generated families.
  const gcc = /^gosi\.gcc\.([a-z]{2})\.(employee|employer)_rate$/i.exec(key);
  if (gcc) {
    const country = gcc[1].toUpperCase();
    return gcc[2].toLowerCase() === 'employee'
      ? t('GOSI for GCC nationals from {country}, employee share', { country })
      : t('GOSI for GCC nationals from {country}, employer share', { country });
  }
  const verified = /^nitaqat\.curve\.([A-Z0-9_]+)\.verified$/i.exec(key);
  if (verified) return t('Nitaqat curve checked against the published annex, {activity}', { activity: ACTIVITY_WORDS(verified[1]) });
  const curve = /^nitaqat\.curve\.([A-Z0-9_]+)\.([A-Za-z0-9_]+)\.(m|c)$/.exec(key);
  if (curve) {
    const params = { activity: ACTIVITY_WORDS(curve[1]), band: curve[2] };
    return curve[3] === 'm' ? t('Nitaqat curve gradient (m), {activity}, {band}', params) : t('Nitaqat curve intercept (c), {activity}, {band}', params);
  }
  return null;
}

/** The name shown for a rule: its plain name, else the key itself (never an invented name). */
export const statutoryRuleName = (t: Translate, key: string) => knownStatutoryRuleName(t, key) ?? key;

/** The unit in words, for the form and the Details disclosure. */
export function statutoryUnitName(t: Translate, unit: StatutoryUnit): string {
  switch (unit.kind) {
    case 'fraction': return unit.contribution ? t('Percentage of the contributory wage') : t('Percentage');
    case 'amount':
      if (!unit.currency) return t('Amount, currency not specified');
      return unit.perMonth ? t('Monthly amount in {currency}', { currency: unit.currency }) : t('Amount in {currency}', { currency: unit.currency });
    case 'multiplier': return t('Multiplier');
    case 'divisor': return t('Days (divisor)');
    case 'days': return t('Days');
    case 'years': return t('Years');
    case 'minutesPerDay': return t('Minutes a day');
    case 'minutesPerWeek': return t('Minutes a week');
    case 'hours': return t('Hours');
    case 'count': return t('Number of renewals');
    case 'boolean': return t('Yes or no');
    case 'date': return t('Date');
    case 'choice': return t('One of a fixed list');
    case 'text': return t('Text');
    default: return t('Unit not specified');
  }
}

const choiceLabel = (t: Translate, word: string): string | null => {
  switch (word.toLowerCase()) {
    case 'basic': return t('Basic salary');
    case 'wage': return t('Full wage');
    case 'conservative': return t('Conservative reading');
    case 'lenient': return t('Lenient reading');
    default: return null;
  }
};

export interface StatutoryValueDisplay {
  text: string;
  /** A visible warning that must travel with the value (unit unknown, unreadable value…). */
  flag?: string;
}

const booleanOf = (unit: StatutoryUnit, raw: string): boolean | null => {
  const v = raw.trim().toLowerCase();
  if (unit.trueValue === '1') {
    if (!PLAIN_DECIMAL.test(v)) return null;
    return Number(v) !== 0;
  }
  if (['true', 'yes', 'y', '1'].includes(v)) return true;
  if (['false', 'no', 'n', '0'].includes(v)) return false;
  return null;
};

/** A stored value in words with its unit: "9.75%", "SAR 45,000 a month", "21 days". */
export function formatStatutoryValue(t: Translate, f: Formatter, unit: StatutoryUnit, raw: string | null | undefined): StatutoryValueDisplay {
  const value = (raw ?? '').trim();
  if (!value) return { text: '—' };
  const unreadable = (): StatutoryValueDisplay => ({ text: value, flag: t('Value not readable') });
  const numeric = (): number | null => {
    const v = normalizeDigits(value);
    return /^-?\d+(\.\d+)?$/.test(v) ? Number(v) : null;
  };
  const exact = (s: string, minDigits = 0) => f.number(Number(s), { minimumFractionDigits: minDigits, maximumFractionDigits: Math.max(minDigits, Math.min(20, scaleOf(s))) });

  switch (unit.kind) {
    case 'fraction': {
      const pct = fractionToPercent(normalizeDigits(value));
      if (pct == null) return unreadable();
      return { text: t('{value}%', { value: exact(pct, 2) }) };
    }
    case 'amount': {
      const n = numeric();
      if (n == null) return unreadable();
      const digits = Number.isInteger(n) ? 0 : 2;
      if (!unit.currency) return { text: f.number(n, { minimumFractionDigits: digits, maximumFractionDigits: 2 }), flag: t('Currency not specified') };
      const amount = f.money(n, unit.currency, { decimals: digits });
      return { text: unit.perMonth ? t('{amount} a month', { amount }) : amount };
    }
    case 'multiplier': {
      const n = numeric();
      return n == null ? unreadable() : { text: t('{value}×', { value: exact(normalizeDigits(value)) }) };
    }
    // A divisor is the number of days a monthly amount is split into; the server presents it as days
    // too (StatutoryOverridePolicy.DisplayUnitFor: Divisor => "days").
    case 'divisor':
    case 'days': {
      const n = numeric();
      return n == null ? unreadable() : { text: t('{count, plural, one {# day} other {# days}}', { count: n }) };
    }
    case 'years': {
      const n = numeric();
      return n == null ? unreadable() : { text: t('{count, plural, one {# year} other {# years}}', { count: n }) };
    }
    case 'minutesPerDay': {
      const n = numeric();
      return n == null ? unreadable() : { text: t('{count, plural, one {# minute a day} other {# minutes a day}}', { count: n }) };
    }
    case 'minutesPerWeek': {
      const n = numeric();
      return n == null ? unreadable() : { text: t('{count, plural, one {# minute a week} other {# minutes a week}}', { count: n }) };
    }
    case 'hours': {
      const n = numeric();
      return n == null ? unreadable() : { text: t('{count, plural, one {# hour} other {# hours}}', { count: n }) };
    }
    case 'count': {
      const n = numeric();
      return n == null ? unreadable() : { text: t('{count, plural, one {# renewal} other {# renewals}}', { count: n }) };
    }
    case 'boolean': {
      const b = booleanOf(unit, value);
      return b == null ? unreadable() : { text: b ? t('Yes') : t('No') };
    }
    case 'date':
      return /^\d{4}-\d{2}-\d{2}$/.test(value) ? { text: f.date(value) } : unreadable();
    case 'choice': {
      const label = unit.choices?.includes(value.toLowerCase()) ? choiceLabel(t, value) ?? value : null;
      return label == null ? unreadable() : { text: label };
    }
    case 'text':
      return { text: choiceLabel(t, value) ?? value };
    default:
      return { text: value, flag: t('Unit not specified') };
  }
}

// ── Editing ──────────────────────────────────────────────────────────────────────────────────

/** What the person types for a stored value: a fraction is edited as a percentage. */
export function toInputValue(unit: StatutoryUnit, stored: string): string {
  if (unit.kind === 'fraction') return fractionToPercent(normalizeDigits(stored)) ?? stored;
  return stored;
}

export type ParsedInput = { ok: true; stored: string } | { ok: false; error: string };

/** Turns what the person typed into the value the server stores, or says why it cannot. */
export function parseInputValue(t: Translate, unit: StatutoryUnit, typed: string): ParsedInput {
  const raw = normalizeDigits(typed);
  if (!raw) return { ok: false, error: t('Enter a value.') };
  const plainNumber = (): { ok: false; error: string } | null =>
    PLAIN_DECIMAL.test(raw) ? null : { ok: false, error: t('Enter a plain number, such as 45000 or 1.5, without commas, signs or symbols.') };

  switch (unit.kind) {
    case 'fraction': {
      const bad = plainNumber();
      if (bad) return bad;
      if (scaleOf(raw) > RATE_SCALE - 2) return { ok: false, error: t('Use at most {digits} decimal places for a percentage.', { digits: RATE_SCALE - 2 }) };
      const stored = percentToFraction(raw);
      if (stored == null) return { ok: false, error: t('Enter a plain number, such as 45000 or 1.5, without commas, signs or symbols.') };
      const max = unit.max ?? 1;
      if (Number(stored) > max) return { ok: false, error: t('The most allowed is {max}%. Type the percentage itself: 9.75 means 9.75%.', { max: fractionToPercent(String(max)) ?? String(max * 100) }) };
      return { ok: true, stored };
    }
    case 'amount': case 'multiplier': case 'divisor': case 'days': case 'years':
    case 'minutesPerDay': case 'minutesPerWeek': case 'hours': case 'count': {
      const bad = plainNumber();
      if (bad) return bad;
      if (unit.maxScale != null && scaleOf(raw) > unit.maxScale) {
        return unit.maxScale === 0
          ? { ok: false, error: t('Enter a whole number.') }
          : { ok: false, error: t('Use at most {digits} decimal places.', { digits: unit.maxScale }) };
      }
      const n = Number(raw);
      if ((unit.min != null && n < unit.min) || (unit.max != null && n > unit.max)) {
        return { ok: false, error: t('Enter a number from {min} to {max}.', { min: unit.min ?? 0, max: unit.max ?? n }) };
      }
      return { ok: true, stored: shiftDecimal(raw, 0) ?? raw };
    }
    case 'boolean':
      return raw === unit.trueValue || raw === unit.falseValue ? { ok: true, stored: raw } : { ok: false, error: t('Choose Yes or No.') };
    case 'date':
      return /^\d{4}-\d{2}-\d{2}$/.test(raw) ? { ok: true, stored: raw } : { ok: false, error: t('Choose a date.') };
    case 'choice':
      return unit.choices?.includes(raw) ? { ok: true, stored: raw } : { ok: false, error: t('Choose one of the listed values.') };
    default:
      return { ok: true, stored: raw };
  }
}

/** The suffix shown inside the input, so the unit is visible while typing. */
export function inputSuffix(t: Translate, unit: StatutoryUnit): string | null {
  switch (unit.kind) {
    case 'fraction': return '%';
    case 'amount': return unit.currency ?? null;
    case 'multiplier': return '×';
    case 'divisor': return t('Days');
    case 'days': case 'years': case 'minutesPerDay': case 'minutesPerWeek': case 'hours': return statutoryUnitName(t, unit);
    case 'count': return t('Renewals');
    default: return null;
  }
}

/** One sentence under the input that says exactly what to type. */
export function inputHint(t: Translate, unit: StatutoryUnit): string {
  switch (unit.kind) {
    case 'fraction':
      return unit.contribution
        ? t('Type the percentage: 9.75 means 9.75%. Payroll stores it as the fraction 0.0975. The most allowed is 30%.')
        : t('Type the percentage: 35 means 35%. Payroll stores it as the fraction 0.35. The most allowed is 100%.');
    case 'amount':
      return unit.currency
        ? t('Type the amount in {currency} without commas, for example 45000.', { currency: unit.currency })
        : t('Type the amount without commas. The currency is not recorded for this rule, so check the source.');
    case 'multiplier': return t('A multiplier, for example 1.5 for time-and-a-half.');
    case 'divisor': return t('The number a monthly amount is divided by to get one day, for example 30.');
    case 'boolean': return t('Choose Yes or No.');
    case 'date': return t('Choose the date it applies from.');
    case 'choice': return t('Choose one of the listed values.');
    case 'text': return t('Saved exactly as typed.');
    case 'unknown': return t('No unit is on record for this rule, so the value is saved exactly as typed. Check the source before you send it.');
    default: return t('Enter a number from {min} to {max}.', { min: unit.min ?? 0, max: unit.max ?? 0 });
  }
}

/** Choices for a select-type unit, as [stored, label]. */
export function choiceOptions(t: Translate, unit: StatutoryUnit): Array<[string, string]> {
  if (unit.kind === 'boolean') return [[unit.trueValue ?? 'true', t('Yes')], [unit.falseValue ?? 'false', t('No')]];
  if (unit.kind === 'choice') return (unit.choices ?? []).map((c) => [c, choiceLabel(t, c) ?? c]);
  return [];
}

// ── The sentence an approver signs ─────────────────────────────────────────────────────────

export interface ChangeSentenceInput {
  kind: 'change' | 'retire';
  label: string;
  /** The value in force on the start date (change) or the company value being stopped (retire). */
  oldText: string | null;
  /** The requested value (change) or the published rate payroll returns to (retire). */
  newText: string | null;
  date: string;
}

/** "Change {label} from {old} to {new}, starting {date}", with the no-previous-value and retire variants. */
export function describeStatutoryChange(t: Translate, input: ChangeSentenceInput): string {
  const { label, oldText, newText, date } = input;
  if (input.kind === 'retire') {
    return newText
      ? t("Stop your company's value for {label} ({old}), starting {date}. Payroll goes back to the published rate of {new}.", { label, old: oldText ?? '—', new: newText, date })
      : t("Stop your company's value for {label} ({old}), starting {date}. Payroll goes back to the published rate.", { label, old: oldText ?? '—', date });
  }
  return oldText
    ? t('Change {label} from {old} to {new}, starting {date}', { label, old: oldText, new: newText ?? '—', date })
    : t('Set {label} to {new}, starting {date}. There is no previous value.', { label, new: newText ?? '—', date });
}
