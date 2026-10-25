/**
 * Cost accounting v1 (WP-F): the pure rules behind the GL screens. Kept free of React so the rules that decide what a
 * user may pick, and which sentence they read, are unit-tested on their own (unit/glCostAccounting.spec.ts).
 */

export const COST_CENTRE_RULES = ['Required', 'Optional', 'NotAllowed'] as const;
export type CostCentreRule = (typeof COST_CENTRE_RULES)[number];

/** Older chart rows say Revenue where newer ones say Income. */
export function isPnlType(type: string | null | undefined): boolean {
  return type === 'Expense' || type === 'Income' || type === 'Revenue';
}

/** Only profit-and-loss accounts can carry a cost centre, so only they can require one. */
export const canRequireCostCentre = isPnlType;

/** True when the rule cannot be saved on an account of this type (the server answers 422 for the same case). */
export function ruleBlockedForType(rule: string, type: string): boolean {
  return rule === 'Required' && !canRequireCostCentre(type);
}

/** A row from a server that predates the rule reads as the migration default. */
export function cleanRule(rule: string | null | undefined): CostCentreRule {
  return (COST_CENTRE_RULES as readonly string[]).includes(rule ?? '') ? (rule as CostCentreRule) : 'Optional';
}

/** '' keeps everything (All rules). */
export function matchesRuleFilter(account: { costCenterRule?: string | null }, filter: string): boolean {
  return !filter || cleanRule(account.costCenterRule) === filter;
}

// ── What the server said ─────────────────────────────────────────────────────

const text = (v: unknown): string | null => (typeof v === 'string' && v.trim() ? v.trim() : null);

/**
 * The server's own refusal sentence in the viewer's language: Arabic prefers `messageAr`, anything else prefers
 * `message`, and each falls back to the other. Null when the body carries neither, so the caller can describe the error.
 */
export function pickServerMessage(data: unknown, locale: string): string | null {
  if (!data || typeof data !== 'object') return null;
  const body = data as { message?: unknown; messageAr?: unknown };
  return locale === 'ar' ? text(body.messageAr) ?? text(body.message) : text(body.message) ?? text(body.messageAr);
}

export function apiErrorCode(err: unknown): string | null {
  const data = (err as { response?: { data?: { code?: unknown; error?: unknown } } } | null | undefined)?.response?.data;
  return text(data?.code) ?? text(data?.error);
}

/** M-7: a payroll run awaits finance review or lock, so the change would alter what it posts. */
export function isLockedIntoApprovedRun(err: unknown): boolean {
  const status = (err as { response?: { status?: number } } | null | undefined)?.response?.status;
  return status === 409 && apiErrorCode(err) === 'locked_into_approved_run';
}
