/**
 * A result that must survive a navigation. After a save that moves the user to another screen, the destination
 * shows the ResultBanner (what changed, where you are, one next action); this carries it across the route change.
 * The flash is read once (taken and cleared), so a reload does not repeat it, and it is capped so a bad value in
 * session storage can never put an unbounded string on screen.
 */
export const FLASH_RESULT_KEY = 'kynexone-flash-result';
const MAX_TEXT = 300;

export interface FlashResult {
  changed: string;
  where?: string;
  pending?: string;
  next?: { label: string; href: string };
}

type StorageLike = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;

const text = (v: unknown): string | undefined => (typeof v === 'string' && v.trim() ? v.trim().slice(0, MAX_TEXT) : undefined);
// An internal path only: a flash must never become an open redirect.
const internalHref = (v: unknown): string | undefined => (typeof v === 'string' && /^\/(?!\/)[^\s]*$/.test(v) ? v.slice(0, 500) : undefined);

export function parseFlashResult(raw: string | null): FlashResult | null {
  if (!raw) return null;
  try {
    const data = JSON.parse(raw) as Record<string, unknown>;
    const changed = text(data.changed);
    if (!changed) return null;
    const next = data.next && typeof data.next === 'object' ? data.next as Record<string, unknown> : null;
    const label = next ? text(next.label) : undefined;
    const href = next ? internalHref(next.href) : undefined;
    return {
      changed,
      where: text(data.where),
      pending: text(data.pending),
      next: label && href ? { label, href } : undefined,
    };
  } catch {
    return null;
  }
}

export function setFlashResult(storage: StorageLike, flash: FlashResult): void {
  try { storage.setItem(FLASH_RESULT_KEY, JSON.stringify(flash)); } catch { /* private mode: the destination simply shows no banner */ }
}

/** Read and clear. */
export function takeFlashResult(storage: StorageLike): FlashResult | null {
  let raw: string | null = null;
  try { raw = storage.getItem(FLASH_RESULT_KEY); storage.removeItem(FLASH_RESULT_KEY); } catch { return null; }
  return parseFlashResult(raw);
}
