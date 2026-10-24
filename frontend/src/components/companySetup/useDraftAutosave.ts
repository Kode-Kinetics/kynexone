'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { companySetupApi, httpStatus, type CompanySetupDraft, type CompanySetupDraftBody } from '../../api/companySetup';

export type SaveState = 'idle' | 'pending' | 'saving' | 'saved' | 'conflict' | 'error';

/**
 * Debounced server autosave for a create-company draft, with optimistic concurrency.
 *
 * Every PUT carries the version this screen last saw. A 409 means another tab or device saved the
 * draft since: autosave stops (it never overwrites the other copy) and the screen offers a reload.
 * `flush()` saves now and resolves true only when the server holds exactly what is on screen — the
 * wizard awaits it before "Save draft & exit" and before Create, whose commit names the version.
 */
export function useDraftAutosave({ draftId, body, step, delayMs = 1200 }: {
  draftId: string | null;
  body: CompanySetupDraftBody;
  step: number;
  delayMs?: number;
}) {
  const [state, setState] = useState<SaveState>('idle');
  const [savedAt, setSavedAt] = useState<string | null>(null);
  const version = useRef(0);
  const latest = useRef({ body, step });
  latest.current = { body, step };
  const dirty = useRef(false);
  /** The body/step the server is known to hold, by reference; an effect run on it is not a change. */
  const baseline = useRef<{ body: CompanySetupDraftBody | null; step: number }>({ body: null, step: 0 });
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const inFlight = useRef<Promise<boolean> | null>(null);
  const conflict = useRef(false);
  const idRef = useRef(draftId);
  idRef.current = draftId;

  /**
   * Adopt a draft the server just returned (create, load, reload): its version, and nothing dirty.
   * The caller must put `draft.body` (this exact object) and `draft.step` on screen.
   */
  const adopt = useCallback((draft: CompanySetupDraft) => {
    version.current = draft.version;
    dirty.current = false;
    conflict.current = false;
    baseline.current = { body: draft.body, step: draft.step };
    setSavedAt(draft.updatedAtUtc);
    setState('saved');
  }, []);

  const save = useCallback(async (): Promise<boolean> => {
    const id = idRef.current;
    if (!id || conflict.current) return !conflict.current && !dirty.current;
    if (inFlight.current) { await inFlight.current; if (!dirty.current) return true; }
    if (!dirty.current) return true;
    dirty.current = false;
    setState('saving');
    const { body: sentBody, step: sentStep } = latest.current;
    const run = companySetupApi.saveDraft(id, { version: version.current, step: sentStep, body: sentBody })
      .then(saved => {
        version.current = saved.version;
        setSavedAt(saved.updatedAtUtc);
        setState(dirty.current ? 'pending' : 'saved');
        return true;
      })
      .catch(err => {
        if (httpStatus(err) === 409) { conflict.current = true; setState('conflict'); return false; }
        dirty.current = true;
        setState('error');
        return false;
      })
      .finally(() => { inFlight.current = null; });
    inFlight.current = run;
    const ok = await run;
    // Typing during the PUT leaves the newer copy dirty: send it too before reporting success.
    if (ok && dirty.current) return save();
    return ok;
  }, []);

  useEffect(() => {
    if (!draftId) return;
    if (body === baseline.current.body && step === baseline.current.step) return;
    if (conflict.current) return;
    dirty.current = true;
    setState('pending');
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => { timer.current = null; void save(); }, delayMs);
  }, [body, step, draftId, delayMs, save]);

  // Leaving the wizard inside the app (a link, the sidebar) unmounts it: a change still waiting for its debounce is
  // sent now, never dropped. A full page unload is the beforeunload prompt below.
  useEffect(() => () => {
    if (timer.current) { clearTimeout(timer.current); timer.current = null; void save(); }
  }, [save]);

  // Leaving with an unsaved change: the browser asks first. Saved work never prompts.
  useEffect(() => {
    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      if (!dirty.current && !inFlight.current) return;
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, []);

  const flush = useCallback(async () => {
    if (timer.current) { clearTimeout(timer.current); timer.current = null; }
    return save();
  }, [save]);

  // isConflict reads the ref, so a caller awaiting flush() sees a 409 at once, not after the next render.
  return { state, savedAt, flush, adopt, version: () => version.current, hasUnsaved: () => dirty.current || !!inFlight.current, isConflict: () => conflict.current };
}
