/**
 * A backend status/enum value, in the viewer's language.
 *
 * WHY. Pages rendered raw values ("PendingManagerApproval") or split them on capitals
 * ("Pending Manager Approval"), each with its own copy of the regex. Neither can be translated,
 * and an Arabic user saw English statuses on every list. Values are now dictionary keys:
 *
 *   enum.<EnumName>.<Value>   e.g. enum.LeaveRequestStatus.PendingManagerApproval
 *   enum.Status.<Value>       shared fallback for values every workflow uses (Approved, Draft…)
 *
 * and an unknown value still renders readably (split on capitals) instead of disappearing, so a
 * new backend status degrades to English rather than to a blank badge. Values with spaces (the
 * attendance service writes "Half day") are normalised to PascalCase before the lookup.
 */

import type { MessageParams } from './translations';

type Translator = (key: string, params?: MessageParams) => string;

/** The enum families with keys in i18n/translations.ts. Add the name here when you add keys. */
export type EnumName =
  | 'Status'
  | 'LeaveRequestStatus'
  | 'LoanStatus'
  | 'PayrollRunStatus'
  | 'ApprovalStatus'
  | 'HrRequestStatus'
  | 'OvertimeStatus'
  | 'AttendanceStatus'
  | 'NitaqatBand';

/** "Half day" → "HalfDay", "pending" → "Pending", "PendingHRApproval" unchanged. */
export function normaliseEnumValue(value: string): string {
  const joined = value.trim().replace(/[^A-Za-z0-9]+([A-Za-z0-9])?/g, (_, c: string | undefined) => (c ? c.toUpperCase() : ''));
  return joined.charAt(0).toUpperCase() + joined.slice(1);
}

/** "PendingFinanceReview" → "Pending finance review" — the fallback for a value with no key. */
export function humaniseEnumValue(value: string): string {
  const spaced = value.trim().replace(/[._-]+/g, ' ').replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2');
  const lower = spaced.replace(/\b([A-Z])([a-z]+)/g, (_, a: string, b: string) => a.toLowerCase() + b);
  return lower.charAt(0).toUpperCase() + lower.slice(1);
}

export function enumKey(enumName: EnumName, value: string): string {
  return `enum.${enumName}.${normaliseEnumValue(value)}`;
}

/** Translate an enum value; falls back to the shared `Status` family, then to readable English. */
export function enumLabel(t: Translator, enumName: EnumName, value: string | null | undefined): string {
  if (value == null || value.trim() === '') return '—';
  const v = normaliseEnumValue(value);
  for (const key of [`enum.${enumName}.${v}`, `enum.Status.${v}`]) {
    const label = t(key);
    if (label !== key) return label;
  }
  return humaniseEnumValue(value);
}

