'use client';

import { ArrowRight } from 'lucide-react';
import type { ReactNode } from 'react';

interface DataPanelProps {
  title: string;
  description?: string;
  children: ReactNode;
  action?: ReactNode;
  viewAll?: boolean;
  onViewAll?: () => void;
  className?: string;
}

export function DataPanel({
  title,
  description,
  children,
  action,
  viewAll,
  onViewAll,
  className = '',
}: DataPanelProps) {
  return (
    <section className={`surface p-5 ${className}`}>
      <div className="mb-4 flex items-start justify-between gap-4">
        <div className="min-w-0">
          <h2 className="text-body font-semibold text-fg">{title}</h2>
          {description ? (
            <p className="mt-0.5 text-meta text-fg-muted">{description}</p>
          ) : null}
        </div>
        {action ? (
          <div className="shrink-0">{action}</div>
        ) : viewAll ? (
          <button
            type="button"
            onClick={onViewAll}
            className="flex shrink-0 items-center gap-1 text-meta font-semibold text-primary-fg hover:underline"
          >
            View all
            <ArrowRight className="h-3 w-3" />
          </button>
        ) : null}
      </div>
      {children}
    </section>
  );
}
