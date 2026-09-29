'use client';

import type { LucideIcon } from 'lucide-react';

interface IconButtonProps {
  label: string;
  icon: LucideIcon;
  onClick?: () => void;
  badge?: number;
}

export function IconButton({ label, icon: Icon, onClick, badge }: IconButtonProps) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      onClick={onClick}
      className="relative grid h-10 w-10 place-items-center rounded-lg border border-slate-200 bg-white text-slate-700 shadow-sm transition hover:border-sapphire/40 hover:text-sapphire dark:border-white/10 dark:bg-white/[0.06] dark:text-slate-200 dark:hover:text-cyanAccent"
    >
      <Icon className="h-4 w-4" aria-hidden="true" />
      {badge ? (
        <span className="absolute -end-1 -top-1 grid min-h-4 min-w-4 place-items-center rounded-full bg-sapphire px-1 text-[10px] font-bold text-white">
          {badge}
        </span>
      ) : null}
    </button>
  );
}
