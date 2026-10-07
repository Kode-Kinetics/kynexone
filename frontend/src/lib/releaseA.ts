'use client';

import { useFeatureFlags } from '../contexts/FeatureFlagContext';

/**
 * The per-tenant Release A flag (benefits by grade, contract renewals, deductions statement). Opt-in on the
 * server: an absent row means OFF, so it is reported in /api/features/disabled-keys until the platform team
 * enables it, and isFeatureEnabled returns false while flags load or fail (fail-closed).
 */
export const RELEASE_A_FLAG = 'release_a';

/** True only when the tenant has Release A switched on. */
export function useReleaseA(): boolean {
  return useFeatureFlags().isFeatureEnabled(RELEASE_A_FLAG);
}
