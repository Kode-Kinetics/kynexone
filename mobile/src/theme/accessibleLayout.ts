export const ACCESSIBLE_LAYOUT = {
  minimumTouchTarget: 44,
  horizontalInset: 16,
  gap: 12,
  minimumCardWidth: 156,
  stackedFontScale: 1.3,
} as const;

/** Width includes the screen's standard horizontal insets. */
export function columnsFor(width: number, fontScale: number, minimumCardWidth: number): 1 | 2 {
  const scale = Number.isFinite(fontScale) ? Math.max(1, fontScale) : 1;
  const availableWidth = width - ACCESSIBLE_LAYOUT.horizontalInset * 2;

  return scale < ACCESSIBLE_LAYOUT.stackedFontScale
    && availableWidth >= Math.max(1, minimumCardWidth) * scale * 2 + ACCESSIBLE_LAYOUT.gap
    ? 2
    : 1;
}

export function resolveAccessibleLayout(width: number, fontScale: number, isRTL: boolean) {
  const columns = columnsFor(width, fontScale, ACCESSIBLE_LAYOUT.minimumCardWidth);

  return {
    width,
    fontScale,
    isRTL,
    columns,
    shouldStack: columns === 1,
    // Yoga already applies native layout direction. Reversing the row here mirrors RTL twice.
    rowDirection: 'row' as const,
    textAlign: isRTL ? 'right' as const : 'left' as const,
    minimumTouchTarget: ACCESSIBLE_LAYOUT.minimumTouchTarget,
  };
}
