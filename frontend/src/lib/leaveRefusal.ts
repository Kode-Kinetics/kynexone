import type { MessageParams } from '../i18n/translations';
import { LEAVE_REFUSAL_KEYS as K } from '../i18n/leaveRefusal';
import { requestFailureReason } from './requestFailure';

/**
 * A refused leave request, in the viewer's language.
 *
 * The server answers HTTP 400 `{ code, message, args }`: `message` is the English sentence (kept for older clients), `code` is
 * stable (backend LeaveRefusalCodes) and `args` carries the values the sentence needs. Each known code maps to one translated
 * sentence with its args filled in. An unknown code, or a server that sends only `message`, keeps the server's message. A known
 * code that lost a value it needs says the neutral "The request was refused" rather than printing a sentence with a hole.
 * `leave_refused` has no dedicated sentence, so it is always the neutral one, never the English text.
 *
 * `audience` only changes the missing-balance sentence: "your balance" for an employee in self-service, "this employee" for HR
 * applying on someone's behalf.
 *
 * `locale` picks the leave type's name: the server sends `leaveType` (English) and, when the type has one, `leaveTypeAr`. In
 * Arabic the sentence uses the Arabic name; without one (or in any other language) it keeps the English name.
 */
type Translator = (key: string, params?: MessageParams) => string;

interface Rule { key: string; needs?: readonly string[]; numeric?: readonly string[] }

const RULES: Record<string, Rule> = {
  leave_overlap: { key: K.overlap },
  leave_notice_required: { key: K.notice, needs: ['days'], numeric: ['days'] },
  leave_insufficient_balance: { key: K.insufficient },
  leave_probation: { key: K.probation },
  leave_min_service_months: { key: K.minService, needs: ['months'], numeric: ['months'] },
  leave_max_consecutive_days: { key: K.maxConsecutive, needs: ['days'], numeric: ['days'] },
  leave_min_days: { key: K.minDays, needs: ['days'], numeric: ['days'] },
  leave_max_days: { key: K.maxDays, needs: ['days'], numeric: ['days'] },
  leave_reason_required: { key: K.reason },
  leave_attachment_required: { key: K.attachment },
  leave_attachment_not_found: { key: K.attachmentMissing },
  leave_half_day_not_allowed: { key: K.halfDay },
  leave_hourly_not_allowed: { key: K.hourly },
  leave_blackout: { key: K.blackout },
  leave_type_unavailable: { key: K.unavailable },
  leave_not_eligible: { key: K.notEligible, needs: ['leaveType'] },
  leave_invalid_dates: { key: K.invalidDates },
  leave_cancel_after_start: { key: K.cancelAfterStart },
  leave_refused: { key: K.refused },
};

type ErrorLike = { response?: { data?: { code?: unknown; message?: unknown; args?: unknown } } } | null | undefined;

function argsOf(raw: unknown): Record<string, string> {
  const out: Record<string, string> = {};
  if (raw && typeof raw === 'object' && !Array.isArray(raw)) {
    for (const [k, v] of Object.entries(raw as Record<string, unknown>)) {
      if (typeof v === 'string' || typeof v === 'number') out[k] = String(v);
    }
  }
  return out;
}

export function leaveRefusalText(err: unknown, t: Translator, audience: 'self' | 'hr' = 'self', locale?: string): string {
  const data = (err as ErrorLike)?.response?.data;
  const code = typeof data?.code === 'string' ? data.code : '';
  if (!code.startsWith('leave_')) return t(requestFailureReason(err));
  const args = argsOf(data?.args);
  if (locale === 'ar' && args.leaveTypeAr?.trim() && args.leaveType) args.leaveType = args.leaveTypeAr.trim();

  if (code === 'leave_balance_not_set_up') {
    return args.year && args.leaveType
      ? t(audience === 'hr' ? K.balanceHr : K.balanceSelf, { year: args.year, leaveType: args.leaveType })
      : t(K.refused);
  }
  if (code === 'leave_insufficient_balance' && !args.year) return t(K.insufficient);
  if (code === 'leave_statutory_limit') {
    const days = Number(args.days);
    return args.days && Number.isFinite(days) ? t(K.statutory, { days }) : t(K.statutoryAny);
  }

  const rule = RULES[code];
  if (!rule) return t(requestFailureReason(err));
  if ((rule.needs ?? []).some((k) => !args[k])) return t(K.refused);
  const params: Record<string, string | number> = { ...args };
  for (const k of rule.numeric ?? []) {
    const n = Number(args[k]);
    if (!Number.isFinite(n)) return t(K.refused);
    params[k] = n;
  }
  return t(rule.key, params);
}
