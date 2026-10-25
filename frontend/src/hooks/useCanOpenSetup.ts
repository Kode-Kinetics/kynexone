'use client';

import { useMemo } from 'react';
import { useAuth } from '../contexts/AuthContext';
import { useCompany } from '../contexts/CompanyContext';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { POLICY_INTAKE_FLAG } from '../lib/policyIntake';
import { setupHasEntries } from '../lib/setupAreas';

/**
 * Whether Setup has a section or tool this user may open: the one answer the menu entry, the top-bar gear and the ⌘K
 * palette read, so an employee whose own-leave permission is also a Setup permission is never offered an empty Setup.
 */
export function useCanOpenSetup(): boolean {
  const { hasPermission, hasRole } = useAuth();
  const { accountType, isGroupScope } = useCompany();
  const flags = useFeatureFlags();
  return useMemo(() => setupHasEntries({
    hasPermission, hasRole,
    isCompanyScopedGroup: accountType === 'Group' && !isGroupScope,
    policyIntake: flags.isFeatureEnabled(POLICY_INTAKE_FLAG),
    isFeatureEnabled: flags.isFeatureEnabled, verdictForPath: flags.verdictForPath, flagStatus: flags.status,
  }), [accountType, flags.isFeatureEnabled, flags.status, flags.verdictForPath, hasPermission, hasRole, isGroupScope]);
}
