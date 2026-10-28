// ============================================================
// KynexOne Mobile — "choose your own password" (admin temporary password)
// ============================================================
//
// Pure and import-free so it runs under node --test. Nothing here logs,
// stores or forwards a password or token; the reset token lives in
// navigation params (memory) only.

export const PASSWORD_CHANGE_MIN_LENGTH = 10;

export type PasswordChangeFieldError = 'tooShort' | 'mismatch';

/** Client-side check before any request is made. Length first, then match. */
export function validateNewPassword(newPassword: string, confirm: string): PasswordChangeFieldError | null {
  if (newPassword.length < PASSWORD_CHANGE_MIN_LENGTH) return 'tooShort';
  if (newPassword !== confirm) return 'mismatch';
  return null;
}

export type ResetFailure =
  /** 401: the token is spent or expired; send the person back to sign in. */
  | { kind: 'expired' }
  /** 400: policy or same-as-temporary; show the server's own message (null if it sent none). */
  | { kind: 'rejected'; message: string | null }
  /** Anything else (network, 429, 5xx): the token may still be valid, stay on the form. */
  | { kind: 'retry'; reason: 'rateLimited' | 'network' | 'server' };

/** Classifies a failed POST /auth/reset-password (axios-shaped error). */
export function classifyResetFailure(error: unknown): ResetFailure {
  const record = error && typeof error === 'object' ? (error as Record<string, unknown>) : null;
  const response = record?.response && typeof record.response === 'object' ? (record.response as Record<string, unknown>) : null;
  const status = typeof response?.status === 'number' ? response.status : undefined;
  if (status === undefined) return { kind: 'retry', reason: record?.isAxiosError === true || record?.request ? 'network' : 'server' };
  if (status === 401) return { kind: 'expired' };
  if (status === 400) {
    const data = response?.data && typeof response.data === 'object' ? (response.data as Record<string, unknown>) : null;
    const message = typeof data?.message === 'string' && data.message.trim() ? data.message.trim() : null;
    return { kind: 'rejected', message };
  }
  if (status === 429) return { kind: 'retry', reason: 'rateLimited' };
  return { kind: 'retry', reason: 'server' };
}

export type SignInNotice = 'passwordSaved' | 'passwordChangeExpired';

/**
 * After the password was saved, what the re-login produced. The token is already
 * spent, so the only safe outcomes are "continue" or "back to sign in with a notice".
 */
export function afterPasswordSaved(relogin: { ok: true; kind: string } | { ok: false }): 'continue' | 'signInWithNotice' {
  if (!relogin.ok) return 'signInWithNotice';
  // A second passwordChange reply cannot be acted on (new token needs the temp password).
  return relogin.kind === 'passwordChange' ? 'signInWithNotice' : 'continue';
}

export type SignInProbe = { ok: true; kind: string } | { ok: false; status?: number };

/**
 * The reset request failed without a clear answer (network, 5xx): the server may have saved the new password and spent
 * the token. The screen signs in with the new password to find out. Worked → it was saved, carry on; a definite 401 →
 * it was not, stay and retry (the token is still good); anything else → back to sign-in, telling the person to try the
 * new password first and the temporary one if that fails.
 */
export function afterAmbiguousReset(probe: SignInProbe): 'continue' | 'stayAndRetry' | 'signInMaybeSaved' {
  if (probe.ok) return probe.kind === 'passwordChange' ? 'stayAndRetry' : 'continue';
  return probe.status === 401 ? 'stayAndRetry' : 'signInMaybeSaved';
}
