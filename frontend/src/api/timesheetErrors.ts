export { notifyApiError } from './client';

export interface TimesheetApiViolation {
  date: string | null;
  code: string;
  message: string;
}

export interface ParsedTimesheetError {
  code: string;
  message: string;
  violations: TimesheetApiViolation[];
}

/**
 * Timesheet endpoints answer a refusal with `{ code, message, violations[] }` rather than a bare
 * string, because the interesting refusal — "these three days claim more time than attendance
 * recorded" — is per-day. Flattening it into one sentence is how a form ends up telling someone
 * "invalid" and nothing else, so the violations survive parsing and the UI lists them.
 */
export function parseApiErrorForTimesheets(err: unknown, fallback: string): ParsedTimesheetError {
  const data = (err as { response?: { data?: unknown } } | undefined)?.response?.data;

  if (data && typeof data === 'object') {
    const body = data as { code?: unknown; message?: unknown; violations?: unknown; title?: unknown };
    const violations = Array.isArray(body.violations)
      ? body.violations
          .filter((v): v is Record<string, unknown> => typeof v === 'object' && v !== null)
          .map((v) => ({
            date: typeof v.date === 'string' ? v.date : null,
            code: typeof v.code === 'string' ? v.code : 'violation',
            message: typeof v.message === 'string' ? v.message : fallback,
          }))
      : [];
    const message =
      (typeof body.message === 'string' && body.message) ||
      (typeof body.title === 'string' && body.title) ||
      fallback;
    return { code: typeof body.code === 'string' ? body.code : 'error', message, violations };
  }

  if (typeof data === 'string' && data.trim() !== '') {
    return { code: 'error', message: data, violations: [] };
  }

  return {
    code: 'error',
    message: err instanceof Error && err.message ? err.message : fallback,
    violations: [],
  };
}
