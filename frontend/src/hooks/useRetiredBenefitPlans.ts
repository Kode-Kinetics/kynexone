'use client';

import { useEffect, useMemo, useState } from 'react';
import { benefitsApi } from '../api/benefits';
import { useAuth } from '../contexts/AuthContext';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { RELEASE_A_FLAG } from '../lib/releaseA';
import { SETUP_MODULE_SECTIONS, moduleSectionAvailable, type SetupModuleSectionId } from '../lib/setupModuleSections';

const PLANS = SETUP_MODULE_SECTIONS.find(section => section.id === 'benefitPlans')!;

/**
 * Whether a Release A tenant still has benefit plans to read. Benefit plans is retired for Release A (its benefits are set in
 * Benefits by grade), but plans made before stay as history, and the people who could open the section can still read them.
 * Asks the plan list once, and only when the answer matters (`wanted`), the tenant is on Release A and this viewer would
 * otherwise be allowed into the section. A failed read counts as "none": the section stays closed, as for a tenant with no plans.
 */
export function useRetiredBenefitPlans(wanted: boolean): {
  /** `checking` until the answer is known; `idle` when it does not apply. */
  status: 'idle' | 'checking' | 'ready';
  inUse: boolean;
  /** For SetupModuleAccess.retiredInUse. */
  retiredInUse: readonly SetupModuleSectionId[];
} {
  const { hasPermission, hasRole } = useAuth();
  const { status, isFeatureEnabled, verdictForPath } = useFeatureFlags();
  const mayOpen = useMemo(() => wanted && status === 'ready' && isFeatureEnabled(RELEASE_A_FLAG)
    && moduleSectionAvailable(PLANS, {
      hasPermission, hasRole, isFeatureEnabled, verdictForPath, flagStatus: status, retiredInUse: ['benefitPlans'],
    }), [wanted, status, isFeatureEnabled, verdictForPath, hasPermission, hasRole]);
  const [count, setCount] = useState<number | null>(null);
  useEffect(() => {
    if (!mayOpen) { setCount(null); return; }
    let live = true;
    benefitsApi.listPlans().then(plans => { if (live) setCount(plans.length); }).catch(() => { if (live) setCount(0); });
    return () => { live = false; };
  }, [mayOpen]);
  const inUse = mayOpen && (count ?? 0) > 0;
  return { status: !mayOpen ? 'idle' : count === null ? 'checking' : 'ready', inUse, retiredInUse: inUse ? ['benefitPlans'] : [] };
}
