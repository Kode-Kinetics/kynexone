/**
 * The Bank / WPS tab as ONE guided flow. Pure (no React, no fetch).
 *
 * `deriveBankSteps` (lib/bankFileSteps.ts) says where each of the four steps stands. This file turns that into the one thing the
 * screen says out loud: which stage the batch is in. The header, the step list, the primary button and the status chip all read
 * the same `BankStage`, so they cannot tell different stories.
 */
import type { BankFlowProgress, BankStep, BankStepKey, BankStepReason } from './bankFileSteps';

/** `setup` is the gate in front of step 2 (position 0); `finished` is "every step is done". */
export type BankStageKey = 'setup' | BankStepKey | 'finished';

export interface BankStage {
  key: BankStageKey;
  /** 0 for the setup gate, 1-4 for the steps (4 when finished). */
  position: number;
  total: 4;
  /** current = can move; blocked = cannot move yet (the reason says why); finished = nothing left to do. */
  state: 'current' | 'blocked' | 'finished';
  reason?: BankStepReason;
}

/** The first step that is not done is the stage. An incomplete bank setup, once a batch is open and the gate is ready, comes before step 2. */
export function deriveBankStage(steps: BankStep[], progress: BankFlowProgress | null): BankStage {
  const batchDone = steps[0]?.state === 'done';
  if (batchDone && progress?.gate === 'ready' && progress.setup === 'incomplete' && !progress.generated) {
    return { key: 'setup', position: 0, total: 4, state: 'current' };
  }
  const index = steps.findIndex(s => s.state !== 'done');
  if (index < 0) return { key: 'finished', position: 4, total: 4, state: 'finished' };
  const step = steps[index];
  return { key: step.key, position: index + 1, total: 4, state: step.state === 'blocked' ? 'blocked' : 'current', reason: step.reason };
}

/** The status chip: one word for where the bank file is, from the stage alone. */
export type BankChip = 'setup' | 'waiting' | 'attention' | 'ready' | 'submitted';
export function bankChipFor(stage: BankStage): BankChip {
  if (stage.state === 'finished') return 'submitted';
  if (stage.key === 'setup') return 'setup';
  if (stage.state === 'blocked') return 'attention';
  if (stage.key === 'download') return 'ready';
  return 'waiting';
}

// ── What "Fix" does for a blocker ────────────────────────────────────────────────

export interface BankIssueLike { code?: string; field?: string | null; employeeId?: string | number | null }

/**
 * Where a blocker is cured:
 *  - setup        a field of the employer's bank setup (opens the setup, focused on that field)
 *  - input        the batch number or credit value date of this batch (step 2)
 *  - beneficiary  the employee's BIC or address lines (the beneficiary editor, with that employee chosen)
 *  - people       the employee's account number or ID, which only People may change
 *  - none         nothing to open from here; the message stands on its own
 */
export type BankFixTarget =
  | { kind: 'setup'; field: string }
  | { kind: 'input'; field: 'batchReference' | 'paymentDate' }
  | { kind: 'beneficiary'; employeeId: number }
  | { kind: 'people'; employeeId: number }
  | { kind: 'none' };

const SETUP_FIELDS = new Set([
  'formatId', 'molEstablishmentId', 'mainAccountNumber', 'organizationName', 'organizationAddress1', 'organizationAddress2', 'organizationAddress3',
  'companyName', 'narrative', 'batchType', 'autoWpsUpload', 'nationalUnifiedNo', 'debitAccountIban',
]);

export function bankFixTarget(issue: BankIssueLike): BankFixTarget {
  const field = issue.field ?? '';
  if (field === 'batchReference' || field === 'paymentDate') return { kind: 'input', field };
  if (SETUP_FIELDS.has(field)) return { kind: 'setup', field };
  const employeeId = issue.employeeId == null || issue.employeeId === '' ? NaN : Number(issue.employeeId);
  if (!Number.isFinite(employeeId)) return { kind: 'none' };
  const code = issue.code ?? '';
  if (code === 'bic_missing' || code === 'bic_invalid' || code === 'employee_address_missing') return { kind: 'beneficiary', employeeId };
  if (code.startsWith('account_') || code === 'employee_id_invalid' || code === 'duplicate_account' || code === 'duplicate_employee_id') return { kind: 'people', employeeId };
  return { kind: 'none' };
}

/** "••0026": the last four characters of an account number or IBAN, never more. */
export function maskedTail(value: string | null | undefined): string {
  const v = (value ?? '').replace(/\s+/g, '');
  return v.length >= 4 ? `••${v.slice(-4)}` : '';
}
