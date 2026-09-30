'use client';

import { pageWindowText } from '../lib/paging';

/**
 * "Showing 100 of 1,234 requests · Load more" under a list that is one page of a longer one.
 * Renders nothing when every row is on screen, so a complete list carries no footer.
 */
export function ListWindowFooter({ shown, total, noun, loadingMore, onLoadMore }: {
  shown: number;
  total: number | null;
  noun: string;
  loadingMore: boolean;
  onLoadMore: () => void;
}) {
  const text = pageWindowText(shown, total, noun);
  if (!text) return null;
  return (
    <div className="flex flex-wrap items-center justify-center gap-3 border-t border-slate-100 px-4 py-3 text-xs text-slate-500 dark:border-white/[0.07] dark:text-slate-400">
      <span role="status">{text}</span>
      <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={loadingMore} onClick={onLoadMore}>
        {loadingMore ? 'Loading…' : 'Load more'}
      </button>
    </div>
  );
}
