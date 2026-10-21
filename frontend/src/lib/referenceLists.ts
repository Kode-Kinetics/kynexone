import type { MasterDataValue } from '../api/setup';

/**
 * Employee reference values (Setup → Reference lists): gender, marital status, employment type, contract type.
 *
 * The employee forms store a value's CODE (`MALE`) and show its label in the reader's language. Rows saved before
 * that hold the English label (`Male`); nothing rewrites them, so every reader maps a stored value to its code
 * first — case-insensitively, by code, then English label, then Arabic label. A legacy row and a code row therefore
 * read the same, and renaming a label in Setup never orphans a row (codes cannot be edited).
 * The backend reads stored values the same way (Infrastructure/Setup/ReferenceList.cs).
 */
export type ReferenceValue = Pick<MasterDataValue, 'code' | 'valueEn' | 'valueAr'> & Partial<Pick<MasterDataValue, 'isActive' | 'isDefault' | 'sortOrder'>>;

export interface ReferenceOption {
  value: string;
  label: string;
}

const same = (left: string | undefined, right: string) => !!left && left.trim().toLowerCase() === right.toLowerCase();

/** The value a stored string stands for: code first, then English label, then Arabic label. */
export function findReferenceValue<T extends ReferenceValue>(values: readonly T[], stored?: string | null): T | undefined {
  const key = stored?.trim();
  if (!key) return undefined;
  return values.find((value) => same(value.code, key))
    ?? values.find((value) => same(value.valueEn, key))
    ?? values.find((value) => same(value.valueAr, key));
}

/** The code a stored value stands for; an unknown value comes back as typed (trimmed), blank stays blank. */
export function referenceCode(values: readonly ReferenceValue[], stored?: string | null): string {
  return findReferenceValue(values, stored)?.code.trim() ?? stored?.trim() ?? '';
}

/** The label to show for a stored value in the reader's language (Arabic when there is one), else the value as typed. */
export function referenceLabel(values: readonly ReferenceValue[], stored: string | null | undefined, locale: string): string {
  const value = findReferenceValue(values, stored);
  if (!value) return stored?.trim() ?? '';
  const arabic = locale.toLowerCase().startsWith('ar') ? value.valueAr?.trim() : '';
  return arabic || value.valueEn?.trim() || value.code.trim();
}

/** Whether two stored values mean the same reference value (a code and its label compare equal). */
export function sameReferenceValue(values: readonly ReferenceValue[], left?: string | null, right?: string | null): boolean {
  return referenceCode(values, left).toLowerCase() === referenceCode(values, right).toLowerCase();
}

function activeValues<T extends ReferenceValue>(values: readonly T[]): T[] {
  return values
    .filter((value) => value.isActive !== false && value.code.trim().length > 0)
    .sort((left, right) => (left.sortOrder ?? 0) - (right.sortOrder ?? 0))
    .filter((value, index, all) => all.findIndex((candidate) => same(candidate.code, value.code.trim())) === index);
}

/**
 * The choices a form offers: the active values (code stored, label shown). With no list (Setup unreachable or empty)
 * the built-in `fallback` labels are offered as they always were. A current value that is not offered — retired, or
 * a legacy value the list does not know — is kept as the first choice so opening a form never silently changes it.
 */
export function referenceOptions(
  values: readonly ReferenceValue[],
  locale: string,
  fallback: readonly string[] = [],
  current?: string | null,
): ReferenceOption[] {
  const active = activeValues(values);
  const options = active.length
    ? active.map((value) => ({ value: value.code.trim(), label: referenceLabel(values, value.code, locale) }))
    : fallback.map((value) => ({ value, label: value }));
  const selected = referenceCode(values, current);
  if (selected && !options.some((option) => same(option.value, selected))) {
    return [{ value: selected, label: referenceLabel(values, current, locale) }, ...options];
  }
  return options;
}

/** The code a new record starts with: the list's default value, else `fallback` read through the list. */
export function referenceDefault(values: readonly ReferenceValue[], fallback = ''): string {
  const flagged = activeValues(values).find((value) => value.isDefault);
  return flagged ? flagged.code.trim() : referenceCode(values, fallback);
}
