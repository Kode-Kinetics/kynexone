import type { FieldIssue } from '@/src/lib/hire/hireValidation';
import { SERVER_CODE_KEYS } from '@/src/lib/hire/problem';

type T = (key: string, params?: Record<string, string>) => string;

/** The sentence for an issue, in the viewer's language; a country in the params is named in that language too. */
export function issueText(issue: Pick<FieldIssue, 'key' | 'params'>, t: T, locale: string): string {
  const params = issue.params ? { ...issue.params } : undefined;
  if (params?.countryCode) {
    try { params.country = new Intl.DisplayNames([locale], { type: 'region' }).of(params.countryCode) ?? params.country; } catch { /* keep the English name */ }
  }
  return t(issue.key, params);
}

export function serverCodeKey(code: string): string {
  return SERVER_CODE_KEYS[code] ?? 'This value is not in a valid format.';
}
