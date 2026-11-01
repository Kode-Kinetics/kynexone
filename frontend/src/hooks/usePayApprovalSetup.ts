'use client';

import { useCallback, useEffect, useState } from 'react';
import { payApprovalsApi, type PayApprovalSetup } from '../api/payApprovals';
import { companySetupApi, type ReadinessItem } from '../api/companySetup';
import { reviewerVerification } from '../lib/payReviewerCheck';

export interface PayApprovalSetupState {
  data: PayApprovalSetup | null;
  loading: boolean;
  error: boolean;
  reload: () => void;
}

/**
 * Who can review pay for one company (GET /api/pay-approvals/setup). Reloads when `revision` changes, on demand, and when
 * the window regains focus: the person usually leaves to finish a sign-in and comes back. A failed or malformed reply
 * is `error`, never an empty "ready" answer.
 */
export function usePayApprovalSetup(companyId: string | null, revision?: unknown): PayApprovalSetupState {
  const [data, setData] = useState<PayApprovalSetup | null>(null);
  const [error, setError] = useState(false);
  const [loading, setLoading] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const reload = useCallback(() => setRefresh(n => n + 1), []);
  useEffect(() => {
    let current = true;
    setData(null); setError(false);
    if (!companyId) { setLoading(false); return; }
    setLoading(true);
    payApprovalsApi.setup(companyId).then(value => { if (current) setData(value); })
      .catch(() => { if (current) setError(true); })
      .finally(() => { if (current) setLoading(false); });
    return () => { current = false; };
  }, [companyId, revision, refresh]);
  useEffect(() => {
    window.addEventListener('focus', reload);
    return () => window.removeEventListener('focus', reload);
  }, [reload]);
  return { data, loading, error, reload };
}

/**
 * Whether a usable pay reviewer exists for an existing company: the pay-approval answer plus the setup readiness item
 * that says why a created reviewer is not usable yet. Either call failing is `error`, with a single `reload`.
 */
export function useReviewerVerification(companyId: string | null, revision?: unknown) {
  const setup = usePayApprovalSetup(companyId, revision);
  const [readiness, setReadiness] = useState<ReadinessItem[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [loading, setLoading] = useState(false);
  const { reload: reloadSetup } = setup;
  const [tick, setTick] = useState(0);
  useEffect(() => {
    let current = true;
    setReadiness(null); setFailed(false);
    if (!companyId) { setLoading(false); return; }
    setLoading(true);
    companySetupApi.readiness(companyId).then(rows => { if (current) setReadiness(rows); })
      .catch(() => { if (current) setFailed(true); })
      .finally(() => { if (current) setLoading(false); });
    return () => { current = false; };
  }, [companyId, revision, tick]);
  useEffect(() => {
    const again = () => setTick(n => n + 1);
    window.addEventListener('focus', again);
    return () => window.removeEventListener('focus', again);
  }, []);
  const reload = useCallback(() => { setTick(n => n + 1); reloadSetup(); }, [reloadSetup]);
  const result = reviewerVerification({ setup: setup.data, readiness, loading: loading || setup.loading, failed: failed || setup.error });
  return { result, reload };
}
