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

/**
 * The ERP accounting date of a posted journal, as the journal export sends it (JournalExportBuilder.DeriveAccountingDate): the payroll
 * accrual is a month-end journal whatever day Lock ran, so it carries the last day of its period; any other journal keeps the day it
 * happened when that is inside its period, else the period end. yyyy-MM-dd; an unreadable period falls back to the entry date.
 */
export function erpAccountingDate(j: { kind: string; period: string; entryDate: string }): string {
  const m = /^(\d{4})-(\d{2})$/.exec((j.period ?? '').trim());
  if (!m) return j.entryDate;
  const year = Number(m[1]);
  const month = Number(m[2]);
  if (month < 1 || month > 12) return j.entryDate;
  const last = new Date(Date.UTC(year, month, 0)).getUTCDate();
  const start = `${m[1]}-${m[2]}-01`;
  const end = `${m[1]}-${m[2]}-${String(last).padStart(2, '0')}`;
  if (j.kind === 'accrual') return end;
  return j.entryDate >= start && j.entryDate <= end ? j.entryDate : end;
}
