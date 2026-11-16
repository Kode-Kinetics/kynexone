'use client';

import { useCallback, useLayoutEffect, useRef, useState } from 'react';
import type { CSSProperties } from 'react';

/**
 * Fits an element to whatever is left of the viewport below it, so a screen can be exactly one
 * page tall and never scroll the document.
 *
 * It measures instead of assuming a topbar height: the element's own offset from the top of the
 * page (banners, return links and the topbar all count), minus `bottomGap`. The enclosing
 * <main> reserves bottom padding for the floating assistant button; a negative bottom margin
 * hands that padding back, so the document is exactly one viewport tall and the button floats
 * over the gap this leaves rather than over content.
 *
 * `height` is null until measured (and while disabled); `minHeight` stops the frame collapsing in
 * a very short window, where the page falls back to scrolling instead of clipping.
 */
export function useStageFrame(enabled: boolean, { bottomGap = 24, minHeight = 560 }: { bottomGap?: number; minHeight?: number } = {}) {
  const ref = useRef<HTMLDivElement>(null);
  const [height, setHeight] = useState<number | null>(null);
  const [reclaim, setReclaim] = useState(0);

  const measure = useCallback(() => {
    const el = ref.current;
    if (!el) return;
    const top = el.getBoundingClientRect().top + window.scrollY;
    const main = el.closest('main');
    const pad = main ? parseFloat(getComputedStyle(main).paddingBottom) || 0 : 0;
    setHeight(Math.max(minHeight, Math.round(window.innerHeight - top - bottomGap)));
    setReclaim(Math.max(0, pad - bottomGap));
  }, [bottomGap, minHeight]);

  useLayoutEffect(() => {
    if (!enabled) { setHeight(null); setReclaim(0); return; }
    measure();
    // Content above the frame (a banner) can appear after first paint; measure once more then.
    const raf = requestAnimationFrame(measure);
    window.addEventListener('resize', measure);
    return () => { cancelAnimationFrame(raf); window.removeEventListener('resize', measure); };
  }, [enabled, measure]);

  const style: CSSProperties | undefined = enabled && height != null
    ? { height, marginBottom: -reclaim }
    : undefined;

  return { ref, style, height };
}
