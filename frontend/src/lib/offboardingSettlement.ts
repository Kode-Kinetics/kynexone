import type { FinalSettlementListRow } from '../api/payroll';

/**
 * Where Payroll's Final Settlement for one offboarding stands. The offboarding card used to show a
 * hand-ticked "Final settlement" box that said nothing about whether a settlement existed at all.
 */
export type SettlementStage = 'none' | 'draft' | 'pending' | 'approved' | 'disbursing' | 'paid';

/** Route of Payroll's Final Settlement view (the Payroll page opens it from the `tab` query value). */
export const FINAL_SETTLEMENT_HREF = '/payroll?tab=final-settlement';

/** The settlement that counts for this offboarding: the newest one that was not cancelled. */
export function settlementFor(rows: FinalSettlementListRow[], offboardingId: string): FinalSettlementListRow | null {
  const mine = rows.filter(r => r.offboardingId === offboardingId && r.status !== 'Cancelled');
  if (mine.length === 0) return null;
  return [...mine].sort((a, b) => (b.settlementDueDate ?? '').localeCompare(a.settlementDueDate ?? ''))[0];
}

export function settlementStage(row: FinalSettlementListRow | null): SettlementStage {
  switch (row?.status) {
    case 'Draft': return 'draft';
    case 'PendingApproval': return 'pending';
    case 'Approved': return 'approved';
    case 'Disbursing': return 'disbursing';
    case 'Paid': return 'paid';
    default: return 'none';
  }
}

/** Only a paid settlement clears the item; an approved one is still owed. */
export const isSettlementCleared = (row: FinalSettlementListRow | null) => settlementStage(row) === 'paid';

/** G-006: the Payroll page opened on the off-cycle run that pays this settlement. */
export const settlementRunHref = (runId: string) => `/payroll?tab=runs&run=${encodeURIComponent(runId)}`;

/** G-006: an approved settlement with no off-cycle run yet is a job waiting for a Payroll Manager. */
export const isRunDutyOpen = (row: { status: string; runDutyOpen?: boolean; linkedRunId?: string | null; payrollRunId?: string | null }): boolean =>
  row.runDutyOpen ?? (row.status === 'Approved' && !row.linkedRunId && !row.payrollRunId);
