'use client';

import { useT } from '@/src/hooks/useT';
import { requestFailureReason } from '@/src/lib/requestFailure';

/**
 * What a list shows when its load FAILED — in place of its "No … yet" empty state.
 *
 * An empty state after a failed load is a false statement ("there are no pending approvals") made
 * on no evidence. This says the list could not be loaded, why (the server's own reason when it
 * gave one), and offers a Retry.
 */
function LoadFailedBody({ error, onRetry }: { error?: unknown; onRetry?: () => void }) {
  const t = useT();
  const reason = error === undefined ? null : requestFailureReason(error);
  return (
    <>
      <p>{t('This list could not be loaded, so nothing is shown rather than an incomplete list.')}</p>
      {reason && <p className="mt-1 text-slate-500 dark:text-slate-400">{reason}</p>}
      {onRetry && (
        <button type="button" className="btn-secondary mt-3 h-8 px-3 text-sm" onClick={onRetry}>
          {t('Retry')}
        </button>
      )}
    </>
  );
}

/** For a list rendered as a table: one full-width row. */
export function LoadFailedRow({ colSpan, error, onRetry }: { colSpan: number; error?: unknown; onRetry?: () => void }) {
  return (
    <tr>
      <td colSpan={colSpan} role="alert" className="py-10 text-center text-sm text-rose-600 dark:text-rose-400" data-testid="list-load-failed">
        <LoadFailedBody error={error} onRetry={onRetry} />
      </td>
    </tr>
  );
}

/** For a list rendered as cards or a div list. */
export function LoadFailedNotice({ error, onRetry }: { error?: unknown; onRetry?: () => void }) {
  return (
    <div role="alert" className="px-4 py-10 text-center text-sm text-rose-600 dark:text-rose-400" data-testid="list-load-failed">
      <LoadFailedBody error={error} onRetry={onRetry} />
    </div>
  );
}
