/**
 * Which Saudi statutory special leave a leave type is, by the same rules the server applies
 * (KsaStatutorySpecialLeave.Classify): code, English name and category. Display-only — the server
 * decides every floor and limit; this only chooses which hints and options a form shows.
 */
export type SaudiStatutoryLeaveKind =
  | 'Maternity' | 'Paternity' | 'Marriage' | 'Bereavement' | 'BereavementSibling' | 'Hajj' | 'Iddah';

export function isSaudiStatutoryLeave(code?: string | null, nameEn?: string | null, category?: string | null): SaudiStatutoryLeaveKind | null {
  const c = (code ?? '').trim().toUpperCase();
  const n = (nameEn ?? '').trim().toUpperCase();
  const cat = (category ?? '').trim().toUpperCase();
  const name = (...needles: string[]) => needles.some(x => n.includes(x));
  if (name('UNPAID', 'EXTENSION')) return null;
  if (c === 'IDDAH' || c === 'IDDA' || name('IDDAH') || cat === 'IDDAH') return 'Iddah';
  if (c === 'MAT' || c === 'MATERNITY' || name('MATERNITY') || cat === 'MATERNITY') return 'Maternity';
  if (c === 'PAT' || c === 'PATERNITY' || name('PATERNITY') || cat === 'PATERNITY') return 'Paternity';
  if (c === 'MARRIAGE' || name('MARRIAGE') || cat === 'MARRIAGE') return 'Marriage';
  if (c === 'HAJJ' || name('HAJJ') || cat === 'HAJJ') return 'Hajj';
  if (c === 'BEREAVEMENT' || name('BEREAVEMENT', 'DEATH') || cat === 'BEREAVEMENT')
    return name('SIBLING', 'BROTHER', 'SISTER') ? 'BereavementSibling' : 'Bereavement';
  return null;
}
