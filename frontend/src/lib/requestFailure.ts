import { isMalformedListResponse } from './listResponse';

/**
 * Why a request failed, in words an operator can act on. A missing permission, an unreachable
 * server and a server-side refusal each need a different next step, so they are never collapsed
 * into one message — and never into an empty list, which is what a swallowed error used to render.
 */
export function requestFailureReason(err: unknown): string {
  const e = err as { isAxiosError?: boolean; response?: { status?: number; data?: { message?: string } } } | null;
  const status = e?.response?.status;
  // A 403 keeps the server's own reason ("The user who processed this run cannot approve it"):
  // "no permission" alone does not tell the user what to do next. A bare status word adds nothing.
  if (status === 403) {
    const raw = e?.response?.data?.message;
    const reason = typeof raw === 'string' ? raw.trim() : '';
    return reason && !/^(forbidden|access denied\.?)$/i.test(reason)
      ? `You do not have permission for this. ${reason}`
      : 'You do not have permission for this.';
  }
  if (isMalformedListResponse(err)) return 'The server sent an unexpected reply. Retry in a moment.';
  if (e?.isAxiosError && !e.response) return 'The server could not be reached. Check your connection, then retry.';
  if (e?.response) return e.response.data?.message ?? `The server returned an error (HTTP ${status}).`;
  return 'Something went wrong. Please retry.';
}
