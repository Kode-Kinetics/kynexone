import { apiErrorReason } from '../api/client';

type Tr = (key: string, params?: Record<string, string | number | null | undefined>) => string;

/** The refusals the performance API gives a machine-readable `error` code for. Each has a sentence in both languages (apr.error.*). */
export const REFUSAL_CODES = [
  'acknowledge_by_employee_only', 'appeal_by_employee_only', 'appeal_already_open', 'appeal_reason_required',
  'no_monitoring_evidence', 'pip_closed', 'reviewer_not_linked', 'reviewer_mismatch', 'self_feedback',
  'feedback360_not_enabled', 'feedback360_closed', 'reviewer_outside_company', 'reviewer_role_mismatch',
  'invalid_recommendation', 'employee_not_found', 'invalid_date_range', 'cycle_action_requires_group_scope',
  'invalid_outcome', 'invalid_status', 'reason_required',
] as const;

/** The `error` code in an API refusal body, when it is one this screen has a sentence for. */
export function refusalCode(err: unknown): string | null {
  const data = (err as { response?: { data?: unknown } } | null)?.response?.data;
  const code = data && typeof data === 'object' ? (data as { error?: unknown; code?: unknown }).error ?? (data as { code?: unknown }).code : null;
  return typeof code === 'string' && (REFUSAL_CODES as readonly string[]).includes(code) ? code : null;
}

/** What went wrong, in the viewer's language: the sentence for a known code, otherwise the server's own reason, otherwise the fallback. */
export function appraisalRefusal(err: unknown, t: Tr, fallback: string, locale?: string): string {
  const code = refusalCode(err);
  return code ? t(`apr.error.${code}`) : apiErrorReason(err, fallback, locale);
}
