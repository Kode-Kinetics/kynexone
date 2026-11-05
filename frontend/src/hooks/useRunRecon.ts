'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { payrollReconApi, type ReconDto } from '../api/payrollRecon';
import { parseRecon, parseReconError } from '../lib/payrollRecon';
import { requestFailureReason } from '../lib/requestFailure';

export type ReconLoadState = 'idle' | 'loading' | 'ready' | 'failed' | 'not_applicable';

/**
 * The reconciliation of one payroll run against its prior month. Nothing is read until `enabled`.
 *
 * Honest by construction: while a read is in flight there is no reconciliation (not the last one), a
 * reply of the wrong shape is a failure (not an empty bridge), and a failure carries the server's own
 * reason. `reload()` reads again; so does a change of run, comparison run, or `version`.
 */
export function useRunRecon(runId: string | null, opts: { enabled: boolean; priorRunId?: string | null; version?: number }) {
  const [recon, setRecon] = useState<ReconDto | null>(null);
  const [state, setState] = useState<ReconLoadState>('idle');
  const [error, setError] = useState('');
  const [tick, setTick] = useState(0);
  const seq = useRef(0);
  const loadedFor = useRef('');
  const reload = useCallback(() => setTick(n => n + 1), []);
  const { enabled, priorRunId = null, version = 0 } = opts;

  useEffect(() => {
    const mine = ++seq.current;
    const key = `${runId ?? ''}|${priorRunId ?? ''}`;
    setError('');
    // A different run or comparison starts blank. A re-read of the same one keeps what is on screen until the
    // fresh reply lands, so a form being used is not torn down by the reload it asked for.
    const sameTarget = loadedFor.current === key;
    if (!sameTarget) setRecon(null);
    if (!runId || !enabled) { loadedFor.current = ''; setRecon(null); setState('idle'); return; }
    loadedFor.current = key;
    setState(s => (sameTarget && s === 'ready' ? 'ready' : 'loading'));
    payrollReconApi.get(runId, priorRunId)
      .then(raw => {
        if (seq.current !== mine) return;
        const parsed = parseRecon(raw);
        if (!parsed) { setRecon(null); setState('failed'); setError('The server sent an unexpected reply. Retry in a moment.'); return; }
        if (parsed.mode === 'NotApplicable') { setRecon(null); setState('not_applicable'); return; }
        setRecon(parsed); setState('ready');
      })
      .catch(e => {
        if (seq.current !== mine) return;
        setRecon(null);
        if (parseReconError(e).code === 'recon_not_applicable') { setState('not_applicable'); return; }
        setState('failed'); setError(requestFailureReason(e));
      });
  }, [runId, enabled, priorRunId, version, tick]);

  return { recon, state, error, reload };
}
