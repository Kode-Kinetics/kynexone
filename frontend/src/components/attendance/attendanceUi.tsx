'use client';

import type { ReactNode } from 'react';
import { AlertTriangle } from 'lucide-react';

// The small pieces the Attendance workspace and the attendance devices section of Setup both draw with.

export const dateTime = (value?: string) => value ? new Date(value).toLocaleString() : '-';

export const statusTone = (status: string): 'emerald' | 'rose' | 'amber' | 'blue' | 'slate' => {
  if (status === 'Present' || status === 'Approved' || status === 'Completed' || status === 'Success') return 'emerald';
  if (status === 'Absent' || status === 'Rejected' || status === 'Failed') return 'rose';
  if (status === 'Late' || status === 'Half day' || status.startsWith('Pending')) return 'amber';
  if (status === 'Processed') return 'blue';
  return 'slate';
};

export function DomainUnavailable({ message }: { message: string }) {
  return (
    <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-3 text-sm text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200">
      <span className="inline-flex items-center gap-2"><AlertTriangle className="h-4 w-4 shrink-0" aria-hidden="true" />{message}</span>
    </div>
  );
}

export function Panel({ title, action, children }: { title: string; action?: string; children: ReactNode }) {
  return (
    <div className="surface overflow-hidden">
      <div className="flex items-center justify-between border-b border-slate-100 px-4 py-3 dark:border-white/[0.07]">
        <h2 className="text-sm font-bold text-slate-900 dark:text-white">{title}</h2>
        {action && <span className="text-xs font-semibold text-slate-400 dark:text-slate-500">{action}</span>}
      </div>
      <div className="p-4">{children}</div>
    </div>
  );
}

export function Empty({ text }: { text: string }) {
  return <div className="rounded-lg border border-dashed border-slate-200 p-6 text-center text-sm text-slate-400 dark:border-white/10 dark:text-slate-500">{text}</div>;
}
