import type { VoidRunOptions } from '../api/payroll';

/**
 * Voiding a run whose net pay or statutory remittance has already left the bank is refused (HTTP 422) until the operator
 * STATES what happened to that cash: the bank file was recalled (and the recall reference), or the money is gone and is
 * carried as recoverable. The server lists every outstanding election at once in `detail.requiredElections`
 * (PayrollController.VoidRun, PayrollVoidService §5a); this reads that list and turns it into choices the dialog can show.
 */

export type VoidDisposition = 'FundsRecalled' | 'FundsDisbursed';
export type VoidElectionField = 'settlementDisposition' | 'remittanceDisposition';
export type VoidReferenceField = 'settlementReference' | 'remittanceReference';

export interface VoidElectionItem {
  /** Remittance group (GOSI, TAX, LOAN); null for net pay. */
  label: string | null;
  amount: number;
  period: string;
  cashAccount: string;
}

export interface VoidElectionGroup {
  field: VoidElectionField;
  referenceField: VoidReferenceField;
  kind: 'settlement' | 'remittance';
  items: VoidElectionItem[];
  total: number;
}

export interface VoidChoices {
  settlementDisposition?: VoidDisposition;
  settlementReference?: string;
  remittanceDisposition?: VoidDisposition;
  remittanceReference?: string;
}

interface RawElection {
  code?: unknown; field?: unknown; eventType?: unknown; amount?: unknown; cashAccount?: unknown; period?: unknown;
}

const REMIT_PREFIX = 'PayrollRemit:';

/** The elections the server is asking for, or null when this error is not that refusal. */
export function readRequiredElections(err: unknown): VoidElectionGroup[] | null {
  const response = (err as { response?: { status?: number; data?: unknown } } | null)?.response;
  if (response?.status !== 422) return null;
  const data = response.data as { detail?: { requiredElections?: unknown }; requiredElections?: unknown } | null;
  const raw = data?.detail?.requiredElections ?? data?.requiredElections;
  if (!Array.isArray(raw) || raw.length === 0) return null;

  const groups = new Map<VoidElectionField, VoidElectionGroup>();
  for (const e of raw as RawElection[]) {
    const code = typeof e.code === 'string' ? e.code : '';
    const eventType = typeof e.eventType === 'string' ? e.eventType : '';
    const kind: 'settlement' | 'remittance' = code.startsWith('settlement') || eventType === 'PayrollNetSettlement' ? 'settlement' : 'remittance';
    const field: VoidElectionField = kind === 'settlement' ? 'settlementDisposition' : 'remittanceDisposition';
    const group = groups.get(field) ?? {
      field, kind, items: [], total: 0,
      referenceField: kind === 'settlement' ? 'settlementReference' : 'remittanceReference',
    } as VoidElectionGroup;
    const amount = typeof e.amount === 'number' ? e.amount : Number(e.amount) || 0;
    group.items.push({
      label: kind === 'remittance' && eventType.startsWith(REMIT_PREFIX) ? eventType.slice(REMIT_PREFIX.length) : null,
      amount,
      period: typeof e.period === 'string' ? e.period : '',
      cashAccount: typeof e.cashAccount === 'string' ? e.cashAccount : '',
    });
    group.total += amount;
    groups.set(field, group);
  }
  // Net pay first, then the statutory remittances: the order a finance reader thinks of them.
  return [...groups.values()].sort((a, b) => (a.kind === b.kind ? 0 : a.kind === 'settlement' ? -1 : 1));
}

const referenceOf = (choices: VoidChoices, g: VoidElectionGroup) => (choices[g.referenceField] ?? '').trim();

/** True when every election has a choice and every recall carries its reference. */
export function electionsComplete(groups: VoidElectionGroup[], choices: VoidChoices): boolean {
  return groups.every(g => {
    const choice = choices[g.field];
    if (!choice) return false;
    return choice === 'FundsRecalled' ? referenceOf(choices, g).length > 0 : true;
  });
}

/** What to send with the resubmitted void. A reference travels only with a recall. */
export function electionOptions(choices: VoidChoices): Pick<VoidRunOptions,
  'settlementDisposition' | 'settlementReference' | 'remittanceDisposition' | 'remittanceReference'> {
  const out: Pick<VoidRunOptions, 'settlementDisposition' | 'settlementReference' | 'remittanceDisposition' | 'remittanceReference'> = {};
  if (choices.settlementDisposition) {
    out.settlementDisposition = choices.settlementDisposition;
    const ref = (choices.settlementReference ?? '').trim();
    if (choices.settlementDisposition === 'FundsRecalled' && ref) out.settlementReference = ref;
  }
  if (choices.remittanceDisposition) {
    out.remittanceDisposition = choices.remittanceDisposition;
    const ref = (choices.remittanceReference ?? '').trim();
    if (choices.remittanceDisposition === 'FundsRecalled' && ref) out.remittanceReference = ref;
  }
  return out;
}

/**
 * The refusal for a run whose journal the ERP already holds (HTTP 422, error "erp_already_posted"): voiding here cannot un-post it, so
 * the operator must acknowledge that the correcting document is raised in the ERP as well. Null for any other error.
 */
export function readErpPostedRefusal(err: unknown): { reference: string | null } | null {
  const response = (err as { response?: { status?: number; data?: unknown } } | null)?.response;
  if (response?.status !== 422) return null;
  const data = response.data as { error?: unknown; detail?: { erpPostingReference?: unknown } } | null;
  if (data?.error !== 'erp_already_posted') return null;
  const ref = data.detail?.erpPostingReference;
  return { reference: typeof ref === 'string' && ref.trim() ? ref.trim() : null };
}
