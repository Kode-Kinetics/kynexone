/** The problem codes the onboarding template endpoints return; each has an English and an Arabic sentence (key onboarding.error.<code>). */
export const ONBOARDING_PROBLEM_CODES = [
  'template_name_taken', 'template_match_conflict', 'template_company_mismatch', 'task_evidence_required', 'due_override_reason_required',
  'activation_blocked_by_tasks', 'invalid_reference', 'validation_failed', 'duplicate_task_title',
] as const;

export function onboardingProblemKey(err: unknown): string {
  const data = (err as { response?: { data?: { code?: unknown; errors?: Record<string, unknown> } } } | null)?.response?.data;
  const code = typeof data?.code === 'string' ? data.code : '';
  if (code === 'validation_failed' && JSON.stringify(data?.errors ?? {}).includes('duplicate_task_title')) return 'onboarding.error.duplicate_task_title';
  return (ONBOARDING_PROBLEM_CODES as readonly string[]).includes(code) ? `onboarding.error.${code}` : 'onboarding.error.generic';
}
