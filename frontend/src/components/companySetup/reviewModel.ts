import type { CompanySetupDraftBody, CompanySetupPlan, ExistingRow, OrgSection, PlanArea, PlanItem } from '../../api/companySetup';
import { normalizeFieldPath, type TreeNode } from '../../lib/companySetupWizard';

/**
 * Pure rules behind the Step 7 review (ReviewStep.tsx), kept free of React so they are unit-tested.
 *
 * A row is in exactly one bucket, in the reader's words: added, changed, saved in Step 4 (Addendum B2), staying as it
 * is (unchanged, or a legacy `reuse`), or needing a fix (`conflict`). The count chips filter by bucket.
 */
export type ReviewBucket = 'add' | 'update' | 'saved' | 'kept' | 'fix' | 'remove';

const ORG_SECTIONS: readonly [PlanArea, OrgSection][] = [['costCentre', 'costCentres'], ['department', 'departments'], ['grade', 'grades'], ['designation', 'designations']];
const ORG_AREAS = new Set<PlanArea>(ORG_SECTIONS.map(([area]) => area));

export const reviewCodeKey = (code: string | null | undefined) => (code ?? '').trim().toUpperCase();

/**
 * Rows of the Organisation sections already written live in Step 4 (B2), keyed `area:CODE`. Named by the wizard
 * (`savedSections`), or known without a guess: a row that carries a company id but was not in what the company had
 * when the wizard opened (`baseline`). A row without a company id is never "saved".
 */
export function savedInStep4(body: CompanySetupDraftBody, baseline: CompanySetupDraftBody | null | undefined, savedSections?: readonly OrgSection[]): Set<string> {
  const out = new Set<string>();
  for (const [area, section] of ORG_SECTIONS) {
    const before = new Set(((baseline?.[section] ?? []) as ExistingRow[]).map(r => r.existingId).filter(Boolean));
    const whole = savedSections?.includes(section) ?? false;
    for (const row of (body[section] ?? []) as (ExistingRow & { code: string })[]) {
      if (row.existingId && (whole || !before.has(row.existingId))) out.add(`${area}:${reviewCodeKey(row.code)}`);
    }
  }
  return out;
}

export function reviewBucket(item: PlanItem, saved: ReadonlySet<string>): ReviewBucket {
  if (item.action === 'conflict') return 'fix';
  if (item.action === 'create') return 'add';
  if (item.action === 'update') return 'update';
  if (item.action === 'remove') return 'remove';
  if (item.action === 'unchanged' && ORG_AREAS.has(item.area) && saved.has(`${item.area}:${reviewCodeKey(item.code)}`)) return 'saved';
  return 'kept';
}

/** Shown open: what the review is about. Collapsed: what stays as it is. */
export const isOpenBucket = (b: ReviewBucket) => b !== 'kept';

export interface ReviewCounts { add: number; update: number; saved: number; kept: number; fix: number; remove: number }

/** Every count is the number of rows the chip opens, except `fix`, which also counts the plan's problems (no rows). */
export function reviewCounts(plan: CompanySetupPlan, saved: ReadonlySet<string>): ReviewCounts {
  const out: ReviewCounts = { add: 0, update: 0, saved: 0, kept: 0, fix: plan.problems.length, remove: 0 };
  for (const item of plan.items) out[reviewBucket(item, saved)] += 1;
  return out;
}

/** The draft's own value of the field a problem names (A9 "Keep my value"); null when it is empty or not a value. */
export function draftValueLabel(body: CompanySetupDraftBody, field: string): string | null {
  let holder: unknown = body;
  for (const part of normalizeFieldPath(field).split('.')) holder = holder && typeof holder === 'object' ? (holder as Record<string, unknown>)[part] : undefined;
  if (holder === undefined || holder === null || holder === '') return null;
  if (typeof holder === 'boolean') return holder ? 'Yes' : 'No';
  return typeof holder === 'object' ? null : String(holder);
}

/** A conflict's sentence: the planner's reason alone when it already names the row, so it is not said twice. */
export function conflictNamesRow(item: Pick<PlanItem, 'code' | 'reason'>): boolean {
  const code = (item.code ?? '').trim();
  return !!item.reason && !!code && item.reason.includes(code);
}
