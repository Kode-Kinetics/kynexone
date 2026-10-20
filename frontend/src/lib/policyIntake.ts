'use client';

import { useFeatureFlags } from '../contexts/FeatureFlagContext';

/**
 * The per-tenant policy intake flag: uploading company policy documents, preparing or extracting setup values from
 * them, the policy library and policy questions. Off for every tenant by the owner's decision (2026-10-20): only
 * manual company setup is enabled. Opt-in on the server (an absent row means OFF, so it is listed in
 * /api/features/disabled-keys), and every policy endpoint answers 403 feature_not_enabled while it is off.
 * isFeatureEnabled is fail-closed while flags load or fail, so no policy entry point flashes into view.
 */
export const POLICY_INTAKE_FLAG = 'policy_intake';

/** True only when the tenant has policy intake switched on. */
export function usePolicyIntake(): boolean {
  return useFeatureFlags().isFeatureEnabled(POLICY_INTAKE_FLAG);
}
