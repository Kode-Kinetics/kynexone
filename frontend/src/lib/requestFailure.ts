/**
 * Why a request failed, in words an operator can act on. A missing permission, an unreachable
 * server and a server-side refusal each need a different next step, so they are never collapsed
 * into one message — and never into an empty list, which is what a swallowed error used to render.
 */
export function requestFailureReason(err: unknown): string {
  const e = err as { isAxiosError?: boolean; response?: { status?: number; data?: { message?: string } } } | null;
  const status = e?.response?.status;
  if (status === 403) return 'You do not have permission for this.';
  if (e?.isAxiosError && !e.response) return 'The server could not be reached. Check your connection, then retry.';
  if (e?.response) return e.response.data?.message ?? `The server returned an error (HTTP ${status}).`;
  return 'Something went wrong. Please retry.';
}
