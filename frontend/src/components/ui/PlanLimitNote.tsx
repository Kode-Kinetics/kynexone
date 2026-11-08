'use client';

import { useT } from '../../hooks/useT';

/**
 * Why "Add employee" / "Create user" is switched off at the plan limit. The reason used to live in a hover-only
 * popover, so a keyboard or touch user met a dead button with no explanation. This is always-visible text; the
 * button points at it with aria-describedby (the pattern EmployeeOverview already uses).
 */
export function PlanLimitNote({ id, kind, used, max, className = '' }: { id: string; kind: 'employees' | 'users'; used: number; max: number; className?: string }) {
  const t = useT();
  return (
    <p id={id} data-plan-limit className={`max-w-[16rem] text-meta text-fg-muted ${className}`.trim()}>
      {kind === 'employees'
        ? t('Employee limit reached ({used}/{max}). Upgrade your plan to add more employees.', { used, max })
        : t('User limit reached ({used}/{max}). Upgrade your plan to add more users.', { used, max })}
    </p>
  );
}
