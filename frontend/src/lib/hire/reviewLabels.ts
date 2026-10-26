/**
 * hire-review items arrive as { key, label } with an English-only label. The key is the contract: known keys are
 * rendered through t() (English and Arabic), anything unknown falls back to the server label, then to the key.
 * A bare string (an older mock or response) is treated as a label.
 */
export interface ReviewItem { key: string; label: string }

type Translate = (key: string, params?: Record<string, string | number>) => string;

export function normaliseReviewItems(raw: unknown): ReviewItem[] {
  if (!Array.isArray(raw)) return [];
  return raw.flatMap((r): ReviewItem[] => {
    if (typeof r === 'string') return r ? [{ key: r, label: r }] : [];
    if (r && typeof r === 'object' && typeof (r as ReviewItem).key === 'string') {
      const o = r as Partial<ReviewItem>;
      return [{ key: o.key as string, label: typeof o.label === 'string' && o.label ? o.label : (o.key as string) }];
    }
    return [];
  });
}

/** Known hire-review keys and the sentence each reads as (CD34). Unknown pay:/data: keys get a generic sentence, never the raw key. */
const KEY_SENTENCE: Record<string, string> = {
  'pay:compensationUnapproved': 'Pay package awaiting Finance approval',
  'pay:bankUnverified': 'Imported bank details to verify',
  'pay:ibanMissing': 'The IBAN is missing',
  'pay:salaryMissing': 'The salary has not been entered',
  'pay:molIdMissing': 'The MOL ID is missing',
  'pay:payrollProfileMissing': 'The payroll details have not been entered',
  'data:joining_backdated': 'The start date is more than 30 days in the past.',
};

export function reviewItemLabel(item: ReviewItem, t: Translate): string {
  const known = KEY_SENTENCE[item.key];
  if (known) return t(known);
  if (item.key.startsWith('pay:')) return t('Some pay details need attention');
  if (item.key.startsWith('data:')) return t('Some details need review');
  return item.label || item.key;
}
