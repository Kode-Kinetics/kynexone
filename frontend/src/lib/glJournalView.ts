/** Pure view rules for the GL Journal tab on a posted run. */

export interface JournalIdentity { kind: string; group?: string | null }

const REMIT_TITLE: Record<string, string> = { GOSI: 'GOSI remittance', TAX: 'Tax remittance', LOAN: 'Loan remittance' };
const REMIT_REVERSAL_TITLE: Record<string, string> = {
  GOSI: 'GOSI remittance reversal', TAX: 'Tax remittance reversal', LOAN: 'Loan remittance reversal',
};

/** The English title key of one journal, by the event that posted it. */
export function journalTitle(j: JournalIdentity): string {
  switch (j.kind) {
    case 'accrual': return 'Payroll accrual';
    case 'settlement': return 'Net pay settlement';
    case 'settlementReversal': return 'Net pay settlement reversal';
    case 'remittance': return REMIT_TITLE[j.group ?? ''] ?? 'Statutory remittance';
    case 'remittanceReversal': return REMIT_REVERSAL_TITLE[j.group ?? ''] ?? 'Statutory remittance reversal';
    case 'reclass': return 'Disbursed funds reclassification';
    case 'void': return 'Void';
    default: return 'Payroll adjustment';
  }
}

/** True when at least one line carries a cost centre: the column exists only then. */
export function hasCostCentres(lines: ReadonlyArray<{ costCentreCode?: string | null }>): boolean {
  return lines.some(l => (l.costCentreCode ?? '').trim().length > 0);
}

/**
 * Which cost-accounting note, if any, sits above the journal. For a posted run the evidence is the posted lines
 * (`costing.postedWithCostCentres`), because `costing.enabled` comes from the preview plan, which is Off once posted.
 */
export function costingNote(journal: {
  isPosted?: boolean;
  costing?: { enabled: boolean; postedWithCostCentres?: boolean };
}): 'off' | 'postedWithout' | null {
  const costing = journal.costing;
  if (!costing) return null;
  if (journal.isPosted) {
    // A server that predates the field says nothing: never claim cost centres were absent.
    return costing.postedWithCostCentres === false ? 'postedWithout' : null;
  }
  return costing.enabled ? null : 'off';
}
