'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  getRunProcessingStatus, isProcessingActive,
  type ProcessingStatus,
} from '../api/payrollProcessing';
import { POLL_HIDDEN_MS, pollDelay } from '../lib/payrollPipeline';

/**
 * The processing job of one payroll run, kept current.
 *
 * Polls every 2 s while the job is Queued or Running, backs off to 15 s while the browser tab is
 * hidden (and refreshes the moment it is shown again), and stops on unmount, on a settled status, and
 * when the endpoint does not exist. `refresh()` re-reads at once (call it after queueing a job).
 */
export function useRunProcessing(runId: string | null, version = 0) {
  const [status, setStatus] = useState<ProcessingStatus | null>(null);
  const [unavailable, setUnavailable] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const alive = useRef(true);
  const seq = useRef(0);
  const lastActive = useRef(false);
  const [tick, setTick] = useState(0);

  const refresh = useCallback(() => setTick(n => n + 1), []);

  // A different run starts from a blank slate; a refresh of the same run keeps the last known status.
  useEffect(() => {
    setStatus(null);
    setUnavailable(false);
    lastActive.current = false;
  }, [runId]);

  useEffect(() => {
    alive.current = true;
    const mine = ++seq.current;
    const stop = () => { if (timer.current) { clearTimeout(timer.current); timer.current = null; } };
    if (!runId) return stop;

    const poll = async () => {
      stop();
      let next: ProcessingStatus | null = null;
      let keepGoing = false;
      try {
        next = await getRunProcessingStatus(runId);
        if (!alive.current || seq.current !== mine) return;
        setStatus(next);
        setUnavailable(false);
        keepGoing = isProcessingActive(next?.status);
        lastActive.current = keepGoing;
      } catch (e) {
        if (!alive.current || seq.current !== mine) return;
        const code = (e as { response?: { status?: number } })?.response?.status;
        // 404/405: this server has no processing endpoint. Not a transient fault, so do not hammer it.
        if (code === 404 || code === 405) { setUnavailable(true); return; }
        // A blip while a job is running: keep the last known status and try again, slowly.
        setUnavailable(true);
        if (lastActive.current) timer.current = setTimeout(poll, POLL_HIDDEN_MS);
        return;
      }
      if (keepGoing) timer.current = setTimeout(poll, pollDelay(typeof document !== 'undefined' && document.hidden));
    };

    const onVisible = () => {
      // Back in front: catch up now instead of waiting out the slow hidden-tab delay.
      if (!document.hidden && timer.current) void poll();
    };
    document.addEventListener('visibilitychange', onVisible);
    void poll();
    return () => {
      alive.current = false;
      stop();
      document.removeEventListener('visibilitychange', onVisible);
    };
  }, [runId, tick, version]);

  return { status, unavailable, refresh };
}
