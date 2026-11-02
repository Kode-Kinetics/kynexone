/**
 * Statutory minimums are defaults, not walls (owner decision 2026-10-25). A value below the legal minimum is allowed with a
 * warning the admin acknowledges, with a reason, and the server audits it. This is the one client contract for every module
 * that does it: the 409 `warnings_require_acknowledgement` problem the hire path already answers, and the request field
 * `acknowledgedWarnings: [{ code, reason }]`. The server decides every floor; this only reads and displays it.
 */

export const BELOW_MINIMUM_REASON_MIN = 10;
export const BELOW_MINIMUM_REASON_MAX = 500;

export interface FloorWarning {
  code: string;
  field?: string;
  message?: string;
  messageEn?: string;
  messageAr?: string | null;
  requiresPermission?: boolean;
  statutoryValue?: number | null;
  configuredValue?: number | null;
  citation?: string | null;
  citationAr?: string | null;
}

/** One floor that applies to what is typed, whether or not it is under. */
export interface FloorCheck extends FloorWarning {
  label: string;
  labelAr: string;
  below: boolean;
}

export interface FloorAcknowledgement { code: string; reason: string }

/** An acknowledgement the server already holds for a saved value. */
export interface StoredFloorAck {
  code: string;
  field?: string;
  configuredValue?: number | null;
  statutoryValue?: number | null;
  citation?: string | null;
  reason: string;
  acknowledgedBy: string;
  acknowledgedAtUtc: string;
}

export type FloorStatus = 'below' | 'default' | 'generous' | 'neutral';

export function floorStatus(c: FloorCheck): FloorStatus {
  if (c.below) return 'below';
  if (c.statutoryValue == null || c.configuredValue == null || c.statutoryValue === 0) return 'neutral';
  if (c.configuredValue === c.statutoryValue) return 'default';
  return c.configuredValue > c.statutoryValue ? 'generous' : 'neutral';
}

/** The sentence for a check in the reader's language; Arabic falls back to English when the server sent none. */
export function floorMessage(c: FloorWarning, locale: string): string {
  return (locale === 'ar' ? c.messageAr : null) || c.message || c.messageEn || '';
}

export function floorLabel(c: FloorCheck, locale: string): string {
  return (locale === 'ar' ? c.labelAr : null) || c.label;
}

export function floorCitation(c: FloorWarning, locale: string): string {
  return (locale === 'ar' ? c.citationAr : null) || c.citation || '';
}

/** Parses an HTTP error into the warnings of a 409 `warnings_require_acknowledgement`, or null when it is some other error. */
export function parseFloorWarnings(err: unknown): FloorWarning[] | null {
  const r = (err as { response?: { status?: number; data?: { code?: unknown; warnings?: unknown } } })?.response;
  if (r?.status !== 409 || r.data?.code !== 'warnings_require_acknowledgement' || !Array.isArray(r.data.warnings)) return null;
  return r.data.warnings as FloorWarning[];
}

/** The server turned the reason down (422 `override_reason_required`). */
export function isReasonRequired(err: unknown): boolean {
  const r = (err as { response?: { status?: number; data?: { code?: unknown } } })?.response;
  return r?.status === 422 && r.data?.code === 'override_reason_required';
}

export function acknowledgementsFor(warnings: ReadonlyArray<{ code: string }>, reason: string): FloorAcknowledgement[] {
  const text = reason.trim();
  return warnings.map(w => ({ code: w.code, reason: text }));
}

export function reasonIsValid(reason: string): boolean {
  const n = reason.trim().length;
  return n >= BELOW_MINIMUM_REASON_MIN && n <= BELOW_MINIMUM_REASON_MAX;
}

/** Stored acknowledgements parsed from the JSON the server keeps beside a saved policy or country rule. */
export function parseStoredAcks(json?: string | null): StoredFloorAck[] {
  if (!json) return [];
  try {
    const v = JSON.parse(json) as unknown;
    return Array.isArray(v) ? (v as StoredFloorAck[]) : [];
  } catch { return []; }
}

/** The held acknowledgement that answers this check at its present value (a changed value asks again). */
export function coveringAck(stored: readonly StoredFloorAck[], c: FloorCheck): StoredFloorAck | undefined {
  return stored.find(a => a.code === c.code && a.configuredValue === c.configuredValue);
}

/** The checks still waiting for an acknowledgement. */
export function openChecks(checks: readonly FloorCheck[] | null | undefined, stored: readonly StoredFloorAck[] = []): FloorCheck[] {
  // A dry run that answered without a list (or not at all) has nothing below the minimum to show.
  return (Array.isArray(checks) ? checks : []).filter(c => c.below && !coveringAck(stored, c));
}

/** True when the form may be saved: nothing open, or the open ones are acknowledged with a real reason. */
export function readyToSave(open: readonly FloorCheck[], acknowledged: boolean, reason: string): boolean {
  return open.length === 0 || (acknowledged && reasonIsValid(reason));
}

/** Warnings from a refusal, shaped as checks so the panel shows what the server saw. */
export function checksFromWarnings(warnings: readonly FloorWarning[]): FloorCheck[] {
  return warnings.map(w => ({ ...w, label: w.field ?? w.code, labelAr: w.field ?? w.code, below: true }));
}
