/**
 * Payroll manpower + value reconciliation: the rules the screen renders, kept pure (no React, no
 * fetch) so they are unit-tested. The server owns the numbers; this file only
 *   - re-checks that they tie, so a bridge that does not add up is never presented as a bridge,
 *   - works out what a signer still owes before sign-off is offered (written reasons, the
 *     flagged-employee count), and why a sign-off is not available when it is not,
 *   - builds the sign-off request bound to the version and hash the signer actually read,
 *   - names the drill-down behind every figure.
 */
import {
  RECON_ERROR_CODES,
  type ExplanationInput, type ReconAdds, type ReconBridgeRow, type ReconContinuing, type ReconDto, type ReconErrorCode,
  type ReconExits, type ReconFinalSettlement, type ReconOffCycleMemo, type ReconSectionKind, type ReconTies, type ReconValue,
  type ReconVolume, type SignoffDto, type SignoffRequest, type VolumeCategory,
} from '../api/payrollRecon';

/** The server's floor for a written reason (PayrollValidationOverridePolicy.MinimumReasonLength). */
export const MIN_EXPLANATION_LENGTH = 10;

/** Statuses in which a run can be signed off (the server answers run_not_signable otherwise). */
export const SIGNABLE_RUN_STATUSES: readonly string[] = ['Processed', 'PendingFinanceReview'];

const cents = (n: number): number => Math.round(n * 100);

// ── Sign-off state ─────────────────────────────────────────────────────────────

/** The sign-off that counts: only an Active one. A superseded sign-off is history, never a review. */
export const activeSignoff = (s: SignoffDto | null | undefined): SignoffDto | null => (s && s.status === 'Active' ? s : null);

export function reviewFromSignoff(s: SignoffDto | null | undefined): { reviewedAtUtc: string; reviewedByName: string } | null {
  const a = activeSignoff(s);
  return a ? { reviewedAtUtc: a.signedOffAtUtc, reviewedByName: a.signedOffByName } : null;
}

// ── Formatting ─────────────────────────────────────────────────────────────────

/** "+x" / "−x" (a real minus sign) / "x" for nothing. A change smaller than a cent is no change. */
export function signedAmount(n: number, format: (abs: number) => string): string {
  const c = cents(n);
  if (c === 0) return format(0);
  return `${c > 0 ? '+' : '−'}${format(Math.abs(n))}`;
}

/** An amount as it is, with the real minus sign when negative (no plus sign): the same glyph as `signedAmount` uses. */
export function amountText(n: number, format: (abs: number) => string): string {
  return cents(n) < 0 ? `−${format(Math.abs(n))}` : format(Math.abs(n) < 0.005 ? 0 : n);
}

// ── Do the numbers tie? ────────────────────────────────────────────────────────

/** A server tie counts only when it says so; an absent field is not a pass. */
export function tiesOk(t: ReconTies | null | undefined): boolean {
  if (t === true) return true;
  return typeof t === 'object' && t !== null && t.ok === true;
}

const sumGroup = (o: Record<string, number>): number => Object.values(o).reduce((s, v) => s + cents(v), 0);

/** prior + adds + exits + continuing = current, compared in whole cents so 0.1 + 0.2 ties to 0.3. */
export function rowTies(row: Pick<ReconBridgeRow, 'prior' | 'adds' | 'exits' | 'continuing' | 'current'>): boolean {
  return cents(row.prior) + sumGroup(row.adds as unknown as Record<string, number>) + sumGroup(row.exits as unknown as Record<string, number>)
    + sumGroup(row.continuing as unknown as Record<string, number>) === cents(row.current);
}

export function volumeMovement(v: Pick<ReconVolume, 'lines'>): { adds: number; exits: number; net: number } {
  const adds = v.lines.filter(l => l.kind === 'Add').reduce((s, l) => s + l.count, 0);
  const exits = v.lines.filter(l => l.kind === 'Exit').reduce((s, l) => s + l.count, 0);
  return { adds, exits, net: adds - exits };
}

/** Opening + adds - exits = closing. */
export function volumeTies(v: ReconVolume): boolean {
  return v.opening + volumeMovement(v).net === v.closing;
}

/**
 * What does not tie in the value bridge: item rows as "Kind:CODE", headline rows as "headline:x".
 * `bridge` is false for a NoPrior reconciliation, which carries a composition and no movement.
 */
