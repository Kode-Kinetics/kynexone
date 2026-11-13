import { isRealDate } from './dates';

/**
 * Two optional hire answers that payroll and exit read.
 *
 * GOSI first registration date (Saudi nationals only): since 3 July 2024 the contribution schedule depends on when the person FIRST registered with
 * GOSI. Before that date: the existing schedule. On or after: the new-entrant schedule. It is read off the person's GOSI record and is never inferred;
 * blank leaves the cohort unknown (payroll computes it on the existing schedule and raises a finding until it is recorded).
 *
 * Notice period: each company has a default (the country's `notice_period_days`, 60 for Saudi Arabia). A new hire shows it pre-filled; HR can change it
 * per person. A blank field means "the company default", and 0 is the person's own answer.
 */
export const GOSI_NEW_ENTRANT_FROM = '2024-07-03';

/** The schedule a first-registration date puts a person on; null while the date is blank or not a real date. */
export function gosiCohortFor(date: string): 'existing' | 'newEntrant' | null {
  const v = (date ?? '').trim();
  if (!isRealDate(v)) return null;
  return v < GOSI_NEW_ENTRANT_FROM ? 'existing' : 'newEntrant';
}

/** The notice period the form shows and sends: what was typed (0 included), else the company default, else nothing. */
export function effectiveNoticeDays(typed: string, companyDefault: number | null | undefined): number | undefined {
  const v = (typed ?? '').trim();
  if (v !== '' && /^\d+$/.test(v)) return Number(v);
  return typeof companyDefault === 'number' && companyDefault >= 0 ? companyDefault : undefined;
}

/** True for a whole number of days from 0 to 365, the server's own range. */
export const isValidNoticeDays = (v: string) => /^\d+$/.test(v.trim()) && Number(v.trim()) <= 365;
