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

export function reviewItemLabel(item: ReviewItem, t: Translate): string {
  switch (item.key) {
    case 'pay:compensationUnapproved': return t('Pay package awaiting Finance approval');
    case 'pay:bankUnverified': return t('Imported bank details to verify');
    default: return item.label || item.key;
  }
}