export function valueTieIssues(value: ReconValue, bridge = true): string[] {
  const issues = new Set<string>();
  const sections = value.sections ?? [];
  const items = (kind: ReconSectionKind) => sections.filter(s => s.kind === kind).flatMap(s => s.items);
  const total = (kind: ReconSectionKind, pick: (r: ReconBridgeRow) => number) => items(kind).reduce((s, r) => s + cents(pick(r)), 0);
  if (bridge) for (const s of sections) for (const i of s.items) if (!rowTies(i)) issues.add(`${s.kind}:${i.code}`);

  const h = value.headline;
  if (bridge) for (const k of ['gross', 'deductions', 'net', 'employer'] as const) if (!rowTies(h[k])) issues.add(`headline:${k}`);
  if (cents(h.gross.current) !== total('Earning', r => r.current)) issues.add('headline:gross');
  if (cents(h.deductions.current) !== total('Deduction', r => r.current)) issues.add('headline:deductions');
  if (cents(h.employer.current) !== total('Employer', r => r.current)) issues.add('headline:employer');
  if (cents(h.net.current) !== cents(h.gross.current) - cents(h.deductions.current)) issues.add('headline:net');
  if (bridge && cents(h.net.prior) !== cents(h.gross.prior) - cents(h.deductions.prior)) issues.add('headline:net');
  return [...issues];
}

/** Server ties, the server's integrity check and the client's own re-check all agree. */
export function reconTrusted(d: ReconDto): boolean {
  if (!d.integrity?.ok) return false;
  if (!tiesOk(d.volume.ties) || !tiesOk(d.value.ties)) return false;
  const bridge = d.mode === 'Full';
  if (bridge && !volumeTies(d.volume)) return false;
  return valueTieIssues(d.value, bridge).length === 0;
}

// ── Written reasons ────────────────────────────────────────────────────────────

/** Item explanations are keyed "Kind:CODE": a deduction and an employer cost can share a code. */
export const itemKey = (kind: ReconSectionKind, code: string): string => `${kind}:${code}`;
export const explanationId = (scope: ExplanationInput['scope'], key: string): string => `${scope}:${key}`;

export interface FlaggedItem { kind: ReconSectionKind; code: string; label: string; key: string; prior: number; current: number; delta: number; deltaPct: number | null }

/** The items the server flagged as moving more than the threshold: each needs a written reason. */
export function flaggedItems(value: ReconValue): FlaggedItem[] {
  return (value.sections ?? []).flatMap(s => s.items.filter(i => i.flagged).map(i => ({
    kind: s.kind, code: i.code, label: i.label, key: itemKey(s.kind, i.code), prior: i.prior, current: i.current, delta: i.delta, deltaPct: i.deltaPct,
  })));
}

/** Exits nobody could explain from the data. Each listed employee needs a written reason. */
export function employeeReasonCategories(v: ReconVolume): Array<'NOT_PAID' | 'UNEXPLAINED'> {
  return (['NOT_PAID', 'UNEXPLAINED'] as const).filter(c => v.lines.some(l => l.category === c && l.count > 0));
}

export interface RequiredExplanation { scope: ExplanationInput['scope']; key: string; label: string | null }

export function requiredExplanations(value: ReconValue, employeeIds: number[]): RequiredExplanation[] {
  return [
    ...flaggedItems(value).map(i => ({ scope: 'Item' as const, key: i.key, label: i.label })),
    ...employeeIds.map(id => ({ scope: 'Employee' as const, key: String(id), label: null })),
  ];
}

export const explanationText = (drafts: Record<string, string>, scope: ExplanationInput['scope'], key: string): string => (drafts[explanationId(scope, key)] ?? '').trim();

export function missingExplanations(required: RequiredExplanation[], drafts: Record<string, string>): Array<{ scope: ExplanationInput['scope']; key: string }> {
  return required.filter(r => explanationText(drafts, r.scope, r.key).length < MIN_EXPLANATION_LENGTH).map(r => ({ scope: r.scope, key: r.key }));
}

// ── The sign-off request ───────────────────────────────────────────────────────

export function buildSignoffRequest(input: {
  recon: ReconDto;
  required: RequiredExplanation[];
  drafts: Record<string, string>;
  acknowledgedFlaggedEmployeeCount: number;
  notes: string;
  priorRunId: string | null;
}): SignoffRequest {
  const { recon } = input;
  if (typeof recon.processVersion !== 'number' || !recon.snapshotHash) {
    throw new Error('The reconciliation carries no version stamp, so a sign-off cannot be bound to it.');
  }
  const notes = input.notes.trim();
  return {
    expectedProcessVersion: recon.processVersion,
    expectedSnapshotHash: recon.snapshotHash,
    ...(input.priorRunId ? { priorRunId: input.priorRunId } : {}),
    acknowledgedFlaggedEmployeeCount: input.acknowledgedFlaggedEmployeeCount,
    explanations: input.required.map(r => ({ scope: r.scope, key: r.key, text: explanationText(input.drafts, r.scope, r.key) })),
    ...(notes ? { notes } : {}),
  };
}

