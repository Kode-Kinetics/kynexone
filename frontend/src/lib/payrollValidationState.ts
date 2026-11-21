/**
 * What the Validation tab shows when it has no findings to list. An empty list means three different things, and only
 * one of them is "nothing was found": a Draft run has never been validated (the server refuses to validate it until it is
 * processed), a failed read tells nothing, and a read still in flight has not answered.
 */
export type ValidationPanelState = 'select_run' | 'loading' | 'unavailable' | 'not_run' | 'none_saved';

export function validationPanelState(input: { runId: string; status: string | undefined; loading: boolean; failed: boolean }): ValidationPanelState {
  if (!input.runId) return 'select_run';
  if (input.loading) return 'loading';
  if (input.failed) return 'unavailable';
  if (input.status === 'Draft') return 'not_run';
  return 'none_saved';
}
