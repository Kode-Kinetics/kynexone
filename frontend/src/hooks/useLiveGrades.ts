'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { gradesApi, type GradeDto } from '../api/organization';
import { subscribeGradesChanged } from '../lib/gradeEvents';

/** Focus and visibilitychange usually fire together when a tab comes back; one refetch is enough. */
const RETURN_REFRESH_MIN_MS = 1500;

/**
 * The tenant's grades, kept current while a form is open: refetched when Setup reports a grade change (this tab
 * or another), and when the user returns to the tab or window. Callers filter (e.g. active only) themselves.
 * A failed refresh keeps the last good list; only a failed first load leaves it empty.
 */
export function useLiveGrades(): { grades: GradeDto[]; failed: boolean; reload: () => void } {
  const [grades, setGrades] = useState<GradeDto[]>([]);
  const [failed, setFailed] = useState(false);
  const requestRef = useRef(0);
  const lastReturnRefreshRef = useRef(0);

  const reload = useCallback(() => {
    const request = ++requestRef.current;
    gradesApi.listAll()
      .then((rows) => { if (request === requestRef.current) { setGrades(rows); setFailed(false); } })
      .catch(() => { if (request === requestRef.current) setFailed(true); });
  }, []);

  useEffect(() => {
    reload();
    const unsubscribe = subscribeGradesChanged(reload);
    const onReturn = () => {
      if (typeof document !== 'undefined' && document.visibilityState === 'hidden') return;
      const now = Date.now();
      if (now - lastReturnRefreshRef.current < RETURN_REFRESH_MIN_MS) return;
      lastReturnRefreshRef.current = now;
      reload();
    };
    window.addEventListener('focus', onReturn);
    document.addEventListener('visibilitychange', onReturn);
    return () => {
      // Invalidate any request still in flight so it cannot write after unmount.
      requestRef.current++;
      unsubscribe();
      window.removeEventListener('focus', onReturn);
      document.removeEventListener('visibilitychange', onReturn);
    };
  }, [reload]);

  return { grades, failed, reload };
}
