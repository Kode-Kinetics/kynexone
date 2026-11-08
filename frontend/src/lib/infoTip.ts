/** Rules of the `i` affordance (Screen Standard 3b) that are not markup: who sees the field key, which text wins, where the popover sits. */

/** Setup (Field help text) is `security.manage`: only its holders can act on a field key, so only they are shown it. */
export const FIELD_KEY_PERMISSION = 'security.manage';

/** The popover is at most this wide (px). */
export const INFO_TIP_MAX_WIDTH = 280;

export function canSeeFieldKey(hasPermission: ((permission: string) => boolean) | null, fieldKey: string | undefined): boolean {
  return !!fieldKey && !!hasPermission && hasPermission(FIELD_KEY_PERMISSION);
}

/**
 * A per-company override (Setup, Field help text) replaces the built-in text and is shown as the company wrote it;
 * the built-in text is a translation key (English is the key) and goes through t().
 */
export function resolveInfoText(text: string, override: string | undefined, t: (key: string) => string): string {
  return override && override.trim() ? override : t(text);
}

/**
 * The popover is anchored to the inline-start edge of the icon and grows toward inline-end. It flips to the other
 * edge when it would leave the viewport: past the right edge in LTR, past the left edge (0) in RTL.
 */
export function flipsAtViewportEdge(rect: { left: number; right: number }, viewportWidth: number, dir: 'ltr' | 'rtl'): boolean {
  return dir === 'rtl' ? rect.left < 0 : rect.right > viewportWidth;
}

/**
 * Where the popover would sit when anchored to the inline-start edge of the icon: grows right from the icon's left in
 * LTR, left from the icon's right in RTL. Computed from the ICON's rect and the popover's width, never from the popover's
 * own rect, so the answer does not depend on whether it is currently drawn flipped (which made it alternate on every
 * second open).
 */
export function popoverEdgesAtInlineStart(anchor: { left: number; right: number }, width: number, dir: 'ltr' | 'rtl'): { left: number; right: number } {
  return dir === 'rtl' ? { left: anchor.right - width, right: anchor.right } : { left: anchor.left, right: anchor.left + width };
}

/** The popover keeps at least this far (px) from either viewport edge. */
export const INFO_TIP_VIEWPORT_MARGIN = 8;

/**
 * Final popover geometry, always fully inside the viewport (with an 8 px margin). The width is capped at the viewport
 * minus both margins; the popover is then anchored to the icon's inline-start edge if it fits there, else to the
 * inline-end edge, else (a narrow screen where neither fits) pushed to the nearest position that is inside.
 * `offset` is the distance, in px along the inline axis, from the icon's inline-start edge to the popover's inline-start
 * edge (negative = further toward inline-start), so the component applies it as `inset-inline-start`. Pure and computed
 * from the icon's rect and the popover's width only, so it is the same on every open, in LTR and RTL.
 */
export function placeInfoTip(
  anchor: { left: number; right: number },
  popoverWidth: number,
  viewportWidth: number,
  dir: 'ltr' | 'rtl',
  margin: number = INFO_TIP_VIEWPORT_MARGIN,
): { width: number; offset: number } {
  const width = Math.max(0, Math.min(popoverWidth, viewportWidth - 2 * margin));
  const min = margin;
  const max = viewportWidth - margin - width; // the largest `left` that still fits
  const fits = (left: number) => left >= min && left <= max;
  // Candidate `left` edges: anchored at the inline-start edge of the icon, then at its inline-end edge.
  const atStart = dir === 'rtl' ? anchor.right - width : anchor.left;
  const atEnd = dir === 'rtl' ? anchor.left : anchor.right - width;
  const left = fits(atStart) ? atStart : fits(atEnd) ? atEnd : Math.min(Math.max(atStart, min), Math.max(min, max));
  const offset = dir === 'rtl' ? anchor.right - (left + width) : left - anchor.left;
  return { width, offset };
}
