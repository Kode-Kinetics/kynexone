// Which identity documents a profile shows. A Saudi employee has an Iqama or National ID, not an
// Emirates ID; showing every GCC document to everyone reads as a template, not a record.
// The jurisdiction is the employing company's country (companies[].countryCode on /auth/me).
// Import-free so node --test can load it.

export type GccCountry = 'SA' | 'AE' | 'QA' | 'KW' | 'BH' | 'OM';

export type IdentityKey = 'nationalId' | 'emiratesId' | 'qid' | 'civilId' | 'passport' | 'visa';

export interface IdentityValues {
  iqamaNumber?: string | null;
  iqamaExpiry?: string | null;
  emiratesId?: string | null;
  emiratesIdExpiry?: string | null;
  qid?: string | null;
  qidExpiry?: string | null;
  civilId?: string | null;
  civilIdExpiry?: string | null;
  passportNumber?: string | null;
  passportExpiry?: string | null;
  visaNumber?: string | null;
  visaExpiry?: string | null;
}

export interface IdentityRow {
  key: IdentityKey;
  number: string | null;
  expiry: string | null;
}

const GCC: readonly GccCountry[] = ['SA', 'AE', 'QA', 'KW', 'BH', 'OM'];

export function normalizeCountry(value: unknown): GccCountry | null {
  if (typeof value !== 'string') return null;
  const code = value.trim().toUpperCase();
  return (GCC as readonly string[]).includes(code) ? (code as GccCountry) : null;
}

/** One country for the employee: every company on the session must agree, else unknown. */
export function companyCountry(companies: readonly { countryCode?: string | null }[] | null | undefined): GccCountry | null {
  const codes = new Set((companies ?? []).map((c) => normalizeCountry(c.countryCode)).filter((c): c is GccCountry => c !== null));
  return codes.size === 1 ? [...codes][0] : null;
}

/** The national identity document each GCC country issues to residents. */
const PRIMARY_ID: Record<GccCountry, IdentityKey> = {
  SA: 'nationalId', // Iqama for residents, National ID for Saudis — the same column (iqamaNumber)
  AE: 'emiratesId',
  QA: 'qid',
  KW: 'civilId',
  BH: 'civilId', // CPR
  OM: 'civilId',
};

function clean(value: string | null | undefined): string | null {
  return value && value.trim() ? value.trim() : null;
}

/**
 * Rows to show, in order. The jurisdiction's own ID always shows (as "not on file" when empty, so HR
 * can see the gap); passport always shows; any other document shows only when the record holds one.
 * With no known jurisdiction, only documents that are actually on the record are shown.
 */
export function identityRows(country: GccCountry | null, v: IdentityValues): IdentityRow[] {
  const all: IdentityRow[] = [
    { key: 'nationalId', number: clean(v.iqamaNumber), expiry: clean(v.iqamaExpiry) },
    { key: 'emiratesId', number: clean(v.emiratesId), expiry: clean(v.emiratesIdExpiry) },
    { key: 'qid', number: clean(v.qid), expiry: clean(v.qidExpiry) },
    { key: 'civilId', number: clean(v.civilId), expiry: clean(v.civilIdExpiry) },
    { key: 'passport', number: clean(v.passportNumber), expiry: clean(v.passportExpiry) },
    { key: 'visa', number: clean(v.visaNumber), expiry: clean(v.visaExpiry) },
  ];
  const primary = country ? PRIMARY_ID[country] : null;
  const rows = all.filter((row) => {
    if (row.key === primary || row.key === 'passport') return true;
    return row.number !== null || row.expiry !== null;
  });
  if (!primary) return rows;
  return [...rows.filter((r) => r.key === primary), ...rows.filter((r) => r.key !== primary)];
}

/** Dialling code for the phone-number hint. Null when unknown: the hint then asks for a country code. */
export function dialCode(country: GccCountry | null): string | null {
  switch (country) {
    case 'SA': return '+966';
    case 'AE': return '+971';
    case 'QA': return '+974';
    case 'KW': return '+965';
    case 'BH': return '+973';
    case 'OM': return '+968';
    default: return null;
  }
}

/** Expiry alerts for the documents that are shown (never for a document the jurisdiction hides). */
export function expiringDocuments(
  rows: readonly IdentityRow[],
  todayIso: string,
  withinDays = 60,
): { key: IdentityKey; expiry: string; daysLeft: number }[] {
  const today = Date.parse(`${todayIso.slice(0, 10)}T00:00:00Z`);
  if (Number.isNaN(today)) return [];
  return rows
    .filter((r): r is IdentityRow & { expiry: string } => r.expiry !== null)
    .map((r) => {
      const at = Date.parse(`${r.expiry.slice(0, 10)}T00:00:00Z`);
      return { key: r.key, expiry: r.expiry, daysLeft: Math.round((at - today) / 86_400_000) };
    })
    .filter((r) => !Number.isNaN(r.daysLeft) && r.daysLeft <= withinDays)
    .sort((a, b) => a.daysLeft - b.daysLeft);
}

/**
 * Employment status from the record (EmployeeStatuses: Draft, Invited, Active, Suspended, Offboarded,
 * Archived) → a tone. Anything else is shown neutral, never as "Active".
 */
export function employmentStatusTone(status: string | null | undefined): {
  key: 'active' | 'invited' | 'suspended' | 'offboarded' | 'unknown';
  tone: 'success' | 'info' | 'warning' | 'neutral';
} {
  switch ((status ?? '').trim().toLowerCase()) {
    case 'active': return { key: 'active', tone: 'success' };
    case 'draft':
    case 'invited': return { key: 'invited', tone: 'info' };
    case 'suspended': return { key: 'suspended', tone: 'warning' };
    case 'offboarded':
    case 'archived': return { key: 'offboarded', tone: 'neutral' };
    default: return { key: 'unknown', tone: 'neutral' };
  }
}