export type SignoffBlocker =
  | 'already_signed' | 'no_permission' | 'run_status' | 'not_applicable' | 'scope' | 'integrity' | 'no_stamp'
  | 'employees_unknown' | 'explanations' | 'flagged_unknown' | 'acknowledge';

/** Can the form be submitted, and if not, the first reason why (the one the screen says). */
export function signoffGate(input: {
  recon: ReconDto;
  runStatus: string;
  mayApprove: boolean;
  required: RequiredExplanation[];
  drafts: Record<string, string>;
  /** null = the count of flagged employees could not be read. */
  flaggedEmployeeCount: number | null;
  flaggedAffirmed: boolean;
  /** False when the employees who need a written reason could not be listed. Default true. */
  employeesKnown?: boolean;
}): { canSubmit: boolean; blocker: SignoffBlocker | null } {
  const { recon } = input;
  const stop = (blocker: SignoffBlocker) => ({ canSubmit: false, blocker });
  if (activeSignoff(recon.signoff)) return stop('already_signed');
  if (!input.mayApprove) return stop('no_permission');
  if (!SIGNABLE_RUN_STATUSES.includes(input.runStatus)) return stop('run_status');
  if (recon.mode === 'NotApplicable') return stop('not_applicable');
  if (!recon.scope.canSignOff || recon.scope.partial) return stop('scope');
  if (!reconTrusted(recon)) return stop('integrity');
  if (typeof recon.processVersion !== 'number' || !recon.snapshotHash) return stop('no_stamp');
  if (input.employeesKnown === false) return stop('employees_unknown');
  if (missingExplanations(input.required, input.drafts).length > 0) return stop('explanations');
  if (input.flaggedEmployeeCount === null) return stop('flagged_unknown');
  if (input.flaggedEmployeeCount > 0 && !input.flaggedAffirmed) return stop('acknowledge');
  return { canSubmit: true, blocker: null };
}

// ── Drill-down ─────────────────────────────────────────────────────────────────

/** "newJoiner" -> "NEW_JOINER": the category names the API uses. */
export const categoryOfKey = (key: string): string => key.replace(/([A-Z])/g, '_$1').toUpperCase();

/** A cell of an item row: its prior or current amount, or one add / exit category / continuing driver. */
export type ValueCell = 'prior' | 'current' | { group: 'adds'; key: keyof ReconAdds } | { group: 'exits'; key: keyof ReconExits } | { group: 'continuing'; key: keyof ReconContinuing };

export type DrillTarget =
  | { section: 'volume'; category: VolumeCategory | 'OPENING' | 'CLOSING' | 'CONTINUING' }
  | { section: 'value'; kind: ReconSectionKind; itemCode: string; cell: ValueCell }
  | { section: 'value'; flagged: true }
  | { section: 'finalSettlement'; cell: string }
  | { section: 'finalSettlement'; component: string };

export interface DrillParams { section: 'volume' | 'value' | 'finalSettlement'; kind?: ReconSectionKind; itemCode?: string; category?: string; bucket?: string }

export function drillParams(t: DrillTarget): DrillParams {
  if (t.section === 'volume') return { section: 'volume', category: t.category };
  if (t.section === 'finalSettlement') return 'cell' in t ? { section: 'finalSettlement', category: t.cell } : { section: 'finalSettlement', itemCode: t.component };
  if ('flagged' in t) return { section: 'value', bucket: 'flagged' };
  const head = { section: 'value' as const, kind: t.kind, itemCode: t.itemCode };
  if (t.cell === 'prior' || t.cell === 'current') return { ...head, bucket: t.cell };
  return t.cell.group === 'continuing' ? { ...head, bucket: t.cell.key } : { ...head, category: categoryOfKey(t.cell.key) };
}

// ── Errors ─────────────────────────────────────────────────────────────────────

export interface ReconError {
  code: ReconErrorCode | 'unknown';
  status: number | null;
  message: string | null;
  /** reconciliation_not_reviewed: why the sign-off does not count. */
  reason: 'Missing' | 'Stale' | 'PriorChanged' | null;
  processVersion: number | null;
  signedVersion: number | null;
  /** variance_explanation_required: the explanations still missing. */
  missingKeys: string[];
}

