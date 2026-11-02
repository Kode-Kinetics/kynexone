'use client';

import { useEffect } from 'react';
import { attemptChunkReload, isChunkLoadError } from '@/src/lib/chunkReload';
import { captureRenderError } from '@/src/lib/errorTracking';

export default function Error({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  useEffect(() => {
    if (!isChunkLoadError(error)) captureRenderError(error); // no-op unless a DSN is configured
    // Deployment skew: a chunk referenced by the stale page is gone. A single
    // hard reload pulls the new build. Loop-guarded so it can't thrash.
    if (isChunkLoadError(error)) {
      attemptChunkReload();
    }
  }, [error]);

  // While the recovery reload is in flight, render nothing (avoids a flash of
  // the error card right before the page reloads).
  if (isChunkLoadError(error)) return null;

  return (
    <div className="flex min-h-screen flex-col items-center justify-center gap-4 bg-lightBg px-6 text-center dark:bg-midnight">
      <p className="text-2xl font-semibold text-slate-800 dark:text-slate-100">
        Something went wrong
      </p>
      <p className="max-w-md text-sm text-slate-500 dark:text-slate-400">
        An unexpected error occurred. You can try again, or reload the page.
      </p>
      <div className="flex gap-3">
        <button onClick={() => reset()} className="btn-primary text-sm">
          Try again
        </button>
        <button
          onClick={() => window.location.reload()}
          className="btn-secondary text-sm"
        >
          Reload
        </button>
      </div>
    </div>
  );
}
