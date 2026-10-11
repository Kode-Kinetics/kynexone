'use client';

import { AlertTriangle, FlaskConical } from 'lucide-react';
import type { Blocker, CycleStatus, WorksheetStatus } from '../../api/compensation';
import { Badge, type Tone, type Translator } from '../gl/glUi';

/** Plain-language labels for the server's enum values. The raw value is never shown. */
export function cycleStatusLabel(t: Translator, status: CycleStatus | string): string {
  switch (status) {
    case 'Draft': return t('Draft');
    case 'Planning': return t('Planning');
    case 'Review': return t('In review');
    case 'Approved': return t('Approved');
    case 'Applied': return t('Awards scheduled');
    case 'Cancelled': return t('Cancelled');
    default: return t('Unknown');
  }
}

export function cycleStatusTone(status: string): Tone {
  return status === 'Applied' || status === 'Approved' ? 'emerald' : status === 'Cancelled' ? 'slate' : status === 'Review' ? 'violet' : status === 'Planning' ? 'sapphire' : 'amber';
}

export function worksheetStatusLabel(t: Translator, status: WorksheetStatus | string): string {
  switch (status) {
    case 'Draft': return t('Draft');
    case 'Submitted': return t('Waiting for approval');
    case 'Returned': return t('Returned for changes');
    case 'Approved': return t('Approved');
    case 'Cancelled': return t('Cancelled');
    default: return t('Unknown');
  }
}

export function worksheetStatusTone(status: string): Tone {
  return status === 'Approved' ? 'emerald' : status === 'Returned' ? 'rose' : status === 'Submitted' ? 'violet' : 'slate';
}

/** What happened to an award on its way to pay of record (EmployeeChangeRequest status). */
export function changeStatusLabel(t: Translator, status: string | null): string {
  switch (status) {
    case null: case undefined: return t('Not scheduled');
    case 'ApprovedPendingEffectiveDate': return t('Scheduled for the effective date');
    case 'ApprovedApplied': return t('In pay of record');
    case 'PendingApproval': return t('Back in Approvals for review');
    case 'Cancelled': return t('Withdrawn');
    case 'Expired': return t('Expired');
    case 'Rejected': return t('Rejected');
    default: return t('Unknown');
  }
}

export function changeStatusTone(status: string | null): Tone {
  return status === 'ApprovedApplied' ? 'emerald' : status === 'ApprovedPendingEffectiveDate' ? 'sapphire' : status === 'PendingApproval' ? 'amber' : status ? 'slate' : 'rose';
}

export function flagLabel(t: Translator, code: string): string {
  switch (code) {
    case 'above_range_max': return t('Above the grade maximum');
    case 'below_range_min': return t('Below the grade minimum');
    case 'no_grade_range': return t('No grade range');
    case 'missing_benchmark': return t('No market data mapped');
    case 'stale_benchmark': return t('Market data older than 18 months');
    case 'out_of_scope_benchmark': return t('Market data out of scope');
    case 'fx_missing': return t('Exchange rate missing');
    case 'no_salary_row': return t('No salary record');
    case 'contract_bound_pay': return t('Pay bound to a contract');
    case 'pending_salary_change': return t('Another salary change is pending');
    case 'later_salary_row': return t('A later salary change exists');
    case 'out_of_guideline': return t('Outside the guideline');
    case 'prorated': return t('Prorated');
    default: return t('Check this line');
  }
}

export function flagTone(code: string): Tone {
  return ['above_range_max', 'below_range_min', 'fx_missing', 'no_salary_row', 'contract_bound_pay', 'later_salary_row'].includes(code) ? 'rose'
    : ['out_of_guideline', 'pending_salary_change', 'stale_benchmark', 'out_of_scope_benchmark'].includes(code) ? 'amber' : 'slate';
}

export function benchmarkStateLabel(t: Translator, state: string): string {
  switch (state) {
    case 'current': return t('Market data');
    case 'stale': return t('Stale market data');
    case 'out_of_scope': return t('Out of scope');
    default: return t('No market data');
  }
}

export function ratingLabel(t: Translator, rating: string): string {
  return rating === 'Unrated' ? t('Not rated') : rating;
}

export function SyntheticBanner({ t }: { t: Translator }) {
  return (
    <div role="note" data-testid="synthetic-banner" className="flex items-start gap-2 rounded-lg border border-violet-300 bg-violet-50 px-3 py-2 text-xs font-semibold text-violet-900 dark:border-violet-500/30 dark:bg-violet-500/10 dark:text-violet-200">
      <FlaskConical className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
      <span>{t('Synthetic sample — not market data. These figures are invented test values and must never be used for pay decisions.')}</span>
    </div>
  );
}

export function SyntheticChip({ t }: { t: Translator }) {
  return <Badge tone="violet" title={t('Synthetic sample — not market data')}>{t('Synthetic — not market data')}</Badge>;
}

export function BlockerList({ blockers, t, title }: { blockers: Blocker[]; t: Translator; title?: string }) {
  if (blockers.length === 0) return null;
  return (
    <div role="alert" data-testid="blockers" className="rounded-lg border border-amber-300 bg-amber-50 px-3 py-2 text-xs text-amber-900 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200">
      <p className="mb-1 flex items-center gap-1.5 font-semibold">
        <AlertTriangle className="h-3.5 w-3.5" aria-hidden /> {title ?? t('Before you continue')}
      </p>
      <ul className="list-disc space-y-0.5 ps-5">
        {blockers.map((b, i) => <li key={`${b.code}-${i}`}>{b.message}</li>)}
      </ul>
    </div>
  );
}

/**
 * Where pay sits in the grade range: a track from the grade minimum to the maximum, the midpoint marked, the
 * current position as a dot and the proposed position as a ring. Text alternative carries the same numbers.
 */
export function RangeBar({ current, proposed, label }: { current: number | null; proposed: number | null; label: string }) {
  if (current == null) return null;
  const clamp = (v: number) => Math.max(0, Math.min(1, v));
  return (
    <div className="relative mt-1 h-2 w-24 rounded-full bg-slate-200 dark:bg-white/10" role="img" aria-label={label} title={label}>
      <span className="absolute inset-y-0 start-1/2 w-px bg-slate-400 dark:bg-slate-500" aria-hidden />
      <span className="absolute top-1/2 h-2.5 w-2.5 -translate-y-1/2 rounded-full bg-slate-500 ring-2 ring-white dark:ring-slate-900 rtl:translate-x-1/2 ltr:-translate-x-1/2"
        style={{ insetInlineStart: `${clamp(current) * 100}%` }} aria-hidden />
      {proposed != null && proposed !== current && (
        <span className="absolute top-1/2 h-3 w-3 -translate-y-1/2 rounded-full border-2 border-sapphire bg-white dark:border-cyanAccent dark:bg-slate-900 rtl:translate-x-1/2 ltr:-translate-x-1/2"
          style={{ insetInlineStart: `${clamp(proposed) * 100}%` }} aria-hidden />
      )}
    </div>
  );
}
