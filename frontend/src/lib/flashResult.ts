/**
 * A result that must survive a navigation. After a save that moves the user to another screen, the destination
 * shows the ResultBanner (what changed, where you are, one next action); this carries it across the route change.
 * The flash is read once (taken and cleared), so a reload does not repeat it; it expires after a minute, is shown only on
 * the page it was left for, and is cleared on sign-out. It is capped so a bad value in session storage can never put an
 * unbounded string on screen.
 */
export const FLASH_RESULT_KEY = 'kynexone-flash-result';
const MAX_TEXT = 300;

export interface FlashResult {
  changed: string;
  where?: string;
  pending?: string;
  next?: { label: string; href: string };
}

/** A flash older than this is stale: it was meant for the very next screen, not a later visit. */
export const FLASH_TTL_MS = 60_000;

type StorageLike = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;

const text = (v: unknown): string | undefined => (typeof v === 'string' && v.trim() ? v.trim().slice(0, MAX_TEXT) : undefined);
// An internal path only: a flash must never become an open redirect.
const internalHref = (v: unknown): string | undefined => (typeof v === 'string' && /^\/(?!\/)[^\s]*$/.test(v) ? v.slice(0, 500) : undefined);
/** `/people/12/edit/?x=1#y` -> `/people/12/edit`. */
const pathOf = (href: string) => (href.split(/[?#]/)[0].replace(/\/+$/, '') || '/');

export function parseFlashResult(raw: string | null): FlashResult | null {
  return parseStored(raw)?.flash ?? null;
}

function parseStored(raw: string | null): { flash: FlashResult; expectedPath?: string; at?: number } | null {
  if (!raw) return null;
  try {
    const data = JSON.parse(raw) as Record<string, unknown>;
    const changed = text(data.changed);
    if (!changed) return null;
    const next = data.next && typeof data.next === 'object' ? data.next as Record<string, unknown> : null;
    const label = next ? text(next.label) : undefined;
    const href = next ? internalHref(next.href) : undefined;
    return {
      flash: { changed, where: text(data.where), pending: text(data.pending), next: label && href ? { label, href } : undefined },
      expectedPath: internalHref(data.expectedPath),
      at: typeof data.at === 'number' && Number.isFinite(data.at) ? data.at : undefined,
    };
  } catch {
    return null;
  }
}

/** Leave `flash` for the screen at `expectedPath`; it is shown there and nowhere else, for FLASH_TTL_MS. */
export function setFlashResult(storage: StorageLike, flash: FlashResult, expectedPath: string, now: number = Date.now()): void {
  try { storage.setItem(FLASH_RESULT_KEY, JSON.stringify({ ...flash, expectedPath: pathOf(expectedPath), at: now })); } catch { /* private mode: the destination simply shows no banner */ }
}

/**
 * Read it for the page the user is on. Shown only when it carries both a timestamp and a page, is no older than the TTL
 * and the page matches; it is cleared when it is used or found stale or malformed. On the wrong page it is left (a redirect
 * hop may still be in flight) until the TTL passes.
 */
export function takeFlashResult(storage: StorageLike, currentPath: string, now: number = Date.now()): FlashResult | null {
  try {
    const stored = parseStored(storage.getItem(FLASH_RESULT_KEY));
    if (!stored || stored.at === undefined || !stored.expectedPath || now - stored.at > FLASH_TTL_MS) { storage.removeItem(FLASH_RESULT_KEY); return null; }
    if (pathOf(stored.expectedPath) !== pathOf(currentPath)) return null;
    storage.removeItem(FLASH_RESULT_KEY);
    return stored.flash;
  } catch {
    return null;
  }
}

export function clearFlashResult(storage: Pick<Storage, 'removeItem'>): void {
  try { storage.removeItem(FLASH_RESULT_KEY); } catch { /* nothing to clear */ }
}
