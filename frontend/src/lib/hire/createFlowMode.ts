/** How long the Add Employee entry waits for the feature flags before it falls back to the legacy dialog. */
export const FLAGS_WAIT_MS = 4000;

export type CreateFlowMode = 'loading' | 'legacy' | 'wizard';

/**
 * Which Add Employee surface to show (CD57). The legacy dialog is the default. The new wizard needs a
 * positive answer from the flags (hire_wizard_v2 ON). While the flags load there is an accessible
 * loading state, never a blank screen; an errored or unanswered (past the wait) request means legacy.
 */
export function createFlowMode(input: { status: 'loading' | 'ready' | 'error'; wizardOn: boolean; waitedOut: boolean }): CreateFlowMode {
  if (input.status === 'ready') return input.wizardOn ? 'wizard' : 'legacy';
  if (input.status === 'error') return 'legacy';
  return input.waitedOut ? 'legacy' : 'loading';
}
