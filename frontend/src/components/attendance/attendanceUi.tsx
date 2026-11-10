'use client';

import type { ReactNode } from 'react';
import { Activity, AlertTriangle } from 'lucide-react';

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
    <div className="rounded-lg border border-warning/30 bg-warning-soft px-3 py-3 text-body text-warning-fg">
      <span className="inline-flex items-center gap-2"><AlertTriangle className="h-4 w-4 shrink-0" aria-hidden="true" />{message}</span>
    </div>
  );
}

export function Panel({ title, action, children }: { title: string; action?: string; children: ReactNode }) {
  return (
    <div className="surface overflow-hidden">
      <div className="flex items-center justify-between border-b border-line px-4 py-3">
        <h2 className="text-body font-semibold text-fg">{title}</h2>
        {action && <span className="text-meta font-semibold text-fg-subtle">{action}</span>}
      </div>
      <div className="p-4">{children}</div>
    </div>
  );
}

export function Empty({ text }: { text: string }) {
  return <div className="rounded-lg border border-dashed border-line p-6 text-center text-body text-fg-subtle">{text}</div>;
}

export function SectionTitle({ icon: Icon, title, subtitle }: { icon: typeof Activity; title: string; subtitle: string }) {
  return (
    <div className="flex gap-3">
      <span className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-sapphire/10 text-sapphire"><Icon className="h-4 w-4" aria-hidden="true" /></span>
      <div className="min-w-0">
        <h2 className="text-body font-semibold text-fg">{title}</h2>
        <p className="mt-0.5 text-meta text-fg-muted">{subtitle}</p>
      </div>
    </div>
  );
}
