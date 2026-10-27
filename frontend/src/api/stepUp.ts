/**
 * "Confirm it's you" (POST /api/auth/step-up → X-Step-Up header). Before an action against an Admin or the workspace
 * owner, the API answers 403 { code: 'step_up_required' }. The API client then asks the mounted StepUpHost to collect
 * the password (and two-step code), keeps the five-minute token here — in memory only, never in storage — and repeats
 * the request once. The refused request changed nothing, so repeating it is safe.
 */

export const STEP_UP_HEADER = 'X-Step-Up';
export const STEP_UP_REQUIRED = 'step_up_required';

let token: { value: string; expiresAt: number } | null = null;
let handler: (() => Promise<boolean>) | null = null;
let pending: Promise<boolean> | null = null;

/** The current token, while it is still comfortably inside its lifetime. */
export function currentStepUpToken(): string | null {
  if (!token || Date.now() > token.expiresAt - 15_000) return null;
  return token.value;
}

export function setStepUpToken(value: string, expiresInSeconds: number) {
  token = { value, expiresAt: Date.now() + Math.max(0, expiresInSeconds) * 1000 };
}

export function clearStepUpToken() {
  token = null;
}

/** The dialog host registers itself; the returned function unregisters it. */
export function registerStepUpHandler(next: () => Promise<boolean>): () => void {
  handler = next;
  return () => { if (handler === next) handler = null; };
}

/** Ask the person to confirm it's them. Concurrent refusals share one dialog. False when cancelled or no host is mounted. */
export function requestStepUp(): Promise<boolean> {
  if (!handler) return Promise.resolve(false);
  pending ??= handler().finally(() => { pending = null; });
  return pending;
}

export function isStepUpRequired(err: unknown): boolean {
  const response = (err as { response?: { status?: number; data?: { code?: unknown; error?: unknown } } } | null)?.response;
  return response?.status === 403 && (response.data?.code === STEP_UP_REQUIRED || response.data?.error === STEP_UP_REQUIRED);
}
