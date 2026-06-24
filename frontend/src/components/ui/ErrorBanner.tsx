'use client';

import { X, XCircle } from 'lucide-react';

export function ErrorBanner({ message, onDismiss }: { message: string; onDismiss: () => void }) {
  return (
    <div
      role="alert"
      className="flex items-start gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700 dark:border-red-800/60 dark:bg-red-900/20 dark:text-red-400"
    >
      <XCircle className="h-4 w-4 mt-0.5 shrink-0" />
      <span className="flex-1">{message}</span>
      <button
        type="button"
        onClick={onDismiss}
        aria-label="Dismiss error"
        className="shrink-0 rounded p-0.5 hover:bg-red-100 dark:hover:bg-red-800/40 transition-colors"
      >
        <X className="h-4 w-4" />
      </button>
    </div>
  );
}
