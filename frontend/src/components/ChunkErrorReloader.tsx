'use client';

import { useEffect } from 'react';
import { attemptChunkReload, isChunkLoadError } from '@/src/lib/chunkReload';

/**
 * Catches chunk-load failures that happen OUTSIDE React's render tree — e.g. a
 * lazy `import()` during client-side navigation, or a failed dynamic chunk after
 * a deploy. React error boundaries don't see these, so we listen at the window
 * level and force a one-time recovery reload (loop-guarded in chunkReload).
 *
 * Renders nothing.
 */
export function ChunkErrorReloader() {
  useEffect(() => {
    // NOTE: do NOT clear the reload guard on mount. Doing so re-armed the
    // reloader on every page load, so a recurring chunk error looped forever.
    // The once-per-session guard must persist for the whole tab session.
    const onError = (event: ErrorEvent) => {
      if (isChunkLoadError(event.error) || isChunkLoadError(event.message)) {
        attemptChunkReload();
      }
    };

    const onRejection = (event: PromiseRejectionEvent) => {
      if (isChunkLoadError(event.reason)) {
        attemptChunkReload();
      }
    };

    window.addEventListener('error', onError);
    window.addEventListener('unhandledrejection', onRejection);
    return () => {
      window.removeEventListener('error', onError);
      window.removeEventListener('unhandledrejection', onRejection);
    };
  }, []);

  return null;
}
