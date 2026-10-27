export type AutosaveState = 'idle' | 'pending' | 'saving' | 'saved' | 'conflict' | 'error';

export interface AutosaveOptions<P> {
  delayMs?: number;
  schedule?: (fn: () => void, ms: number) => unknown;
  cancel?: (id: unknown) => void;
  /** Saves `payload` against the version the previous save returned. Throws an axios-like error; 409 = changed elsewhere. */
  save: (payload: P, baseUpdatedAtUtc: string) => Promise<{ updatedAtUtc: string }>;
  onState?: (state: AutosaveState, savedAtUtc?: string) => void;
}

/**
 * Debounced, version-checked autosave for the server-side hire draft (pattern of companySetup/useDraftAutosave).
 * Nothing is saved until a draft row exists (`adopt`). A 409 stops autosave for good: it never overwrites the other copy.
 */
export function createAutosave<P>(options: AutosaveOptions<P>) {
  const delay = options.delayMs ?? 1200;
  const schedule = options.schedule ?? ((fn, ms) => setTimeout(fn, ms));
  const cancel = options.cancel ?? (id => clearTimeout(id as ReturnType<typeof setTimeout>));
  let version: string | null = null;
  let latest: P | undefined;
  let dirty = false;
  let conflict = false;
  let timer: unknown = null;
  let inFlight: Promise<boolean> | null = null;
  const emit = (s: AutosaveState, at?: string) => options.onState?.(s, at);

  const save = async (): Promise<boolean> => {
    if (conflict) return false;
    if (inFlight) { await inFlight; }
    if (!dirty) return true;
    if (version === null) return false;
    dirty = false;
    emit('saving');
    const sent = latest as P;
    const run = options.save(sent, version).then(res => {
      if (res.updatedAtUtc?.trim()) version = res.updatedAtUtc;
      emit(dirty ? 'pending' : 'saved', res.updatedAtUtc);
      return true;
    }).catch(err => {
      if ((err as { response?: { status?: number } })?.response?.status === 409) { conflict = true; emit('conflict'); return false; }
      dirty = true;
      emit('error');
      return false;
    }).finally(() => { inFlight = null; });
    inFlight = run;
    const ok = await run;
    if (ok && dirty) return save();
    return ok;
  };

  return {
    /** A draft row exists: this is the version the server holds. */
    adopt(updatedAtUtc: string) { version = updatedAtUtc?.trim() ? updatedAtUtc : null; conflict = false; dirty = false; emit('saved', updatedAtUtc); },
    markDirty(payload: P) {
      latest = payload; dirty = true;
      if (conflict || version === null) return;
      emit('pending');
      if (timer !== null) cancel(timer);
      timer = schedule(() => { timer = null; void save(); }, delay);
    },
    async flush() {
      if (timer !== null) { cancel(timer); timer = null; }
      return save();
    },
    hasUnsaved: () => dirty || !!inFlight,
    isConflict: () => conflict,
    version: () => version,
    dispose() { if (timer !== null) { cancel(timer); timer = null; } },
  };
}
