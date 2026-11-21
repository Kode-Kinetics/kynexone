/**
 * The one formatting layer of the employee record (Screen Standard): what a stored value, a system event name or a field list looks
 * like to a person. No raw ISO timestamp, PascalCase event name or camelCase field name is ever shown; Arabic reads the same
 * way as English (Latin digits, the tenant's calendar) because everything goes through the shared formatter and the dictionary.
 *
 * Pure functions: the viewer's formatter and translator come in as arguments, so they are unit-testable without a browser.
 */
import type { DateInput, Formatter } from './format';
import type { MessageParams } from '../i18n/translations';

type Translate = (key: string, params?: MessageParams) => string;
type DateFormatter = Pick<Formatter, 'date' | 'dateTime'>;

const ISO_INSTANT = /\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/;
/** A day, or midnight of a day (what a date column looks like once it has crossed the API as an instant). */
const DAY_OR_MIDNIGHT = /^(\d{4}-\d{2}-\d{2})(?:T00:00(?::00(?:\.0+)?)?(?:Z|[+-]00:?00)?)?$/;
const NO_ZONE = /^\d{4}-\d{2}-\d{2}T[\d:.]+$/;

/** True when the text still has the raw timestamp shape (2026-11-13T22:33...). */
export function looksLikeIso(text: string): boolean {
  return ISO_INSTANT.test(text);
}

/** A calendar date, "31 Oct 2026": a plain day or a midnight instant is the day itself, whatever zone the viewer is in. */
export function recordDate(fx: DateFormatter, value: DateInput): string {
  const raw = typeof value === 'string' ? value.trim() : value;
  if (raw == null || raw === '') return '—';
  if (typeof raw === 'string') {
    const day = DAY_OR_MIDNIGHT.exec(raw);
    if (day) return fx.date(day[1]);
    const out = fx.date(NO_ZONE.test(raw) ? `${raw}Z` : raw);
    return out === '—' ? raw : out;
  }
  return fx.date(raw);
}

/** A moment, "14 Nov 2026, 01:33", in the tenant's zone. The API's timestamps are UTC even when they arrive without the Z. */
export function recordDateTime(fx: DateFormatter, value: DateInput): string {
  const raw = typeof value === 'string' ? value.trim() : value;
  if (raw == null || raw === '') return '—';
  if (typeof raw === 'string') {
    const out = fx.dateTime(NO_ZONE.test(raw) ? `${raw}Z` : raw);
    return out === '—' ? raw : out;
  }
  return fx.dateTime(raw);
}

const ACRONYMS = new Set(['id', 'iban', 'wps', 'gosi', 'qid', 'vat', 'hr', 'ssn', 'url', 'pdf', 'cv', 'otp', 'sms', 'kpi', 'eosb', 'bic', 'swift', 'ksa', 'uae', 'gl']);

/** "bankName" / "SensitiveChangeApproved" / "payroll.wps.generated" -> "Bank name" / "Sensitive change approved" / "Payroll WPS generated". */
function words(name: string): string {
  const spaced = name.trim().replace(/[._-]+/g, ' ').replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2');
  const parts = spaced.split(/\s+/).filter(Boolean).map(w => (ACRONYMS.has(w.toLowerCase()) ? w.toUpperCase() : w.toLowerCase()));
  if (parts.length === 0) return '';
  if (!ACRONYMS.has(parts[0].toLowerCase())) parts[0] = parts[0].charAt(0).toUpperCase() + parts[0].slice(1);
  return parts.join(' ');
}

/** The dictionary's word for a code when it has one (`enum.RecordEvent.<code>` / `enum.RecordField.<code>`), else the readable English. */
function named(t: Translate, kind: 'event' | 'field', code: string): string {
  const key = `enum.${kind === 'event' ? 'RecordEvent' : 'RecordField'}.${code.toLowerCase().replace(/[^a-z0-9]/g, '')}`;
  const known = t(key);
  if (known !== key) return known;
  const plain = words(code);
  const translated = t(plain);
  return translated;
}

/** A system event name as words: "SensitiveChangeApproved" -> "Sensitive change approved". */
export function humaniseEventName(t: Translate, name: string | null | undefined): string {
  const code = (name ?? '').trim();
  if (!code) return '—';
  return named(t, 'event', code);
}

/** A list of stored field names as words: "bankName,accountNumber" -> "Bank name, account number". */
export function humaniseFieldList(t: Translate, list: string | null | undefined): string {
  const names = (list ?? '').split(/[,;]/).map(n => n.trim()).filter(Boolean);
  if (names.length === 0) return '';
  // After the first name the list reads as one sentence: lower-case the initial unless it starts an acronym (ID number, IBAN).
  const lowered = (text: string) => (/^[A-Z][a-z]/.test(text) ? text.charAt(0).toLowerCase() + text.slice(1) : text);
  return names.map((n, i) => (i === 0 ? named(t, 'field', n) : lowered(named(t, 'field', n)))).join(`${t('List separator')} `);
}

export interface ChangeRow {
  eventType?: string | null;
  fieldName?: string | null;
  oldValue?: string | null;
  newValue?: string | null;
}

function shownValue(fx: DateFormatter, value: string | null | undefined): string {
  const text = (value ?? '').trim();
  if (!text) return '';
  if (!looksLikeIso(text) && !/^\d{4}-\d{2}-\d{2}$/.test(text)) return text;
  return DAY_OR_MIDNIGHT.test(text) ? recordDate(fx, text) : recordDateTime(fx, text);
}

/**
 * One history row as a field name and a short detail. A change with nothing on either side (the server withholds sensitive values)
 * says "Details withheld" rather than showing an empty arrow.
 */
export function describeChange(t: Translate, fx: DateFormatter, row: ChangeRow): { field: string; detail: string } {
  const field = humaniseFieldList(t, row.fieldName);
  const before = shownValue(fx, row.oldValue);
  const after = shownValue(fx, row.newValue);
  if (!before && !after) return { field, detail: t('Details withheld') };
  return { field, detail: t('{old} → {new}', { old: before || '—', new: after || '—' }) };
}
