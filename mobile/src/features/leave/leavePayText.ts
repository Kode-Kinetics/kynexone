// ============================================================
// Plural categories and plain-language error classes for Leave, Overtime and Payslips.
// Pure, unit-tested (tests/leavePay.test.ts). No react / react-native imports.
// ============================================================

export type PluralCategory = 'zero' | 'one' | 'two' | 'few' | 'many' | 'other';

/**
 * CLDR plural category. Arabic has six forms (يوم واحد / يومان / ٣ أيام / ١١ يومًا / ١٠٠ يوم).
 * Hand-written rather than Intl.PluralRules so it behaves the same on every JS engine.
 */
export function pluralCategory(language: 'en' | 'ar', n: number): PluralCategory {
  if (!Number.isFinite(n)) return 'other';
  if (language === 'en') return n === 1 ? 'one' : 'other';
  if (!Number.isInteger(n)) return 'other';
  if (n === 0) return 'zero';
  if (n === 1) return 'one';
  if (n === 2) return 'two';
  const mod100 = n % 100;
  if (mod100 >= 3 && mod100 <= 10) return 'few';
  if (mod100 >= 11 && mod100 <= 99) return 'many';
  return 'other';
}

export type LeavePayErrorKind =
  | 'offline'
  | 'permission'
  | 'notFound'
  | 'featureOff'
  | 'overlap'
  | 'insufficientBalance'
  | 'balanceNotSetUp'
  | 'notice'
  | 'minService'
  | 'minDays'
  | 'maxDays'
  | 'maxConsecutive'
  | 'notEligible'
  | 'typeUnavailable'
  | 'statutoryLimit'
  | 'statutoryLimitNoDays'
  | 'hourly'
  | 'attachmentNotFound'
  | 'invalidDates'
  | 'cancelAfterStart'
  | 'refused'
  | 'blackout'
  | 'reasonRequired'
  | 'attachmentRequired'
  | 'halfDayNotAllowed'
  | 'probation'
  | 'noApprovalRoute'
  | 'notWithdrawable'
  | 'alreadyCancelled'
  | 'cancellationPending'
  | 'overtimeBelowMinimum'
  | 'overtimeAboveDailyMax'
  | 'overtimeMonthlyCap'
  | 'overtimeOverlap'
  | 'overtimeNoPolicy'
  | 'notLinked'
  | 'generic';

export interface ErrorShape {
  /** No HTTP response at all (network down, timeout). */
  network?: boolean;
  status?: number;
  message?: string;
  code?: string;
  /** The values a refusal sentence needs (days, months, year, leaveType, and leaveTypeAr when the type has an Arabic name). Present only when the server sent them. */
  args?: Record<string, string>;
}

/** Reads the parts of an axios-style error this module needs. */
export function errorShape(error: unknown): ErrorShape {
  const e = error as { response?: { status?: number; data?: { message?: string; code?: string; error?: string; args?: unknown } }; request?: unknown; message?: string } | null;
  if (!e || typeof e !== 'object') return {};
  if (!e.response) return { network: Boolean(e.request), message: e.message };
  const shape: ErrorShape = {
    status: e.response.status,
    message: e.response.data?.message,
    code: e.response.data?.code ?? e.response.data?.error,
  };
  const args = stringArgs(e.response.data?.args);
  if (args) shape.args = args;
  return shape;
}

/**
 * The leave type's name for a refusal sentence. The server sends `leaveType` (English) and, when the type has one,
 * `leaveTypeAr`; Arabic UI uses the Arabic name and falls back to the English one.
 */
export function refusalLeaveTypeName(args: Record<string, string> | undefined, language: 'en' | 'ar'): string {
  const ar = args?.leaveTypeAr?.trim();
  return language === 'ar' && ar ? ar : (args?.leaveType ?? '');
}

/** The refusal args as a string map, or undefined when the server sent none (older servers) or something else. */
function stringArgs(raw: unknown): Record<string, string> | undefined {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return undefined;
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(raw as Record<string, unknown>)) {
    if (typeof v === 'string' || typeof v === 'number') out[k] = String(v);
  }
  return Object.keys(out).length > 0 ? out : undefined;
}

