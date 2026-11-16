/**
 * The Bank / WPS tab as four steps: 1 choose the payment batch, 2 check it, 3 create the bank file, 4 download it and mark it submitted.
 * Pure derivation from what the screen already knows (no new API call): each step is done, current, blocked or upcoming, and a step
 * that cannot move says why with a stable reason id (the component turns the id into a translated sentence and a next step).
 */

export type BankStepKey = 'batch' | 'check' | 'create' | 'download';
export type BankStepState = 'done' | 'current' | 'blocked' | 'upcoming';

export type BankStepReason =
  | 'run_not_locked' | 'no_permission_create' | 'choose_batch'
  | 'checking' | 'no_permission' | 'off' | 'check_failed'
  | 'validation_blocked' | 'needs_validation' | 'needs_file' | 'needs_setup';

/** What the gate (availability) and the panel (validate / generate) report upward. */
export interface BankFlowProgress {
  gate: 'checking' | 'no_permission' | 'off' | 'failed' | 'ready';
  validation: 'none' | 'running' | 'stale' | 'blocked' | 'passed';
  generated: boolean;
  /**
   * The employer's bank setup (account, establishment id, ...) as saved. Incomplete holds every later step: it is the gate in front of
   * step 2. Absent means "not reported" and is read as complete (the gate and older callers never say).
   */
  setup?: 'complete' | 'incomplete';
  /** The last "Create bank file" was refused (a conflict, field errors on the date or the reference). */
  createRefused?: boolean;
  /** The file was downloaded in this session. */
  downloaded?: boolean;
  /** How many employees the last check covered. */
  checked?: number;
}

export interface BankStep { key: BankStepKey; state: BankStepState; reason?: BankStepReason }

const SUBMITTED = new Set(['Submitted', 'Accepted', 'Paid', 'Reconciled']);

export function deriveBankSteps(input: {
  hasBatch: boolean;
  runLocked: boolean;
  canCreateBatch: boolean;
  /** Only a Saudi batch has the bank-file flow behind steps 2 and 3. */
  isSaudi: boolean;
  progress: BankFlowProgress | null;
  /** The batch's effective WPS status: Submitted and beyond means step 4 is done. */
  wpsStatus?: string | null;
}): BankStep[] {
  const { hasBatch, runLocked, canCreateBatch, isSaudi, progress } = input;
  const gate = progress?.gate ?? 'checking';
  const generated = !!progress?.generated;
  const submitted = !!input.wpsStatus && SUBMITTED.has(input.wpsStatus);

  const batch: BankStep = hasBatch
    ? { key: 'batch', state: 'done' }
    : !runLocked ? { key: 'batch', state: 'blocked', reason: 'run_not_locked' }
    : !canCreateBatch ? { key: 'batch', state: 'blocked', reason: 'no_permission_create' }
    : { key: 'batch', state: 'current', reason: 'choose_batch' };

  if (!hasBatch || !isSaudi) {
    return [batch,
      { key: 'check', state: 'upcoming', reason: 'choose_batch' },
      { key: 'create', state: 'upcoming', reason: 'choose_batch' },
      { key: 'download', state: 'upcoming', reason: 'choose_batch' }];
  }

  const gateReason: BankStepReason | null = gate === 'checking' ? 'checking' : gate === 'no_permission' ? 'no_permission' : gate === 'off' ? 'off' : gate === 'failed' ? 'check_failed' : null;
  if (gateReason) {
    const state: BankStepState = gate === 'checking' ? 'current' : 'blocked';
    return [batch,
      { key: 'check', state, reason: gateReason },
      { key: 'create', state: 'upcoming', reason: gate === 'checking' ? 'needs_validation' : gateReason },
      { key: 'download', state: 'upcoming', reason: 'needs_file' }];
  }

  const validated = generated || progress?.validation === 'passed';
  const setupPending = progress?.setup === 'incomplete' && !generated;
  const check: BankStep = setupPending ? { key: 'check', state: 'upcoming', reason: 'needs_setup' }
    : validated ? { key: 'check', state: 'done' }
    : progress?.validation === 'blocked' ? { key: 'check', state: 'blocked', reason: 'validation_blocked' }
    : { key: 'check', state: 'current' };
  const create: BankStep = generated ? { key: 'create', state: 'done' }
    : validated && !setupPending ? { key: 'create', state: 'current' }
    : { key: 'create', state: 'upcoming', reason: 'needs_validation' };
  const download: BankStep = submitted ? { key: 'download', state: 'done' }
    : generated ? { key: 'download', state: 'current' }
    : { key: 'download', state: 'upcoming', reason: 'needs_file' };
  return [batch, check, create, download];
}

/**
 * A starting value for the bank's batch reference (1 to 16 digits, never reused for this establishment): the digits of the payment batch
 * number (PAY-202611-220353 -> 202611220353), which the server already makes unique per batch; failing that, the run's period and 01.
 * Only a suggestion: the field stays editable and the server's "already used" check still decides.
 */
export function suggestedBatchReference(batchNumber: string | null | undefined, run?: { year: number; month: number } | null): string {
  const digits = (batchNumber ?? '').replace(/\D/g, '');
  if (/[1-9]/.test(digits)) return digits.slice(-16);
  if (run) return `${run.year}${String(run.month).padStart(2, '0')}01`;
  return '';
}
