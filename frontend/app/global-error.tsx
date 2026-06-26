'use client';

import { useEffect } from 'react';
import { attemptChunkReload, isChunkLoadError } from '@/src/lib/chunkReload';

/**
 * Root-level error boundary. Replaces the root layout when an error escapes it,
 * so it must render its own <html>/<body> and cannot rely on app CSS being
 * loaded — styles are inline.
 *
 * Primary job: recover from deployment skew (stale chunk after a new deploy) by
 * forcing a single hard reload.
 */
export default function GlobalError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  useEffect(() => {
    if (isChunkLoadError(error)) {
      attemptChunkReload();
    }
  }, [error]);

  const chunk = isChunkLoadError(error);

  return (
    <html lang="en">
      <body
        style={{
          margin: 0,
          minHeight: '100vh',
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          justifyContent: 'center',
          gap: 16,
          background: '#0B1020',
          color: '#e2e8f0',
          fontFamily:
            'Inter, system-ui, -apple-system, Segoe UI, Roboto, sans-serif',
          textAlign: 'center',
          padding: 24,
        }}
      >
        {/* During a recovery reload, keep it minimal — the page is about to refresh. */}
        {chunk ? (
          <p style={{ fontSize: 14, color: '#94a3b8' }}>Updating to the latest version…</p>
        ) : (
          <>
            <p style={{ fontSize: 24, fontWeight: 800, margin: 0 }}>
              Something went wrong
            </p>
            <p style={{ fontSize: 14, color: '#94a3b8', maxWidth: 420, margin: 0 }}>
              An unexpected error occurred. Please reload the page.
            </p>
            <div style={{ display: 'flex', gap: 12, marginTop: 8 }}>
              <button
                onClick={() => reset()}
                style={{
                  padding: '8px 16px',
                  borderRadius: 8,
                  border: 'none',
                  background: '#3b82f6',
                  color: '#fff',
                  fontSize: 14,
                  cursor: 'pointer',
                }}
              >
                Try again
              </button>
              <button
                onClick={() => window.location.reload()}
                style={{
                  padding: '8px 16px',
                  borderRadius: 8,
                  border: '1px solid #334155',
                  background: 'transparent',
                  color: '#e2e8f0',
                  fontSize: 14,
                  cursor: 'pointer',
                }}
              >
                Reload
              </button>
            </div>
          </>
        )}
      </body>
    </html>
  );
}
