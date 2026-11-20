/** Pure rules of the People list's bulk actions (no React), so a unit test can pin them. */

export type BulkMutation = 'activate' | 'deactivate' | 'delete';

type ErrorLike = { response?: { status?: number; data?: { message?: string } } };

const GENERIC = 'The bulk action could not be completed.';

/**
 * One English sentence for a failed bulk call; the screen renders it through t(). A message the server sent always wins.
 * A dropped connection or a 5xx does not say whether anything was applied, so those sentences never claim "nothing changed".
 */
export function bulkFailureMessage(error: unknown, action: BulkMutation): string {
  const response = (error as ErrorLike | null)?.response;
  if (!response) return 'The server did not answer. Refresh the list to see whether anything changed, then try again.';
  const serverMessage = response.data?.message;
  if (serverMessage) return serverMessage;
  const status = response.status ?? 0;
  if (status === 429) return 'Too many requests. Wait a moment and try again.';
  if (status === 403) {
    return action === 'activate' ? 'You do not have permission to activate employees.'
      : action === 'deactivate' ? 'You do not have permission to deactivate employees.'
        : 'You do not have permission to delete employees.';
  }
  if (status >= 500) return 'The server hit a problem. Refresh the list to see whether anything changed, then try again.';
  return GENERIC;
}

export interface SelectedRowReadiness { status: string; readinessState: string; activationBlockersCount: number }

/**
 * Of the rows picked, how many are Draft employees the list already knows cannot be activated yet (the Profile column says
 * Incomplete or Needs attention: missing details or pay approval). A hint only: the server still decides.
 */
export function countCannotActivateYet(selected: readonly SelectedRowReadiness[]): { blocked: number; total: number } {
  let blocked = 0;
  for (const row of selected) {
    if (row.status !== 'Draft') continue;
    if (row.readinessState === 'Ready') continue;
    if (row.readinessState === 'Blocked' || row.readinessState === 'NeedsAttention' || row.readinessState === 'NotReady' || row.activationBlockersCount > 0) blocked++;
  }
  return { blocked, total: selected.length };
}
