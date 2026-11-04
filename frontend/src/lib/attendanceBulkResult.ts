/**
 * Small, pure judgements about the outcome of a bulk call, so a screen never paints a bad result green.
 */
export type OutcomeTone = 'emerald' | 'amber' | 'rose';

/** Rose when nothing succeeded and something failed, amber when some failed, emerald otherwise. */
export function outcomeTone(succeeded: number, failed: number): OutcomeTone {
  if (failed > 0) return succeeded === 0 ? 'rose' : 'amber';
  return 'emerald';
}

/**
 * Deploy-skew guard. The frontend can ship before the backend: an older API ignores `employeeIds` and
 * processes everyone. For a "selected" run, a job result that reports a different employee count than the
 * selection is therefore not success. Returns the two numbers to show, or null when nothing is amiss
 * (or the result does not say how many were processed).
 */
export function processedMismatch(input: { scope: 'all' | 'selected'; expected: number; result: Record<string, unknown> | null }): { processed: number; expected: number } | null {
  if (input.scope !== 'selected') return null;
  const processed = input.result?.employees;
  if (typeof processed !== 'number' || !Number.isFinite(processed)) return null;
  return processed === input.expected ? null : { processed, expected: input.expected };
}
