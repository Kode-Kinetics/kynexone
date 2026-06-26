'use client';

import { useEffect } from 'react';
import {
  attemptChunkReload,
  clearChunkReloadGuard,
  isChunkLoadError,
} from '@/src/lib/chunkReload';

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
    // We mounted successfully — this build's chunks loaded fine. Reset the guard
    // so a future deploy can recover again.
    clearChunkReloadGuard();

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