export function parseReconError(err: unknown): ReconError {
  const e = err as { response?: { status?: number; data?: Record<string, unknown> | null } } | null;
  const data = (e?.response?.data && typeof e.response.data === 'object') ? e.response.data : {};
  const raw = typeof data.error === 'string' ? data.error : typeof data.code === 'string' ? data.code : '';
  const code = (RECON_ERROR_CODES as readonly string[]).includes(raw) ? raw as ReconErrorCode : 'unknown';
  const reason = data.reason === 'Missing' || data.reason === 'Stale' || data.reason === 'PriorChanged' ? data.reason : null;
  const num = (v: unknown) => (typeof v === 'number' && Number.isFinite(v) ? v : null);
  return {
    code,
    status: typeof e?.response?.status === 'number' ? e.response.status : null,
    message: typeof data.message === 'string' && data.message ? data.message : null,
    reason,
    processVersion: num(data.processVersion),
    signedVersion: num(data.signedVersion),
    missingKeys: Array.isArray(data.missingKeys) ? data.missingKeys.filter((k): k is string => typeof k === 'string') : [],
  };
}

// ── Tolerant readers ───────────────────────────────────────────────────────────

export function normalizeCells(cells: ReconFinalSettlement['cells'] | null | undefined): Array<{ code: string; count: number }> {
  if (!cells) return [];
  if (Array.isArray(cells)) return cells.map(c => ({ code: c.code, count: c.count }));
  return Object.entries(cells).map(([code, count]) => ({ code, count }));
}

export function offCycleSummary(v: ReconOffCycleMemo | number | null | undefined): { count: number; gross: number | null } {
  if (typeof v === 'number') return { count: v, gross: null };
  if (v && typeof v === 'object') return { count: v.count ?? 0, gross: typeof v.gross === 'number' ? v.gross : null };
  return { count: 0, gross: null };
}

// ── Which run can be the comparison ────────────────────────────────────────────

/** Regular and Replacement runs own their pay period; off-cycle runs sit inside one. */
export const isPeriodOwning = (runType: string | null | undefined): boolean => runType === 'Regular' || runType === 'Replacement';

/**
 * True when the pipeline must hold the run at Review: a period-owning run, in a company whose rule requires the sign-off.
 * It fails closed: until the reconciliation is read, or if the server did not say, the sign-off is required.
 */
export const reviewIsRequired = (runType: string | null | undefined, recon: Pick<ReconDto, 'signoffRequired'> | null | undefined): boolean =>
  isPeriodOwning(runType) && recon?.signoffRequired !== false;

type Candidate = { id: string; companyId: string | null; year: number; month: number; status: string; runType: string };

/**
 * The earlier runs a signer may pick as the comparison instead of the previous month: the same company,
 * period-owning, carrying totals (not Draft, not Voided). Newest first.
 */
export function priorRunCandidates<T extends Candidate>(run: Candidate, runs: T[]): T[] {
  const period = (r: Candidate) => r.year * 12 + r.month;
  return runs
    .filter(r => r.id !== run.id && r.companyId === run.companyId && isPeriodOwning(r.runType)
      && r.status !== 'Draft' && r.status !== 'Voided' && period(r) < period(run))
    .sort((a, b) => period(b) - period(a));
}

// ── Reading the reply ──────────────────────────────────────────────────────────

const MODES: readonly string[] = ['Full', 'NoPrior', 'NotApplicable'];
const isObj = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);

/** Narrow an untrusted reply to the parts the screen reads. Anything else is "not a reconciliation", never guessed. */
export function parseRecon(raw: unknown): ReconDto | null {
  if (!isObj(raw) || typeof raw.mode !== 'string' || !MODES.includes(raw.mode)) return null;
  if (!isObj(raw.scope) || !isObj(raw.volume) || !Array.isArray(raw.volume.lines)) return null;
  if (!isObj(raw.value) || !Array.isArray(raw.value.sections) || !isObj(raw.value.headline)) return null;
  if (!isObj(raw.finalSettlement) || !isObj(raw.integrity) || !isObj(raw.thresholds)) return null;
  return raw as unknown as ReconDto;
}

/** "2026-11" -> { year: 2026, month: 11 }; null for anything that is not a calendar month. */
export function parsePeriod(p: string | null | undefined): { year: number; month: number } | null {
  const m = /^(\d{4})-(\d{2})$/.exec(p ?? '');
  if (!m) return null;
  const month = Number(m[2]);
  return month >= 1 && month <= 12 ? { year: Number(m[1]), month } : null;
}
