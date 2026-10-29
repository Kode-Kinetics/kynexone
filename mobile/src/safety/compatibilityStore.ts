import { create } from 'zustand';

/**
 * Set when a server reply no longer matches what this app version understands (schema mismatch)
 * or the server answers 426 Upgrade Required. `blocking` means the session itself could not be
 * read, so there is nothing safe to show but the "please update" screen.
 */
interface CompatibilityState {
  updateRequired: boolean;
  blocking: boolean;
  dismissed: boolean;
  flag: (blocking?: boolean) => void;
  dismiss: () => void;
  reset: () => void;
}

export const useCompatibilityStore = create<CompatibilityState>((set) => ({
  updateRequired: false,
  blocking: false,
  dismissed: false,
  flag: (blocking = false) =>
    set((s) => ({ updateRequired: true, blocking: s.blocking || blocking })),
  dismiss: () => set({ dismissed: true }),
  reset: () => set({ updateRequired: false, blocking: false, dismissed: false }),
}));
