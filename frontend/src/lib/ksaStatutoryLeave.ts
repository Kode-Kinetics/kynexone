/**
 * Which Saudi statutory special leave a leave type is, by the same rules the server applies
 * (KsaStatutorySpecialLeave.Classify): code, English name and category. Display-only — the server
 * decides every floor and limit; this only chooses which hints and options a form shows.
 * The names are the server's KsaStatutoryLeaveKind names.
 */
import type { StatutoryLeaveKindName } from '../api/leave';

export type SaudiStatutoryLeaveKind = StatutoryLeaveKindName;

export function isSaudiStatutoryLeave(code?: string | null, nameEn?: string | null, category?: string | null): SaudiStatutoryLeaveKind | null {
  const c = (code ?? '').trim().toUpperCase();
  const n = (nameEn ?? '').trim().toUpperCase();
  const cat = (category ?? '').trim().toUpperCase();
  const name = (...needles: string[]) => needles.some(x => n.includes(x));
  if (name('UNPAID', 'EXTENSION')) return null;
  if (c === 'IDDAH' || c === 'IDDA' || name('IDDAH') || cat === 'IDDAH')
    return name('NON-MUSLIM', 'NON MUSLIM', 'NONMUSLIM') ? 'IddahNonMuslim' : 'IddahMuslim';
  if (c === 'MAT' || c === 'MATERNITY' || name('MATERNITY') || cat === 'MATERNITY') return 'Maternity';
  if (c === 'PAT' || c === 'PATERNITY' || name('PATERNITY') || cat === 'PATERNITY') return 'Paternity';
  if (c === 'MARRIAGE' || name('MARRIAGE') || cat === 'MARRIAGE') return 'Marriage';
  if (c === 'HAJJ' || name('HAJJ') || cat === 'HAJJ') return 'Hajj';
  if (c === 'BEREAVEMENT' || name('BEREAVEMENT', 'DEATH') || cat === 'BEREAVEMENT')
    return name('SIBLING', 'BROTHER', 'SISTER') ? 'BereavementSibling' : 'Bereavement';
  return null;
}

/** Statute set in calendar time (weeks, months) — matches the server's IsCalendarSpan. */
export function isCalendarSpanLeave(kind: SaudiStatutoryLeaveKind | null): boolean {
  return kind === 'Maternity' || kind === 'IddahMuslim';
}

/** The kinds whose requester may declare a separate event (a second death, birth, marriage). */
export function isDeclarableLeave(kind: SaudiStatutoryLeaveKind | null): boolean {
  return kind === 'Bereavement' || kind === 'BereavementSibling' || kind === 'Paternity' || kind === 'Marriage';
}

/** English label keys for each kind; translated with t(). */
export const STATUTORY_KIND_LABEL: Record<SaudiStatutoryLeaveKind, string> = {
  Maternity: 'Maternity leave',
  Paternity: 'Leave for the birth of a child',
  Marriage: 'Marriage leave',
  Bereavement: 'Bereavement leave',
  BereavementSibling: 'Bereavement leave for a brother or sister',
  Hajj: 'Hajj leave',
  IddahMuslim: 'Iddah leave',
  IddahNonMuslim: 'Leave for a non-Muslim widow',
};
