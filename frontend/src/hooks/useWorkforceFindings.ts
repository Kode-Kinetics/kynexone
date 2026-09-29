'use client';

/**
 * Open findings from the rules engine (AiInsightEngine — deterministic threshold checks,
 * hourly, stamped GeneratedBy="System"; no language model produces this text).
 *
 * Gating is fail-closed and was carried over from the old WorkforceBriefingPanel:
 *   - AI Assistant module off for the tenant  -> `enabled: false`, nothing fetched.
 *   - caller lacks ai.insights_view           -> `enabled: false`, nothing fetched (the endpoint
 *     would 403 and the shared client would raise a global access-denied toast).
 *   - fetch fails                             -> `failed: true`, empty list, no retry loop.
 * Callers render nothing for a disabled source rather than an empty "all clear".
 */

import { useCallback, useEffect, useRef, useState } from 'react';
import { aiAssistantApi } from '../api/intelligence';
import type { AIInsight, AIProviderStatus } from '../api/intelligence';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { useAuth } from '../contexts/AuthContext';

const PAGE_SIZE = 50;

export interface WorkforceFindings {
  enabled: boolean;
  insights: AIInsight[] | null;
  total: number;
  failed: boolean;
  reload: () => void;
}

export function useWorkforceFindings(active = true): WorkforceFindings {
  const { isFeatureEnabled } = useFeatureFlags();
  const { hasPermission } = useAuth();
  const enabled = isFeatureEnabled('ai_assistant') && hasPermission('ai.insights_view');

  const [insights, setInsights] = useState<AIInsight[] | null>(null);
  const [total, setTotal] = useState(0);
  const [failed, setFailed] = useState(false);
  const inFlight = useRef(false);

  const load = useCallback(() => {
    if (inFlight.current) return;
    inFlight.current = true;
    aiAssistantApi
      .listInsights({ acknowledged: false, pageSize: PAGE_SIZE })
      .then((r) => {
        setInsights(r.items ?? []);
        setTotal(r.total ?? r.items?.length ?? 0);
        setFailed(false);
      })
      .catch(() => {
        setInsights([]);
        setFailed(true);
      })
      .finally(() => { inFlight.current = false; });
  }, []);

  useEffect(() => {
    if (enabled && active) load();
  }, [enabled, active, load]);

  return { enabled, insights: enabled ? insights : null, total, failed, reload: load };
}

/** Tri-state provider probe: null = not yet known; a failed probe reads as disabled. */
export function useAssistantProvider(active: boolean): AIProviderStatus | null {
  const [status, setStatus] = useState<AIProviderStatus | null>(null);
  useEffect(() => {
    if (!active || status) return;
    aiAssistantApi.status().then(setStatus).catch(() => setStatus({ enabled: false, provider: 'unknown' }));
  }, [active, status]);
  return status;
}