/**
 * Leave refusal codes (API contract: backend LeaveRefusalCodes) -> message kind, and the args each sentence needs.
 * A code whose sentence needs a value the server did not send degrades to `refused` rather than printing a hole.
 */
const LEAVE_CODE_KINDS: Record<string, { kind: LeavePayErrorKind; needs?: readonly string[] }> = {
  leave_overlap: { kind: 'overlap' },
  leave_notice_required: { kind: 'notice', needs: ['days'] },
  leave_insufficient_balance: { kind: 'insufficientBalance' },
  leave_balance_not_set_up: { kind: 'balanceNotSetUp', needs: ['year', 'leaveType'] },
  leave_probation: { kind: 'probation' },
  leave_min_service_months: { kind: 'minService', needs: ['months'] },
  leave_max_consecutive_days: { kind: 'maxConsecutive', needs: ['days'] },
  leave_min_days: { kind: 'minDays', needs: ['days'] },
  leave_max_days: { kind: 'maxDays', needs: ['days'] },
  leave_reason_required: { kind: 'reasonRequired' },
  leave_attachment_required: { kind: 'attachmentRequired' },
  leave_attachment_not_found: { kind: 'attachmentNotFound' },
  leave_half_day_not_allowed: { kind: 'halfDayNotAllowed' },
  leave_hourly_not_allowed: { kind: 'hourly' },
  leave_blackout: { kind: 'blackout' },
  leave_type_unavailable: { kind: 'typeUnavailable' },
  leave_not_eligible: { kind: 'notEligible', needs: ['leaveType'] },
  leave_invalid_dates: { kind: 'invalidDates' },
  leave_cancel_after_start: { kind: 'cancelAfterStart' },
  leave_refused: { kind: 'refused' },
};

function kindFromCode(code: string, args: Record<string, string> | undefined): LeavePayErrorKind | null {
  if (code === 'leave_statutory_limit') return args?.days ? 'statutoryLimit' : 'statutoryLimitNoDays';
  const entry = LEAVE_CODE_KINDS[code];
  if (!entry) return null;
  return (entry.needs ?? []).every((k) => args?.[k]) ? entry.kind : 'refused';
}

/** Maps a server refusal to a plain-language message key. The server's English text is never shown in Arabic. */
export function classifyLeavePayError(e: ErrorShape): LeavePayErrorKind {
  if (e.network) return 'offline';
  const msg = (e.message ?? '').toLowerCase();
  const code = (e.code ?? '').toLowerCase();
  if (code === 'feature_not_enabled') return 'featureOff';
  // A leave refusal code decides first; the sentence below is only for servers that do not send one yet.
  const fromCode = kindFromCode(code, e.args);
  if (fromCode) return fromCode;
  if (code === 'ess_employee_not_linked' || msg.includes('not linked to an employee')) return 'notLinked';
  if (msg.includes('already has an approved or pending leave')) return 'overlap';
  if (msg.includes('insufficient leave balance')) return 'insufficientBalance';
  if (msg.includes('blackout')) return 'blackout';
  if (msg.includes('reason is required')) return 'reasonRequired';
  if (msg.includes('attachment is required')) return 'attachmentRequired';
  if (msg.includes('half-day leave is not allowed')) return 'halfDayNotAllowed';
  if (msg.includes('probation')) return 'probation';
  if (msg.includes('only unapproved requests can be withdrawn')) return 'notWithdrawable';
  if (msg.includes('already cancelled or withdrawn')) return 'alreadyCancelled';
  if (msg.includes('cancellation request is already pending')) return 'cancellationPending';
  if (msg.includes('minimum overtime')) return 'overtimeBelowMinimum';
  if (msg.includes('maximum overtime')) return 'overtimeAboveDailyMax';
  if (msg.includes('monthly overtime cap')) return 'overtimeMonthlyCap';
  if (msg.includes('overlaps an existing request')) return 'overtimeOverlap';
  if (msg.includes('active overtime policy')) return 'overtimeNoPolicy';
  if (e.status === 422) return 'noApprovalRoute';
  if (e.status === 403) return 'permission';
  if (e.status === 404) return 'notFound';
  // The server understood the request and refused it. A refusal is not "something went wrong on our side".
  if (e.status === 400) return 'refused';
  return 'generic';
}
