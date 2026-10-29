export interface ProbationOverLimitError { error: 'probation_over_limit'; message: string; days: number; maxDays: number }

/** The advisory 409 for a probation longer than the company's maximum (Setup > Payroll rules), or null. */
export function probationOverLimitFromError(err: unknown): ProbationOverLimitError | null {
  const e = err as { response?: { status?: number; data?: { error?: string } } };
  if (e?.response?.status === 409 && e.response.data?.error === 'probation_over_limit') {
    return e.response.data as ProbationOverLimitError;
  }
  return null;
}
