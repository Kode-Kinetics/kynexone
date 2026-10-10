import { useSafeAreaInsets } from 'react-native-safe-area-context';

/** Height of the floating tab-bar capsule. */
export const TAB_BAR_HEIGHT = 62;
/** Breathing room between the last content and the capsule. */
const TAB_BAR_GAP = 10;

/** Distance from the screen bottom to the capsule's bottom edge. */
export function tabBarBottomOffset(bottomInset: number): number {
  return bottomInset > 0 ? Math.max(bottomInset - 12, 10) : 12;
}

/**
 * Points the floating tab bar covers at the bottom of the screen (capsule + offset + gap).
 *
 * The tab shell (MainTabs) already reserves this space for every tab scene, so screens inside the
 * tabs do NOT add it to their scroll padding. Use it only for things laid out against the full
 * window (e.g. an overlay rendered outside the scene).
 */
export function useTabBarInset(): number {
  const insets = useSafeAreaInsets();
  return TAB_BAR_HEIGHT + tabBarBottomOffset(insets.bottom) + TAB_BAR_GAP;
}
