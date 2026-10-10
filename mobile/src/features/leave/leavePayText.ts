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
}

/** Reads the parts of an axios-style error this module needs. */
export function errorShape(error: unknown): ErrorShape {
  const e = error as { response?: { status?: number; data?: { message?: string; code?: string; error?: string } }; request?: unknown; message?: string } | null;
  if (!e || typeof e !== 'object') return {};
  if (!e.response) return { network: Boolean(e.request), message: e.message };
  return {
    status: e.response.status,
    message: e.response.data?.message,
    code: e.response.data?.code ?? e.response.data?.error,
  };
}

/** Maps a server refusal to a plain-language message key. The server's English text is never shown in Arabic. */
export function classifyLeavePayError(e: ErrorShape): LeavePayErrorKind {
  if (e.network) return 'offline';
  const msg = (e.message ?? '').toLowerCase();
  const code = (e.code ?? '').toLowerCase();
  if (code === 'feature_not_enabled') return 'featureOff';
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
  return 'generic';
}
