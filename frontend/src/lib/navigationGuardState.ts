export interface NavigationIntent {
  destination: string;
  run: () => void | Promise<void>;
  restoreFocus?: HTMLElement | null;
}

export interface NavigationGuardSnapshot {
  intent: NavigationIntent | null;
  busy: boolean;
  error: string;
  sequence: number;
}

type Listener = () => void;

export interface NavigationGuardState {
  snapshot(): NavigationGuardSnapshot;
  subscribe(listener: Listener): () => void;
  pending: NavigationIntent | null;
  busy: boolean;
  error: string;
  sequence: number;
  bypassDestination: string | null;
  listeners: Set<Listener>;
}

export function createNavigationGuardState(): NavigationGuardState {
  const state: NavigationGuardState = {
    pending: null,
    busy: false,
    error: '',
    sequence: 0,
    bypassDestination: null,
    listeners: new Set(),
    snapshot: () => ({ intent: state.pending, busy: state.busy, error: state.error, sequence: state.sequence }),
    subscribe: (listener) => {
      state.listeners.add(listener);
      return () => state.listeners.delete(listener);
    },
  };
  return state;
}

function notify(state: NavigationGuardState) {
  state.listeners.forEach((listener) => listener());
}

export function queueIntent(state: NavigationGuardState, intent: NavigationIntent): 'queued' | 'bypass' {
  if (state.bypassDestination === intent.destination) {
    state.bypassDestination = null;
    return 'bypass';
  }
  if (state.busy) return 'queued';
  state.pending = intent;
  state.error = '';
  state.sequence += 1;
  notify(state);
  return 'queued';
}

export function cancelIntent(state: NavigationGuardState) {
  if (state.busy) return;
  const focusTarget = state.pending?.restoreFocus;
  state.pending = null;
  state.error = '';
  notify(state);
  focusTarget?.focus();
}

export async function confirmIntent(
  state: NavigationGuardState,
  discarders: ReadonlyArray<() => void | Promise<void>>,
  fallbackError = 'Navigation could not be completed.',
  errorOverride?: string,
) {
  if (state.busy || !state.pending) return;
  const intent = state.pending;
  state.busy = true;
  state.error = '';
  state.bypassDestination = intent.destination;
  notify(state);
  try {
    for (const discard of discarders) await discard();
    await intent.run();
    if (state.pending === intent) state.pending = null;
    state.error = '';
  } catch (error) {
    state.bypassDestination = null;
    state.error = errorOverride ?? (error instanceof Error && error.message ? error.message : fallbackError);
    throw error;
  } finally {
    state.busy = false;
    if (!state.pending) state.bypassDestination = null;
    notify(state);
  }
}
