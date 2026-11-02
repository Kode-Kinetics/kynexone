import { msg } from '../i18n/translations';

/**
 * "Used by 12 employees, 3 designations", built here from the server's structured usage ({kind, label, count}[]) so it
 * reads in the reader's language. The server's English sentence stays on the API for other callers. A kind this file
 * does not know falls back to the server's English label rather than hiding the reference.
 */
export interface UsageItem { kind: string; label: string; count: number }

const KIND: Record<string, string> = {
  employee: msg('{count, plural, one {# employee} other {# employees}}'),
  position: msg('{count, plural, one {# position} other {# positions}}'),
  department: msg('{count, plural, one {# department} other {# departments}}'),
  designation: msg('{count, plural, one {# designation} other {# designations}}'),
  location: msg('{count, plural, one {# location} other {# locations}}'),
  branch: msg('{count, plural, one {# branch} other {# branches}}'),
  gradeEntitlement: msg('{count, plural, one {# benefit or loan limit} other {# benefit and loan limits}}'),
  approvalWorkflow: msg('{count, plural, one {# approval workflow} other {# approval workflows}}'),
  departmentStaffingBudget: msg('{count, plural, one {# staffing budget} other {# staffing budgets}}'),
};

type T = (key: string, params?: Record<string, string | number>) => string;

export function usageSentence(items: readonly UsageItem[] | null | undefined, t: T, englishFallback?: string | null): string {
  if (!items || items.length === 0) return englishFallback ?? '';
  const parts = items.map(i => (KIND[i.kind] ? t(KIND[i.kind], { count: i.count }) : `${i.count} ${i.label}`));
  return t('Used by {list}', { list: parts.join(t(', ')) });
}
