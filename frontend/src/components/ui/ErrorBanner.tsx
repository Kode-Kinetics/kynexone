'use client';

import { X, XCircle } from 'lucide-react';

export function ErrorBanner({ message, onDismiss }: { message: string; onDismiss: () => void }) {
  return (
    <div
      role="alert"
      className="flex items-start gap-3 rounded-lg border border-danger/30 bg-danger-soft px-4 py-3 text-body text-danger-fg"
    >
      <XCircle className="h-4 w-4 mt-0.5 shrink-0" />
      <span className="flex-1">{message}</span>
      <button
        type="button"
        onClick={onDismiss}
        aria-label="Dismiss error"
        className="shrink-0 rounded p-0.5 transition-colors hover:bg-danger/10"
      >
        <X className="h-4 w-4" />
      </button>
    </div>
  );
}
