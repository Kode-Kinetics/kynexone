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
